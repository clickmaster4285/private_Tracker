using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;
using client.Services.Streaming;

namespace client.Services;

/// <summary>
/// WebRTC publisher to the server SFU via GET /api/v1/live-stream/push (DeviceAuth signaling).
/// Captures only after the server sends {"type":"start"}; stops on {"type":"stop"}.
/// Independent of <see cref="WsClient"/> (presence) — this socket is media signaling only.
/// </summary>
public sealed class LiveStreamClient : BackgroundService
{
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly ScreenCaptureService _capture;
    private readonly NetProbeService _probe;
    private readonly ILogger<LiveStreamClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly object _sessionGate = new();
    private ClientWebSocket? _activeWs;
    private CancellationTokenSource? _sessionKill;

    public LiveStreamClient(
        ILogStore store,
        AppConfig config,
        ScreenCaptureService capture,
        NetProbeService probe,
        ILogger<LiveStreamClient> logger)
    {
        _store = store;
        _config = config;
        _capture = capture;
        _probe = probe;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("LiveStreamClient parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.StreamEnabled)
        {
            _logger.LogInformation("LiveStreamClient parked (ALPHA_STREAM_ENABLED=false)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        var backoffSec = 2;
        while (!stoppingToken.IsCancellationRequested)
        {
            EmployeeInfo? employee = null;
            try { employee = await _store.GetEmployeeInfoAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "LiveStreamClient: employee lookup failed"); }

            if (client.Core.UpdatePriority.ShouldYield || client.Core.FaceVerifyYield.ShouldYield)
            {
                _capture.SetStreamActive(false);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            if (employee is null ||
                (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            {
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            using var sessionKill = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sessionGate) { _sessionKill = sessionKill; }
            try
            {
                await RunSessionAsync(employee, sessionKill.Token);
                backoffSec = 2;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "LiveStreamClient session ended; reconnect in {Sec}s",
                    backoffSec);
            }
            finally
            {
                lock (_sessionGate) { _sessionKill = null; }
                _capture.SetStreamActive(false);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(backoffSec), stoppingToken); }
            catch (OperationCanceledException) { break; }
            backoffSec = Math.Min(30, backoffSec * 2);
        }
    }

    private async Task RunSessionAsync(EmployeeInfo employee, CancellationToken ct)
    {
        var wsUrl = BuildWsUrl(_config.ServerUrl);
        if (wsUrl is null)
        {
            _logger.LogWarning("LiveStreamClient: ALPHA_SERVER_URL missing or invalid");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return;
        }

        using var ws = new ClientWebSocket();
        lock (_sessionGate) { _activeWs = ws; }
        try
        {
        if (!string.IsNullOrWhiteSpace(employee.DeviceToken))
            ws.Options.SetRequestHeader("Authorization", $"Device {employee.DeviceToken}");
        else
            ws.Options.SetRequestHeader("Authorization", $"Bearer {employee.Token}");
        ClientIdentityHeaders.Apply(ws.Options);

        _logger.LogInformation("LiveStreamClient connecting to {Url}", wsUrl);
        await ws.ConnectAsync(new Uri(wsUrl), ct);

        await SendHelloAsync(ws, ct);

        List<RTCIceServer> iceServers = new()
        {
            new RTCIceServer { urls = "stun:stun.l.google.com:19302" },
        };

        RTCPeerConnection? pc = null;
        ScreenVp8Encoder? encoder = null;
        CancellationTokenSource? mediaCts = null;
        Task? mediaPump = null;
        // ICE candidates that arrive before the answer is applied cannot be
        // added to the peer connection (remote description not set yet) — they
        // are buffered here and flushed right after setRemoteDescription.
        var pendingIce = new List<RTCIceCandidateInit>();
        var remoteIceReady = false;
        var buffer = new byte[64 * 1024];
        var messageBuf = new MemoryStream(8 * 1024);
        // Max socket lifetime (Phase 1.8) — force reconnect so tokens/ICE stay fresh.
        using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lifetimeCts.CancelAfter(TimeSpan.FromHours(4));

        try
        {
            while (ws.State == WebSocketState.Open && !lifetimeCts.Token.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buffer, lifetimeCts.Token);
                }
                catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    _logger.LogInformation("LiveStreamClient: max socket lifetime reached — reconnecting");
                    break;
                }
                catch (WebSocketException ex)
                {
                    _logger.LogDebug(ex, "LiveStreamClient receive ended");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    messageBuf.SetLength(0);
                    continue;
                }

                messageBuf.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuf.GetBuffer(), 0, (int)messageBuf.Length);
                messageBuf.SetLength(0);

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                        continue;
                    var type = typeProp.GetString();

                    switch (type)
                    {
                        case "ice_servers":
                            iceServers = ParseIceServers(doc.RootElement);
                            break;

                        case "start":
                            _logger.LogInformation("LiveStreamClient: start capture (WebRTC)");
                            try
                            {
                                await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                                (pc, encoder, mediaCts, mediaPump) = await StartPublisherAsync(ws, iceServers, ct);
                                pendingIce.Clear();
                                remoteIceReady = false;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "LiveStreamClient: failed to start WebRTC publisher");
                                await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                                pc = null;
                                encoder = null;
                                mediaCts = null;
                                mediaPump = null;
                                _capture.SetStreamActive(false);
                                try
                                {
                                    var err = JsonSerializer.Serialize(new { type = "error", code = "publisher_start_failed" }, JsonOpts);
                                    await ws.SendAsync(Encoding.UTF8.GetBytes(err), WebSocketMessageType.Text, true, ct);
                                }
                                catch { /* ignored */ }
                            }
                            break;

                        case "stop":
                            _logger.LogInformation("LiveStreamClient: stop capture");
                            await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
                            pc = null;
                            encoder = null;
                            mediaCts = null;
                            mediaPump = null;
                            pendingIce.Clear();
                            remoteIceReady = false;
                            _capture.SetStreamActive(false);
                            break;

                        case "force_keyframe":
                            encoder?.ForceKeyFrame();
                            break;

                        case "select_monitor":
                        {
                            var idx = 0;
                            if (doc.RootElement.TryGetProperty("index", out var idxProp) &&
                                idxProp.ValueKind == JsonValueKind.Number)
                                idx = idxProp.GetInt32();
                            var applied = _capture.SetSelectedMonitor(idx);
                            _logger.LogInformation("LiveStreamClient: select_monitor → {Index}", applied);
                            await SendHelloAsync(ws, ct);
                            break;
                        }

                        case "answer":
                            if (pc is not null && doc.RootElement.TryGetProperty("sdp", out var sdpProp))
                            {
                                var sdp = sdpProp.GetString();
                                if (!string.IsNullOrEmpty(sdp))
                                {
                                    var answer = new RTCSessionDescriptionInit
                                    {
                                        type = RTCSdpType.answer,
                                        sdp = sdp,
                                    };
                                    var setResult = pc.setRemoteDescription(answer);
                                    remoteIceReady = setResult == SetDescriptionResultEnum.OK;
                                    if (!remoteIceReady)
                                        _logger.LogWarning("LiveStreamClient setRemoteDescription: {Result}", setResult);
                                    if (remoteIceReady && pendingIce.Count > 0)
                                    {
                                        foreach (var cand in pendingIce)
                                            pc.addIceCandidate(cand);
                                    }
                                    pendingIce.Clear();
                                }
                            }
                            break;

                        case "ice":
                            if (pc is not null)
                            {
                                var cand = BuildRemoteIce(doc.RootElement);
                                if (cand is not null)
                                {
                                    if (remoteIceReady)
                                        pc.addIceCandidate(cand);
                                    else
                                        pendingIce.Add(cand);
                                }
                            }
                            break;

                        case "error":
                            _logger.LogWarning("LiveStreamClient server error: {Json}", json);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LiveStreamClient: bad signaling message (keeping socket)");
                }
            }
        }
        finally
        {
            await StopMediaAsync(pc, encoder, mediaCts, mediaPump);
            _capture.SetStreamActive(false);
            if (ws.State == WebSocketState.Open)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
                catch { /* ignored */ }
            }
        }
        }
        finally
        {
            lock (_sessionGate)
            {
                if (ReferenceEquals(_activeWs, ws))
                    _activeWs = null;
            }
        }
    }

    private async Task<(RTCPeerConnection pc, ScreenVp8Encoder encoder, CancellationTokenSource mediaCts, Task mediaPump)>
        StartPublisherAsync(ClientWebSocket ws, List<RTCIceServer> iceServers, CancellationToken ct)
    {
        // Probe soft-fails: a transient HTTP/auth/timeout must not refuse publish.
        // Only a successful measurement below the floor hard-skips (thin-link guard).
        var uplink = await _probe.MeasureUplinkKbpsAsync(ct);
        int uplinkKbps;
        if (uplink is null)
        {
            uplinkKbps = Math.Min(_config.StreamMaxBitrateKbps, Math.Max(1500, _config.StreamMinUplinkKbps));
            await WriteStatusAsync("stream_skip_reason", "uplink_probe_failed_using_fallback");
            _logger.LogWarning(
                "LiveStreamClient: uplink probe failed — publishing at conservative fallback uplink={Kbps}kbps",
                uplinkKbps);
        }
        else if (uplink.Value < _config.StreamMinUplinkKbps)
        {
            await WriteStatusAsync("stream_skip_reason",
                $"uplink_below_floor measured={uplink} min={_config.StreamMinUplinkKbps}");
            _logger.LogWarning(
                "LiveStreamClient: uplink {Measured} kbps < ALPHA_STREAM_MIN_UPLINK_KBPS={Min} — skip publish",
                uplink, _config.StreamMinUplinkKbps);
            throw new InvalidOperationException(
                $"Uplink {uplink} kbps below floor {_config.StreamMinUplinkKbps} kbps");
        }
        else
        {
            uplinkKbps = uplink.Value;
            await WriteStatusAsync("stream_skip_reason", "");
        }

        var kbps = NetProbeService.SelectBitrateKbps(uplinkKbps, _config.StreamMaxBitrateKbps);
        await WriteStatusAsync("stream_uplink_kbps", uplinkKbps.ToString());
        await WriteStatusAsync("stream_selected_bitrate_kbps", kbps.ToString());

        _capture.SetMaxWidthOverride(0);
        _capture.SetStreamActive(true);

        var config = new RTCConfiguration { iceServers = iceServers };
        var pc = new RTCPeerConnection(config);
        var keyframeEvery = Math.Max(1, _config.StreamFps * Math.Max(1, _config.StreamKeyframeIntervalSec));
        var encoder = new ScreenVp8Encoder
        {
            TargetKbps = kbps,
            Fps = (uint)Math.Clamp(_config.StreamFps, 1, 30),
            KeyframeMaxDistance = (uint)keyframeEvery,
            LagInFrames = (uint)_config.StreamVp8LagFrames,
        };
        encoder.ForceKeyFrame();
        if (OperatingSystem.IsWindows())
        {
            var vpxErr = VpxNativeBootstrap.ProbeWindowsLoadError();
            if (vpxErr is not null)
            {
                _logger.LogError("LiveStreamClient VP8 native probe failed: {Err}", vpxErr);
                await WriteStatusAsync("stream_encode_error", TruncateStatus(vpxErr));
            }
            else
                await WriteStatusAsync("stream_encode_error", "");
        }
        _logger.LogInformation(
            "LiveStreamClient VP8 encode bitrate={Kbps}kbps (uplink≈{Up}kbps) fps={Fps} maxWidth={MaxW} keyframeEvery={Kf} lag={Lag} [ScreenVp8Encoder]",
            kbps, uplinkKbps, _config.StreamFps, _config.StreamMaxWidth, keyframeEvery, _config.StreamVp8LagFrames);
        var mediaCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var videoTrack = new MediaStreamTrack(
            new VideoFormat(VideoCodecsEnum.VP8, 96, 90000),
            MediaStreamStatusEnum.SendOnly);
        pc.addTrack(videoTrack);

        pc.onconnectionstatechange += (state) =>
        {
            _logger.LogInformation("LiveStreamClient PC state → {State}", state);
            _ = WriteStatusAsync("stream_pc_state", state.ToString());
            if (state == RTCPeerConnectionState.connected)
                encoder.ForceKeyFrame();
        };

        pc.oniceconnectionstatechange += (state) =>
        {
            _logger.LogInformation("LiveStreamClient ICE state → {State}", state);
            _ = WriteStatusAsync("stream_ice_state", state.ToString());
        };

        var iceReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pc.onicecandidate += async (cand) =>
        {
            if (cand is null || ws.State != WebSocketState.Open)
                return;
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    type = "ice",
                    candidate = cand.candidate,
                    sdpMid = cand.sdpMid,
                    sdpMLineIndex = cand.sdpMLineIndex,
                }, JsonOpts);
                await iceReady.Task.WaitAsync(ct);
                if (ws.State != WebSocketState.Open)
                    return;
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
            }
            catch (OperationCanceledException) { /* session ended */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LiveStreamClient ICE send failed");
            }
        };

        try
        {
            var offer = pc.createOffer(null);
            await pc.setLocalDescription(offer);

            var sdp = offer.sdp ?? pc.localDescription?.sdp?.ToString();
            if (string.IsNullOrWhiteSpace(sdp))
                throw new InvalidOperationException("WebRTC offer SDP is empty");

            var offerJson = JsonSerializer.Serialize(new { type = "offer", sdp }, JsonOpts);
            await ws.SendAsync(Encoding.UTF8.GetBytes(offerJson), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            iceReady.TrySetResult();
        }

        var mediaPump = Task.Run(() => MediaPumpAsync(pc, encoder, kbps, uplinkKbps, mediaCts.Token), mediaCts.Token);
        return (pc, encoder, mediaCts, mediaPump);
    }

    private async Task MediaPumpAsync(
        RTCPeerConnection pc,
        ScreenVp8Encoder encoder,
        uint initialKbps,
        int uplinkKbps,
        CancellationToken ct)
    {
        var fps = Math.Max(1, _config.StreamFps);
        var frameDurationRtp = 90000 / fps;
        var keyframeEvery = Math.Max(1, fps * Math.Max(1, _config.StreamKeyframeIntervalSec));
        var frameIndex = 0;
        long bytesWindow = 0;
        var windowStarted = Environment.TickCount64;
        long dropped = 0;
        long encodeMsAcc = 0;
        int encodeCount = 0;

        var currentKbps = initialKbps;
        var currentFps = fps;
        var widthTier = new[] { _config.StreamMaxWidth, 1280, 960, 640 };
        var widthTierIdx = 0;
        var slowSendStreak = 0;
        var goodSendStreak = 0;
        var lastTelemetryAt = Environment.TickCount64;
        // 0 ⇒ stall watchdog reports no_frames until the first real frame arrives
        // (Task.Delay(5s) + init-now falsely cleared stall forever on blank machines).
        var lastFrameAt = 0L;
        var waitingForIceLogged = false;
        var lastEncodeErrorAt = 0L;

        // Watchdog: if capture stops producing frames, log + sync status so we can see
        // blank-preview machines in Postgres (stream_capture_stall) without the client log.
        var stallWatch = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5000, ct);
                    var frameAge = lastFrameAt == 0
                        ? long.MaxValue
                        : Environment.TickCount64 - lastFrameAt;
                    if (frameAge < 5000)
                    {
                        await WriteStatusAsync("stream_capture_stall", "");
                        await WriteStatusAsync("stream_capture_error", "");
                        continue;
                    }
                    var captureErr = _capture.LastCaptureError;
                    var detail = string.IsNullOrEmpty(captureErr)
                        ? $"no_frames_5s produced={_capture.FramesProduced} pc={pc.connectionState}"
                        : $"no_frames_5s produced={_capture.FramesProduced} capture={captureErr} pc={pc.connectionState}";
                    _logger.LogWarning(
                        "LiveStreamClient: no screen frames for ≥5s (available={Avail}) — {Detail}",
                        _capture.StreamAvailable, detail);
                    await WriteStatusAsync("stream_capture_stall", "no_frames_5s");
                    await WriteStatusAsync("stream_capture_error", TruncateStatus(detail));
                }
            }
            catch (OperationCanceledException) { /* normal */ }
        }, ct);

        try
        {
            await foreach (var frame in _capture.Frames.ReadAllAsync(ct))
            {
                lastFrameAt = Environment.TickCount64;
                if (pc.connectionState is RTCPeerConnectionState.closed or RTCPeerConnectionState.failed)
                    break;

                byte[]? encoded = null;
                try
                {
                    if (frameIndex % keyframeEvery == 0)
                        encoder.ForceKeyFrame();
                    frameIndex++;

                    var encSw = Stopwatch.StartNew();
                    encoded = encoder.EncodeBgra(frame.Width, frame.Height, frame.Bgra);
                    encSw.Stop();
                    encodeMsAcc += encSw.ElapsedMilliseconds;
                    encodeCount++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    dropped++;
                    if (Environment.TickCount64 - lastEncodeErrorAt >= 5000)
                    {
                        lastEncodeErrorAt = Environment.TickCount64;
                        _logger.LogWarning(ex, "LiveStreamClient encode failed ({W}x{H})", frame.Width, frame.Height);
                        await WriteStatusAsync("stream_encode_error", TruncateStatus($"{ex.GetType().Name}: {ex.Message}"));
                    }
                    continue;
                }

                try
                {
                    if (encoded is { Length: > 0 })
                    {
                        // SIPSorcery: media should flow after ICE+DTLS connected.
                        // Sending while checking/failed silently drops RTP → SFU withTrack=false.
                        if (pc.connectionState != RTCPeerConnectionState.connected)
                        {
                            if (!waitingForIceLogged)
                            {
                                waitingForIceLogged = true;
                                _logger.LogWarning(
                                    "LiveStreamClient: encoded frames waiting for WebRTC connected (state={State} ice={Ice})",
                                    pc.connectionState, pc.iceConnectionState);
                                await WriteStatusAsync("stream_send_error",
                                    TruncateStatus($"waiting_pc={pc.connectionState};ice={pc.iceConnectionState}"));
                            }
                        }
                        else
                        {
                            if (waitingForIceLogged)
                            {
                                waitingForIceLogged = false;
                                encoder.ForceKeyFrame();
                                await WriteStatusAsync("stream_send_error", "");
                                _logger.LogInformation("LiveStreamClient: WebRTC connected — sending video");
                            }

                            bytesWindow += encoded.Length;
                            if (frameIndex <= 3 || frameIndex % keyframeEvery == 1)
                            {
                                _logger.LogInformation(
                                    "LiveStreamClient frame#{N} encoded {Bytes} bytes ({W}x{H})",
                                    frameIndex, encoded.Length, frame.Width, frame.Height);
                            }

                            var sendSw = Stopwatch.StartNew();
                            pc.SendVideo((uint)frameDurationRtp, encoded);
                            // Dev/QA: inject backpressure so ABR can be proven without NIC tools.
                            if (_config.StreamDebugSendDelayMs > 0)
                                await Task.Delay(_config.StreamDebugSendDelayMs, ct);
                            sendSw.Stop();

                            // Adaptive degradation on send backpressure (bitrate → resolution → fps).
                            if (sendSw.ElapsedMilliseconds > 80)
                            {
                                slowSendStreak++;
                                goodSendStreak = 0;
                                if (slowSendStreak >= 5)
                                {
                                    slowSendStreak = 0;
                                    if (currentKbps > 800)
                                    {
                                        currentKbps = Math.Max(500, currentKbps * 3 / 4);
                                        encoder.TargetKbps = currentKbps;
                                        _logger.LogInformation("LiveStreamClient degrade bitrate → {Kbps}kbps", currentKbps);
                                    }
                                    else if (widthTierIdx < widthTier.Length - 1)
                                    {
                                        widthTierIdx++;
                                        _capture.SetMaxWidthOverride(widthTier[widthTierIdx]);
                                        _logger.LogInformation("LiveStreamClient degrade resolution maxWidth → {W}", widthTier[widthTierIdx]);
                                    }
                                    else if (currentFps > 8)
                                    {
                                        currentFps = Math.Max(8, currentFps - 2);
                                        encoder.Fps = (uint)currentFps;
                                        frameDurationRtp = 90000 / currentFps;
                                        keyframeEvery = Math.Max(1, currentFps * Math.Max(1, _config.StreamKeyframeIntervalSec));
                                        _logger.LogInformation("LiveStreamClient degrade fps → {Fps}", currentFps);
                                    }
                                }
                            }
                            else if (sendSw.ElapsedMilliseconds < 20)
                            {
                                goodSendStreak++;
                                slowSendStreak = 0;
                                // Slow recovery toward ladder target.
                                if (goodSendStreak >= 60)
                                {
                                    goodSendStreak = 0;
                                    var target = NetProbeService.SelectBitrateKbps(uplinkKbps, _config.StreamMaxBitrateKbps);
                                    if (currentFps < fps)
                                    {
                                        currentFps = Math.Min(fps, currentFps + 1);
                                        encoder.Fps = (uint)currentFps;
                                        frameDurationRtp = 90000 / currentFps;
                                        keyframeEvery = Math.Max(1, currentFps * Math.Max(1, _config.StreamKeyframeIntervalSec));
                                    }
                                    else if (widthTierIdx > 0)
                                    {
                                        widthTierIdx--;
                                        if (widthTierIdx == 0)
                                            _capture.SetMaxWidthOverride(0);
                                        else
                                            _capture.SetMaxWidthOverride(widthTier[widthTierIdx]);
                                    }
                                    else if (currentKbps < target)
                                    {
                                        currentKbps = Math.Min(target, currentKbps + Math.Max(250, currentKbps / 10));
                                        encoder.TargetKbps = currentKbps;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        dropped++;
                    }

                    var elapsed = Environment.TickCount64 - windowStarted;
                    if (elapsed >= 5000)
                    {
                        var kbpsOut = bytesWindow * 8.0 / elapsed;
                        _logger.LogInformation(
                            "LiveStreamClient encode stats: ~{Kbps:F0}kbps out, target={Target}kbps, frame={W}x{H}, frames={N}, drops={D}, encodeAvgMs={Enc:F1}, pc={Pc}",
                            kbpsOut, currentKbps, frame.Width, frame.Height, frameIndex, dropped,
                            encodeCount > 0 ? encodeMsAcc / (double)encodeCount : 0,
                            pc.connectionState);
                        bytesWindow = 0;
                        windowStarted = Environment.TickCount64;
                    }

                    if (Environment.TickCount64 - lastTelemetryAt >= 10_000)
                    {
                        lastTelemetryAt = Environment.TickCount64;
                        var sendKbps = bytesWindow > 0 && elapsed > 0
                            ? (int)(bytesWindow * 8.0 / Math.Max(1, Environment.TickCount64 - windowStarted + elapsed))
                            : (int)(pc.connectionState == RTCPeerConnectionState.connected ? currentKbps : 0);
                        await WriteStatusAsync("stream_selected_bitrate_kbps", currentKbps.ToString());
                        await WriteStatusAsync("stream_send_kbps", sendKbps.ToString());
                        await WriteStatusAsync("stream_encode_ms",
                            encodeCount > 0 ? ((int)(encodeMsAcc / encodeCount)).ToString() : "0");
                        await WriteStatusAsync("stream_dropped_frames", dropped.ToString());
                        await WriteStatusAsync("stream_pc_state", pc.connectionState.ToString());
                        await WriteStatusAsync("stream_ice_state", pc.iceConnectionState.ToString());
                        encodeMsAcc = 0;
                        encodeCount = 0;
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    dropped++;
                    if (Environment.TickCount64 - lastEncodeErrorAt >= 5000)
                    {
                        lastEncodeErrorAt = Environment.TickCount64;
                        _logger.LogWarning(ex, "LiveStreamClient send failed");
                        await WriteStatusAsync("stream_send_error", TruncateStatus($"{ex.GetType().Name}: {ex.Message}"));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try { await stallWatch; } catch { /* cancelled */ }
        }
    }

    private static string TruncateStatus(string value) =>
        value.Length <= 180 ? value : value[..177] + "...";

    private async Task WriteStatusAsync(string key, string value)
    {
        try { await _store.SetStatusAsync(key, value, CancellationToken.None); }
        catch (Exception ex) { _logger.LogDebug(ex, "LiveStreamClient status write {Key} failed", key); }
    }

    private async Task StopMediaAsync(
        RTCPeerConnection? pc,
        ScreenVp8Encoder? encoder,
        CancellationTokenSource? mediaCts,
        Task? mediaPump)
    {
        try { mediaCts?.Cancel(); } catch { /* ignored */ }
        if (mediaPump is not null)
        {
            try { await mediaPump; } catch { /* ignored */ }
        }
        try { mediaCts?.Dispose(); } catch { /* ignored */ }
        try { encoder?.Dispose(); } catch { /* ignored */ }
        try { pc?.close(); } catch { /* ignored */ }
        try { pc?.Dispose(); } catch { /* ignored */ }
        _capture.SetMaxWidthOverride(0);
    }

    private async Task SendHelloAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var monitors = _capture.GetMonitors();
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            platform = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux() ? "linux" : "macos",
            streamAvailable = _capture.StreamAvailable,
            version = AppInfo.Version,
            selectedMonitor = _capture.SelectedMonitorIndex,
            monitors = monitors.Select(m => new
            {
                index = m.Index,
                name = m.Name,
                width = m.Width,
                height = m.Height,
                isPrimary = m.IsPrimary,
            }).ToArray(),
        }, JsonOpts);
        await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);
    }

    private static List<RTCIceServer> ParseIceServers(JsonElement root)
    {
        var list = new List<RTCIceServer>();
        if (!root.TryGetProperty("iceServers", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            list.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
            return list;
        }

        foreach (var el in arr.EnumerateArray())
        {
            var urls = new List<string>();
            if (el.TryGetProperty("urls", out var urlsEl))
            {
                if (urlsEl.ValueKind == JsonValueKind.String)
                    urls.Add(urlsEl.GetString()!);
                else if (urlsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var u in urlsEl.EnumerateArray())
                    {
                        var s = u.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            urls.Add(s);
                    }
                }
            }
            if (urls.Count == 0)
                continue;

            var server = new RTCIceServer { urls = string.Join(",", urls) };
            if (el.TryGetProperty("username", out var user) && user.ValueKind == JsonValueKind.String)
                server.username = user.GetString();
            if (el.TryGetProperty("credential", out var cred) && cred.ValueKind == JsonValueKind.String)
                server.credential = cred.GetString();
            list.Add(server);
        }

        if (list.Count == 0)
            list.Add(new RTCIceServer { urls = "stun:stun.l.google.com:19302" });
        return list;
    }

    private static RTCIceCandidateInit? BuildRemoteIce(JsonElement root)
    {
        if (!root.TryGetProperty("candidate", out var candProp))
            return null;
        var candidate = candProp.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var init = new RTCIceCandidateInit { candidate = candidate };
        if (root.TryGetProperty("sdpMid", out var mid) && mid.ValueKind == JsonValueKind.String)
            init.sdpMid = mid.GetString();
        if (root.TryGetProperty("sdpMLineIndex", out var mline) && mline.ValueKind == JsonValueKind.Number)
            init.sdpMLineIndex = (ushort)mline.GetInt32();

        return init;
    }

    private static string? BuildWsUrl(string? serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return null;
        if (!Uri.TryCreate(serverUrl.TrimEnd('/'), UriKind.Absolute, out var uri))
            return null;
        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/api/v1/live-stream/push",
            Query = string.Empty,
        };
        return builder.Uri.ToString();
    }
}
