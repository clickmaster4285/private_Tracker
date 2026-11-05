using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.Models;

namespace client.Services;

/// <summary>
/// Long-lived outbound WebSocket to GET /api/v1/ws (DeviceAuth) — presence /
/// keep-alive / control channel. Independent of <see cref="LiveStreamClient"/>
/// (no capture, no frames). Gated on <see cref="SyncService"/> sync-success
/// (sticky 2xx) with a timeout fallback so idle employees are never stranded.
/// </summary>
public sealed class WsClient : BackgroundService
{
    private readonly ILogStore _store;
    private readonly AppConfig _config;
    private readonly SyncService _sync;
    private readonly ILogger<WsClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly object _sessionGate = new();
    private ClientWebSocket? _activeWs;
    private CancellationTokenSource? _sessionKill;
    private int _paused;

    public WsClient(
        ILogStore store,
        AppConfig config,
        SyncService sync,
        ILogger<WsClient> logger)
    {
        _store = store;
        _config = config;
        _sync = sync;
        _logger = logger;
    }

    /// <summary>Roaming Stop: abort the presence WebSocket and do not reconnect until Resume.</summary>
    public void ForceDisconnect()
    {
        Interlocked.Exchange(ref _paused, 1);
        CancellationTokenSource? kill;
        ClientWebSocket? ws;
        lock (_sessionGate)
        {
            kill = _sessionKill;
            ws = _activeWs;
        }
        try { kill?.Cancel(); } catch { /* ignore */ }
        try { ws?.Abort(); } catch { /* ignore */ }
        _logger.LogInformation("WsClient force-disconnected (Roaming Stop)");
    }

    /// <summary>Roaming Start: allow presence reconnect.</summary>
    public void ResumeConnections()
    {
        Interlocked.Exchange(ref _paused, 0);
        _logger.LogInformation("WsClient resume allowed (Roaming Start)");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (client.Core.ClientProcessMode.ParkTrackingWork)
        {
            _logger.LogInformation("WsClient parked (GUI agent)");
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        if (!_config.WsEnabled)
        {
            _logger.LogInformation("WsClient parked (ALPHA_WS_ENABLED=false)");
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }
            return;
        }

        var backoffSec = Math.Max(1, _config.WsReconnectBaseSec);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Volatile.Read(ref _paused) != 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            EmployeeInfo? employee = null;
            try
            {
                employee = await _store.GetEmployeeInfoAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WsClient: employee lookup failed");
            }

            if (employee is null ||
                (string.IsNullOrWhiteSpace(employee.DeviceToken) && string.IsNullOrWhiteSpace(employee.Token)))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            // Primary gate: wait briefly for a proven sync 2xx. Idle employees
            // (nothing to sync) time out fast and fall through — presence must
            // come up within seconds of login, not after a full sync interval.
            if (!_sync.IsServerReachable)
            {
                var wait = TimeSpan.FromSeconds(8);
                _logger.LogDebug("WsClient waiting up to {Sec}s for SyncService reachability", wait.TotalSeconds);
                try
                {
                    await _sync.WaitUntilServerReachableAsync(wait, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }

            using var sessionKill = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_sessionGate) { _sessionKill = sessionKill; }
            try
            {
                await RunSessionAsync(employee, sessionKill.Token);
                backoffSec = Math.Max(1, _config.WsReconnectBaseSec);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _paused) != 0)
            {
                // ForceDisconnect — stay paused until ResumeConnections.
                backoffSec = Math.Max(1, _config.WsReconnectBaseSec);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WsClient session ended; reconnect in {Sec}s", backoffSec);
            }
            finally
            {
                lock (_sessionGate) { _sessionKill = null; }
            }

            if (Volatile.Read(ref _paused) != 0)
                continue;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSec), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            backoffSec = Math.Min(60, backoffSec * 2);
        }
    }

    private async Task RunSessionAsync(EmployeeInfo employee, CancellationToken ct)
    {
        var wsUrl = BuildWsUrl(_config.ServerUrl);
        if (wsUrl is null)
        {
            _logger.LogWarning("WsClient: ALPHA_SERVER_URL missing or invalid");
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

            _logger.LogInformation("WsClient connecting to {Url}", wsUrl);
            await ws.ConnectAsync(new Uri(wsUrl), ct);

            var hello = JsonSerializer.Serialize(new
            {
                type = "hello",
                platform = OperatingSystem.IsWindows() ? "windows"
                    : OperatingSystem.IsLinux() ? "linux" : "macos",
                version = AppInfo.Version,
            }, JsonOpts);
            await ws.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, ct);

            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pingPump = Task.Run(() => PingLoopAsync(ws, sessionCts.Token), sessionCts.Token);

            var buffer = new byte[16 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    // Keep-alive / welcome / pong — ignore payload content (control channel).
                    if (result.MessageType == WebSocketMessageType.Text)
                        _logger.LogDebug("WsClient recv: {Json}", Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            }
            finally
            {
                sessionCts.Cancel();
                try { await pingPump; } catch { /* ignored */ }
                if (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
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

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _config.WsPingSec));
        var pingBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "ping" }, JsonOpts));
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(interval, ct);
                if (ws.State != WebSocketState.Open)
                    break;
                await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "WsClient ping failed");
        }
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
            Path = "/api/v1/ws",
            Query = string.Empty,
        };
        return builder.Uri.ToString();
    }
}
