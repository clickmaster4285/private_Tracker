using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// IPC host: Windows named pipe (\\.\pipe\&lt;WINDOWS_DLP_PIPE_NAME&gt;) or Linux
/// Unix socket (<c>/var/lib/&lt;pkg&gt;/dlp/dlp.sock</c>).
/// Ops: GetPolicyVersion, GetEnforceStatus, Health, AuthorizeTeardown, ApplyTeardownGate,
/// GetUpdateStatus, RequestUpdateCheck, RequestPolicyPull, EnsureAvSelfProtection.
/// Always listens so update/teardown IPC works even when
/// <c>ALPHA_DLP_ENABLED=false</c> (DLP-only ops then return <c>dlp_disabled</c>).
/// </summary>
public sealed class NamedPipeHost : BackgroundService
{
    /// <summary>Hard cap for service→server removal-verify (never inherit the 60s dlp client).</summary>
    private static readonly TimeSpan VerifyHttpBudget = TimeSpan.FromSeconds(10);

    private readonly DlpConfig _cfg;
    private readonly PolicyStore _store;
    private readonly WebBlockState _webBlock;
    private readonly SilentUpdateState _updateState;
    private readonly SilentUpdateWorker _updateWorker;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NamedPipeHost> _logger;

    public NamedPipeHost(
        DlpConfig cfg,
        PolicyStore store,
        WebBlockState webBlock,
        SilentUpdateState updateState,
        SilentUpdateWorker updateWorker,
        IHttpClientFactory httpFactory,
        IHostApplicationLifetime lifetime,
        ILogger<NamedPipeHost> logger)
    {
        _cfg = cfg;
        _store = store;
        _webBlock = webBlock;
        _updateState = updateState;
        _updateWorker = updateWorker;
        _httpFactory = httpFactory;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (OperatingSystem.IsLinux())
        {
            await ExecuteUnixSocketAsync(stoppingToken);
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            _logger.LogInformation("NamedPipeHost parked (unsupported OS)");
            return;
        }

        // Listen before the database opens. A locked dlp.db used to keep this
        // process Running in Services while the pipe did not exist, and the
        // tracker reported "service is not running."
        if (_cfg.Enabled)
            _ = InitializeStoreInBackground(stoppingToken);
        else
            _logger.LogInformation(
                "NamedPipeHost listening for update IPC (ALPHA_DLP_ENABLED=false)");

        _logger.LogInformation("Named pipe listening on {Pipe}", _cfg.PipeName);

        // Concurrent accept: a stuck HandleClientAsync (abandoned CLI write, slow
        // AuthorizeTeardown HTTP) must not block ApplyTeardownGate / Health.
        const int maxConcurrent = 4;
        using var slot = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slot.WaitAsync(stoppingToken);
                // LocalSystem default pipe ACL denies interactive users — CLI
                // AuthorizeTeardown / ApplyTeardownGate would get Access Denied.
                var server = CreatePipeServer(_cfg.PipeName, maxConcurrent);
                // LongRunning: AppLocker PowerShell must not starve the shared pool used by
                // GetEnforceStatus while Set-AppLockerPolicy runs on EnforceWorker.
                _ = Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        await server.WaitForConnectionAsync(stoppingToken);
                        await HandleClientAsync(server, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        /* shut down */
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Named pipe accept/handle error");
                    }
                    finally
                    {
                        try { await server.DisposeAsync(); } catch { /* ignore */ }
                        try { slot.Release(); } catch { /* ignore */ }
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Named pipe accept loop error on {Pipe}", _cfg.PipeName);
                try { slot.Release(); } catch { /* ignore */ }
                try { await Task.Delay(500, stoppingToken); } catch { /* ignore */ }
            }
        }
    }

    private async Task InitializeStoreInBackground(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var openBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                openBudget.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    await _store.InitializeAsync(openBudget.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning("Policy store did not open within 15s; pipe stays up");
                    EnforceStatus.Shared.NoteStoreFailed("dlp.db did not open within 15 seconds");
                    await _store.InitializeAsync(ct);
                }
                _logger.LogInformation("Policy store ready");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Policy store init failed; pipe stays open and will retry");
                EnforceStatus.Shared.NoteStoreFailed(ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName, int maxInstances)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, maxInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        var security = new PipeSecurity();
        // SYSTEM (service) + Administrators + interactive users (tracker CLI).
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        // Synchronize is required. Without it CreateFile on the pipe returns
        // access denied even though the service process is running.
        var clientRights = PipeAccessRights.ReadWrite
            | PipeAccessRights.CreateNewInstance
            | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            clientRights,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            clientRights,
            AccessControlType.Allow));

        // A full request line must fit without the server already reading.
        // A zero buffer makes the client's write wait forever (write_timeout).
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            security);
    }

    private async Task ExecuteUnixSocketAsync(CancellationToken stoppingToken)
    {
        if (_cfg.Enabled)
            await _store.InitializeAsync(stoppingToken);
        else
            _logger.LogInformation(
                "Unix socket IPC listening (ALPHA_DLP_ENABLED=false)");

        var sockPath = Environment.GetEnvironmentVariable("ALPHA_DLP_SOCKET") is { Length: > 0 } s
            ? s
            : Path.Combine(_cfg.DataDir, "dlp.sock");
        Directory.CreateDirectory(Path.GetDirectoryName(sockPath)!);
        try { if (File.Exists(sockPath)) File.Delete(sockPath); } catch { /* ignore */ }

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(sockPath));
        listener.Listen(8);
        try
        {
            // World-writable socket so the user tracker can talk to the root daemon.
            // Auth is still the DeviceAuth token / removal code on the wire.
            File.SetUnixFileMode(sockPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        }
        catch { /* best-effort */ }

        _logger.LogInformation("Unix DLP socket listening on {Path}", sockPath);

        const int maxConcurrent = 4;
        using var slot = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slot.WaitAsync(stoppingToken);
                var client = await listener.AcceptAsync(stoppingToken);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = new NetworkStream(client, ownsSocket: true);
                        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                        using var reader = new StreamReader(stream, utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                        await using var writer = new StreamWriter(stream, utf8, leaveOpen: true)
                        {
                            AutoFlush = true,
                        };
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        readCts.CancelAfter(TimeSpan.FromSeconds(20));
                        string? line;
                        try { line = await reader.ReadLineAsync(readCts.Token); }
                        catch (OperationCanceledException) { return; }
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var response = await DispatchLineAsync(line, pipeServer: null, stoppingToken);
                        await writer.WriteLineAsync(response.AsMemory(), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        /* shut down */
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Unix socket accept/handle error");
                    }
                    finally
                    {
                        try { slot.Release(); } catch { /* ignore */ }
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Unix socket accept loop error");
                try { slot.Release(); } catch { /* ignore */ }
                try { await Task.Delay(500, stoppingToken); } catch { /* ignore */ }
            }
        }

        try { if (File.Exists(sockPath)) File.Delete(sockPath); } catch { /* ignore */ }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        // Bound read so an abandoned client cannot hold a pipe instance forever.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(20));
        string? line;
        try
        {
            line = await ReadPipeLineAsync(server, readCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(line)) return;

        var response = await DispatchLineAsync(line, server, ct);
        var bytes = Encoding.UTF8.GetBytes(response + "\n");
        try
        {
            // Synchronous write fills the outbound buffer. WriteAsync here deadlocks
            // with the tracker's read the same way the client write used to.
            server.Write(bytes, 0, bytes.Length);
            server.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            /* client gave up */
        }
    }

    private static async Task<string?> ReadPipeLineAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(256);
        while (sb.Length < 64_000)
        {
            var n = await server.ReadAsync(buffer.AsMemory(0, 1), ct);
            if (n <= 0)
                return sb.Length > 0 ? sb.ToString() : null;
            var ch = (char)buffer[0];
            if (ch == '\n')
                return sb.ToString().TrimEnd('\r');
            if (ch != '\r')
                sb.Append(ch);
        }
        return sb.ToString();
    }

    private async Task<string> DispatchLineAsync(
        string line, NamedPipeServerStream? pipeServer, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var op = doc.RootElement.TryGetProperty("op", out var o) ? o.GetString() : "";
            return op switch
            {
                "GetUpdateStatus" => JsonSerializer.Serialize(_updateState.Snapshot()),
                "RequestUpdateCheck" => RequestUpdateCheckJson(),
                "RequestPolicyPull" => RequireDlpSync(RequestPolicyPullJson),
                // Independent of ALPHA_DLP_ENABLED: the soft-lock calls this when Defender
                // blocks loading the product's own natives (onnxruntime.dll).
                "EnsureAvSelfProtection" => await EnsureAvSelfProtectionJson(ct),
                "GetPolicyVersion" => await RequireDlp(() => GetPolicyVersionJson(ct)),
                "GetEnforceStatus" => RequireDlpSync(GetEnforceStatusJson),
                "Health" => await HealthJson(ct),
                "GetWebBlockStatus" => await RequireDlp(() => GetWebBlockStatusJson(ct)),
                // Teardown unlock must work even when ALPHA_DLP_ENABLED=false (pipe still
                // listens for update IPC). RequireDlp would return dlp_disabled and leave
                // AppLocker / USB / hosts stuck after --protected-uninstall.
                "AuthorizeTeardown" => await AuthorizeTeardownJson(doc.RootElement, ct),
                "ApplyTeardownGate" => ApplyTeardownGateJson(doc.RootElement),
                _ => JsonSerializer.Serialize(new { ok = false, error = "unknown_op" }),
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Server pushed "policy_changed", or the policy-gate Retry button. Pull now and
    /// wake Enforce so blockers re-apply (TCP-kill CDN sockets) even when the
    /// published version is unchanged.
    /// </summary>
    private static string RequestPolicyPullJson()
    {
        DlpSignals.RequestPull();
        DlpSignals.RequestEnforce();
        return JsonSerializer.Serialize(new { ok = true, queued = true });
    }

    private async Task<string> EnsureAvSelfProtectionJson(CancellationToken ct)
    {
        if (!_cfg.AvEnforce)
            return JsonSerializer.Serialize(new { ok = false, error = "av_enforce_disabled" });
        var r = await AvSelfProtection.RunAsync(force: false, ct);
        if (r.Added.Count > 0 || r.Restored.Count > 0)
        {
            _logger.LogWarning("Pipe EnsureAvSelfProtection: added={Added} restored={Restored}",
                string.Join(", ", r.Added), string.Join(", ", r.Restored));
        }
        return JsonSerializer.Serialize(new
        {
            ok = r.Ok,
            added = r.Added,
            restored = r.Restored,
            errors = r.Errors,
        });
    }

    private string RequestUpdateCheckJson()
    {
        if (!_cfg.UpdateEnabled)
            return JsonSerializer.Serialize(new { ok = false, error = "update_disabled" });
        _updateWorker.RequestImmediateCheck();
        return JsonSerializer.Serialize(new { ok = true, queued = true });
    }

    private async Task<string> RequireDlp(Func<Task<string>> action)
    {
        if (!_cfg.Enabled)
            return JsonSerializer.Serialize(new { ok = false, error = "dlp_disabled" });
        return await action();
    }

    private string RequireDlpSync(Func<string> action)
    {
        if (!_cfg.Enabled)
            return JsonSerializer.Serialize(new { ok = false, error = "dlp_disabled" });
        return action();
    }

    private static string GetEnforceStatusJson() => EnforceStatus.Shared.ToJson();

    private async Task<string> GetPolicyVersionJson(CancellationToken ct)
    {
        var (version, hash, _) = await _store.GetAppliedAsync(ct);
        return JsonSerializer.Serialize(new { ok = true, policyVersion = version, hash });
    }

    private async Task<string> HealthJson(CancellationToken ct)
    {
        long version = 0;
        if (_cfg.Enabled)
        {
            try
            {
                (version, _, _) = await _store.GetAppliedAsync(ct);
            }
            catch { /* store may be uninitialized when DLP off */ }
        }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            enabled = _cfg.Enabled,
            updateEnabled = _cfg.UpdateEnabled,
            policyVersion = version,
            usbBlock = _cfg.UsbBlock,
            webBlock = _cfg.WebBlock,
            networkLockdown = _cfg.NetworkLockdown,
            appBlock = _cfg.AppBlock,
            deleteBlock = _cfg.DeleteBlock,
            fileTransferBlock = _cfg.FileTransferBlock,
            startupBlock = _cfg.StartupBlock,
            loginReboot = _cfg.LoginReboot,
            avEnforce = _cfg.AvEnforce,
            update = _updateState.Snapshot(),
        });
    }

    private async Task<string> GetWebBlockStatusJson(CancellationToken ct)
    {
        // Rehydrate if EnforceWorker has not published yet (or fingerprint short-circuit).
        if (_webBlock.Domains.Count == 0 && _cfg.WebBlock && _cfg.Enabled)
        {
            try
            {
                var (ver, _, bundleJson) = await _store.GetAppliedAsync(ct);
                var bundle = PolicyBundleParser.Parse(bundleJson);
                var domains = PolicyBundleParser.ActiveWebBlockDomains(bundle, DateTimeOffset.UtcNow);
                if (domains.Count > 0)
                    _webBlock.Set(ver, domains, DomainBlockEnforcer.CurrentGeneration);
            }
            catch { /* keep empty */ }
        }

        var (version, liveDomains, updatedAt, generation) = _webBlock.Snapshot();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            policyVersion = version,
            domains = liveDomains,
            generation,
            updatedAt = updatedAt == DateTimeOffset.MinValue ? (string?)null : updatedAt.ToString("o"),
        });
    }

    private async Task<string> AuthorizeTeardownJson(JsonElement root, CancellationToken ct)
    {
        var code = root.TryGetProperty("code", out var c) ? (c.GetString() ?? "").Trim() : "";
        var purpose = root.TryGetProperty("purpose", out var p) ? (p.GetString() ?? "both").Trim() : "both";
        if (string.IsNullOrEmpty(code))
            return JsonSerializer.Serialize(new { ok = false, error = "code_required" });

        _cfg.RefreshCredentials();
        if (string.IsNullOrWhiteSpace(_cfg.ServerUrl) || string.IsNullOrWhiteSpace(_cfg.DeviceToken))
            return JsonSerializer.Serialize(new { ok = false, error = "credentials_missing" });

        using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        verifyCts.CancelAfter(VerifyHttpBudget);

        try
        {
            var client = _httpFactory.CreateClient("dlp");
            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"{_cfg.ServerUrl}/api/v1/devices/removal-verify")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { code, purpose }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Device", _cfg.DeviceToken);
            req.Headers.TryAddWithoutValidation("X-Client-Platform", "windows");

            using var resp = await client.SendAsync(req, verifyCts.Token);
            var body = await resp.Content.ReadAsStringAsync(verifyCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AuthorizeTeardown verify failed HTTP {Status}: {Body}",
                    (int)resp.StatusCode, body);
                return JsonSerializer.Serialize(new { ok = false, error = "invalid_code", status = (int)resp.StatusCode });
            }

            var valid = false;
            var storedPurpose = purpose;
            try
            {
                using var doc = JsonDocument.Parse(body);
                valid = doc.RootElement.TryGetProperty("valid", out var v) && v.GetBoolean();
                if (doc.RootElement.TryGetProperty("purpose", out var sp))
                    storedPurpose = sp.GetString() ?? storedPurpose;
            }
            catch { /* treat as invalid */ }

            if (!valid)
                return JsonSerializer.Serialize(new { ok = false, error = "invalid_code" });

            return CompleteTeardown(storedPurpose);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("AuthorizeTeardown removal-verify timed out after {Sec}s",
                VerifyHttpBudget.TotalSeconds);
            return JsonSerializer.Serialize(new { ok = false, error = "verify_timeout" });
        }
    }

    /// <summary>
    /// CLI already verified via DeviceAuth and wrote teardown.ok — unlock ACL + stop
    /// without consuming another removal code.
    /// </summary>
    private string ApplyTeardownGateJson(JsonElement root)
    {
        var purpose = root.TryGetProperty("purpose", out var p) ? (p.GetString() ?? "both").Trim() : "both";
        var gate = TeardownGate.PathIn(_cfg.DataDir);
        if (!File.Exists(gate))
            return JsonSerializer.Serialize(new { ok = false, error = "teardown_ok_missing" });

        try
        {
            var text = File.ReadAllText(gate);
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("purpose=", StringComparison.OrdinalIgnoreCase))
                    purpose = line["purpose=".Length..].Trim();
            }
        }
        catch { /* keep request purpose */ }

        return CompleteTeardown(purpose);
    }

    private string CompleteTeardown(string purpose)
    {
        TeardownGate.MarkAuthorized(_cfg.DataDir, purpose);

        // CRITICAL: return ok=true on the pipe FIRST. USBSTOR + sdset can take
        // seconds (or hang). Doing that before WriteLineAsync deadlocked the CLI:
        // client hit a read timeout, Dispose waited on the service, service held the
        // pipe while blocked in sc.exe — protected-uninstall froze on
        // "Unlocking DLP service..." and Apps & features then hit Error 32 (files locked).
        //
        // On Windows: clear AppLocker FIRST so the CLI's follow-up cleanup (and any
        // SYSTEM helper) can spawn sc/taskkill/schtasks if native paths still need them.
        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var apps = AppAllowEnforcer.Clear();
                    _logger.LogWarning(
                        "Teardown cleared AppLocker first (changed={Changed}, detail={Detail})",
                        apps.Changed, apps.Detail);
                    var del = DeleteBlockAclEnforcer.Clear();
                    _logger.LogWarning(
                        "Teardown cleared delete-block ACLs (changed={Changed}, detail={Detail})",
                        del.Changed, del.Detail);
                    var ft = FileTransferEnforcer.Clear();
                    _logger.LogWarning(
                        "Teardown cleared file-transfer browser policies (changed={Changed}, detail={Detail})",
                        ft.Changed, ft.Detail);
                    var su = StartupAppEnforcer.Clear();
                    _logger.LogWarning(
                        "Teardown restored startup apps (changed={Changed}, detail={Detail})",
                        su.Changed, su.Detail);
                    ServiceAclHardener.TryApplyOpen(_logger);
                }

                if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                {
                    var usb = UsbStorEnforcer.Apply(block: false);
                    _logger.LogWarning(
                        "Teardown restored USB (changed={Changed}, detail={Detail}, start={Start})",
                        usb.Changed, usb.Detail, usb.DesiredStart);
                    var web = DomainBlockEnforcer.Clear();
                    _webBlock.Set(0, Array.Empty<string>(), DomainBlockEnforcer.CurrentGeneration);
                    _logger.LogWarning(
                        "Teardown cleared AlphaAI-DLP hosts/FW|nft/PIN (changed={Changed}, detail={Detail})",
                        web.Changed, web.Detail);
                }

                _logger.LogWarning(
                    "Authorized DLP service teardown (purpose={Purpose}) — stopping service",
                    purpose);
                Thread.Sleep(300);
                _lifetime.StopApplication();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-reply teardown work failed");
                try { _lifetime.StopApplication(); } catch { /* ignore */ }
            }
        });

        return JsonSerializer.Serialize(new { ok = true, purpose, stopping = true });
    }
}
