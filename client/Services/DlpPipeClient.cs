using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using client.Core;

namespace client.Services;

/// <summary>
/// Client for the privileged DLP daemon IPC:
/// Windows named pipe (APP_IDENTIFIERS WINDOWS_DLP_PIPE_NAME) or Linux Unix socket
/// (<c>/var/lib/&lt;package&gt;/dlp/dlp.sock</c>).
/// Hard-bounded: never await a hung Dispose. Abandoned connections are closed on a worker
/// thread; the CLI continues teardown regardless.
/// </summary>
public static class DlpPipeClient
{
    public static string DefaultPipeName => AppInfo.WindowsDlpPipeName;

    public static string DefaultUnixSocketPath
    {
        get
        {
            if (Environment.GetEnvironmentVariable("ALPHA_DLP_SOCKET") is { Length: > 0 } s)
                return s;
            if (Environment.GetEnvironmentVariable("ALPHA_DLP_DATA_DIR") is { Length: > 0 } d)
                return Path.Combine(d, "dlp.sock");
            return Path.Combine("/var/lib", AppInfo.PackageName, "dlp", "dlp.sock");
        }
    }

    public const int DefaultConnectTimeoutMs = 2_000;
    public const int DefaultResponseTimeoutMs = 12_000;
    /// <summary>Hard ceiling for ApplyTeardownGate including any stuck dispose.</summary>
    public const int ApplyTeardownOverallTimeoutMs = 25_000;
    /// <summary>Per-write/read budget for ApplyTeardownGate (service may be busy).</summary>
    public const int ApplyTeardownResponseTimeoutMs = 15_000;

    public sealed record PipeCallResult(bool Connected, bool TimedOut, string Body, string? Error);

    public static async Task<(bool Ok, string Body)> SendAsync(
        object request,
        string? pipeName = null,
        int connectTimeoutMs = DefaultConnectTimeoutMs,
        int responseTimeoutMs = DefaultResponseTimeoutMs,
        CancellationToken ct = default)
    {
        var r = await SendDetailedAsync(request, pipeName, connectTimeoutMs, responseTimeoutMs, ct)
            .ConfigureAwait(false);
        return (r.Connected && !r.TimedOut, r.Body);
    }

    public static async Task<PipeCallResult> SendDetailedAsync(
        object request,
        string? pipeName = null,
        int connectTimeoutMs = DefaultConnectTimeoutMs,
        int responseTimeoutMs = DefaultResponseTimeoutMs,
        CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
            return await SendUnixDetailedAsync(request, connectTimeoutMs, responseTimeoutMs, ct)
                .ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
            return new PipeCallResult(false, false,
                "{\"ok\":false,\"error\":\"unsupported_os\"}", "unsupported_os");

        var name = string.IsNullOrWhiteSpace(pipeName)
            ? Environment.GetEnvironmentVariable("ALPHA_DLP_PIPE_NAME")
            : pipeName;
        if (string.IsNullOrWhiteSpace(name))
            name = DefaultPipeName;

        NamedPipeClientStream? client = null;
        try
        {
            client = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

            var connectMs = Math.Clamp(connectTimeoutMs, 500, 10_000);
            var connectTask = client.ConnectAsync(CancellationToken.None);
            var connectDone = await Task.WhenAny(connectTask, Task.Delay(connectMs, CancellationToken.None))
                .ConfigureAwait(false);
            if (connectDone != connectTask)
            {
                AbandonPipe(ref client);
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"connect_timeout\"}", "connect_timeout");
            }

            try { await connectTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AbandonPipe(ref client);
                return new PipeCallResult(false, false,
                    JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                    ex.Message);
            }

            var responseMs = Math.Clamp(responseTimeoutMs, 1_000, 30_000);
            var payload = JsonSerializer.Serialize(request) + "\n";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);

            // Write without StreamWriter (its Dispose can also hang on a stuck pipe).
            var writeTask = client.WriteAsync(payloadBytes, 0, payloadBytes.Length, CancellationToken.None);
            var writeDone = await Task.WhenAny(writeTask, Task.Delay(responseMs, CancellationToken.None))
                .ConfigureAwait(false);
            if (writeDone != writeTask)
            {
                AbandonPipe(ref client);
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"write_timeout\"}", "write_timeout");
            }

            try { await writeTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AbandonPipe(ref client);
                return new PipeCallResult(false, false,
                    JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                    ex.Message);
            }

            try { await client.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* ignore */ }

            // Read one line with a hard deadline; do not use StreamReader.Dispose on timeout.
            var line = await ReadLineWithDeadlineAsync(client, responseMs).ConfigureAwait(false);
            if (line is null)
            {
                AbandonPipe(ref client);
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"response_timeout\"}", "response_timeout");
            }

            var owned = client;
            client = null; // transferred — close after success without blocking caller long
            AbandonPipe(ref owned);

            return new PipeCallResult(true, false,
                string.IsNullOrWhiteSpace(line)
                    ? "{\"ok\":false,\"error\":\"empty_response\"}"
                    : line,
                null);
        }
        catch (Exception ex)
        {
            AbandonPipe(ref client);
            return new PipeCallResult(false, false,
                JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                ex.Message);
        }
        finally
        {
            AbandonPipe(ref client);
        }
    }

    /// <summary>
    /// After the CLI wrote teardown.ok (HTTP verify already done), ask the service to
    /// unlock SCM ACL and stop — no second code consume.
    /// Overall wall-clock budget so a stuck Dispose/connect can never freeze the CLI.
    /// </summary>
    public static async Task<(bool Ok, bool TimedOut, string Body, string? Error)> ApplyTeardownGateDetailedAsync(
        string purpose = "both",
        CancellationToken ct = default)
    {
        var sendTask = SendDetailedAsync(
            new { op = "ApplyTeardownGate", purpose },
            connectTimeoutMs: 4_000,
            responseTimeoutMs: ApplyTeardownResponseTimeoutMs,
            ct: CancellationToken.None);

        var finished = await Task.WhenAny(
                sendTask,
                Task.Delay(ApplyTeardownOverallTimeoutMs, CancellationToken.None))
            .ConfigureAwait(false);

        if (finished != sendTask)
        {
            // Abandon sendTask — do not await (may be stuck in OS pipe close).
            return (false, true,
                "{\"ok\":false,\"error\":\"overall_timeout\"}", "overall_timeout");
        }

        try
        {
            var r = await sendTask.ConfigureAwait(false);
            return (r.Connected && !r.TimedOut && IsOk(r.Body), r.TimedOut, r.Body, r.Error);
        }
        catch (Exception ex)
        {
            return (false, false,
                JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                ex.Message);
        }
    }

    public static async Task<bool> ApplyTeardownGateAsync(
        string purpose = "both",
        CancellationToken ct = default)
    {
        var detailed = await ApplyTeardownGateDetailedAsync(purpose, ct).ConfigureAwait(false);
        return detailed.Ok;
    }

    public static async Task<bool> AuthorizeTeardownAsync(
        string code,
        string purpose = "both",
        CancellationToken ct = default)
    {
        var (connected, body) = await SendAsync(
            new { op = "AuthorizeTeardown", code, purpose },
            connectTimeoutMs: DefaultConnectTimeoutMs,
            responseTimeoutMs: DefaultResponseTimeoutMs,
            ct: ct).ConfigureAwait(false);
        return connected && IsOk(body);
    }

    /// <summary>
    /// Ask LocalSystem <c>SilentUpdateWorker</c> to check/download/apply (no UAC).
    /// Returns false when the pipe is down (service missing/stopped).
    /// </summary>
    public static async Task<bool> RequestUpdateCheckAsync(CancellationToken ct = default)
    {
        var detailed = await RequestUpdateCheckDetailedAsync(ct).ConfigureAwait(false);
        return detailed.Connected && !detailed.TimedOut && IsOk(detailed.Body);
    }

    /// <summary>
    /// Full pipe result. <c>unknown_op</c>, <c>update_disabled</c>, and timeouts stay
    /// visible. The bool helper collapses all of those to false.
    /// </summary>
    public static Task<PipeCallResult> RequestUpdateCheckDetailedAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new PipeCallResult(false, false,
                "{\"ok\":false,\"error\":\"windows_only\"}", "windows_only"));
        return SendDetailedAsync(
            new { op = "RequestUpdateCheck" },
            connectTimeoutMs: DefaultConnectTimeoutMs,
            responseTimeoutMs: DefaultResponseTimeoutMs,
            ct: ct);
    }

    /// <summary>Read Chrome-like updater phase from the DLP/update pipe.</summary>
    public static async Task<(bool Ok, string Body)> GetUpdateStatusAsync(CancellationToken ct = default)
    {
        var detailed = await GetUpdateStatusDetailedAsync(ct).ConfigureAwait(false);
        return (detailed.Connected && !detailed.TimedOut, detailed.Body);
    }

    public static Task<PipeCallResult> GetUpdateStatusDetailedAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(new PipeCallResult(false, false,
                "{\"ok\":false,\"error\":\"windows_only\"}", "windows_only"));
        return SendDetailedAsync(
            new { op = "GetUpdateStatus" },
            connectTimeoutMs: DefaultConnectTimeoutMs,
            responseTimeoutMs: DefaultResponseTimeoutMs,
            ct: ct);
    }

    private static async Task<PipeCallResult> SendUnixDetailedAsync(
        object request,
        int connectTimeoutMs,
        int responseTimeoutMs,
        CancellationToken ct)
    {
        var path = DefaultUnixSocketPath;
        if (!File.Exists(path))
            return new PipeCallResult(false, false,
                "{\"ok\":false,\"error\":\"socket_missing\"}", "socket_missing");

        Socket? sock = null;
        try
        {
            sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var connectMs = Math.Clamp(connectTimeoutMs, 500, 10_000);
            var connectTask = sock.ConnectAsync(new UnixDomainSocketEndPoint(path), CancellationToken.None).AsTask();
            var connectDone = await Task.WhenAny(connectTask, Task.Delay(connectMs, CancellationToken.None))
                .ConfigureAwait(false);
            if (connectDone != connectTask)
            {
                AbandonSocket(ref sock);
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"connect_timeout\"}", "connect_timeout");
            }

            try { await connectTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AbandonSocket(ref sock);
                return new PipeCallResult(false, false,
                    JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                    ex.Message);
            }

            var responseMs = Math.Clamp(responseTimeoutMs, 1_000, 30_000);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
            var writeTask = sock.SendAsync(payload, SocketFlags.None, CancellationToken.None).AsTask();
            var writeDone = await Task.WhenAny(writeTask, Task.Delay(responseMs, CancellationToken.None))
                .ConfigureAwait(false);
            if (writeDone != writeTask)
            {
                AbandonSocket(ref sock);
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"write_timeout\"}", "write_timeout");
            }

            try { await writeTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                AbandonSocket(ref sock);
                return new PipeCallResult(false, false,
                    JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                    ex.Message);
            }

            using var ns = new NetworkStream(sock, ownsSocket: false);
            var line = await ReadLineFromStreamWithDeadlineAsync(ns, responseMs).ConfigureAwait(false);
            AbandonSocket(ref sock);
            if (line is null)
            {
                return new PipeCallResult(false, true,
                    "{\"ok\":false,\"error\":\"response_timeout\"}", "response_timeout");
            }

            return new PipeCallResult(true, false,
                string.IsNullOrWhiteSpace(line)
                    ? "{\"ok\":false,\"error\":\"empty_response\"}"
                    : line,
                null);
        }
        catch (Exception ex)
        {
            AbandonSocket(ref sock);
            return new PipeCallResult(false, false,
                JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                ex.Message);
        }
    }

    private static void AbandonSocket(ref Socket? sock)
    {
        var s = sock;
        sock = null;
        if (s is null) return;
        try
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try
                {
                    if (state is Socket socket)
                    {
                        try { socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
                        socket.Dispose();
                    }
                }
                catch { /* ignore */ }
            }, s);
        }
        catch { /* ignore */ }
    }

    private static async Task<string?> ReadLineFromStreamWithDeadlineAsync(Stream stream, int timeoutMs)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(256);
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var readTask = stream.ReadAsync(buffer, 0, 1, CancellationToken.None);
            var done = await Task.WhenAny(readTask, Task.Delay(remaining, CancellationToken.None))
                .ConfigureAwait(false);
            if (done != readTask)
                return null;

            int n;
            try { n = await readTask.ConfigureAwait(false); }
            catch { return null; }

            if (n <= 0)
                return sb.Length > 0 ? sb.ToString() : null;

            var ch = (char)buffer[0];
            if (ch == '\n')
                return sb.ToString().TrimEnd('\r');
            if (ch != '\r')
                sb.Append(ch);

            if (sb.Length > 64_000)
                return sb.ToString();
        }

        return null;
    }

    /// <summary>
    /// Never await Dispose on a possibly-stuck pipe — that was the protected-uninstall hang.
    /// </summary>
    private static void AbandonPipe(ref NamedPipeClientStream? client)
    {
        var c = client;
        client = null;
        if (c is null) return;
        try
        {
            ThreadPool.QueueUserWorkItem(static state =>
            {
                try
                {
                    if (state is NamedPipeClientStream pipe)
                        pipe.Dispose();
                }
                catch { /* ignore */ }
            }, c);
        }
        catch { /* ignore */ }
    }

    private static async Task<string?> ReadLineWithDeadlineAsync(NamedPipeClientStream client, int timeoutMs)
    {
        var buffer = new byte[1];
        var sb = new StringBuilder(256);
        var deadline = Environment.TickCount64 + timeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var remaining = (int)Math.Max(1, deadline - Environment.TickCount64);
            var readTask = client.ReadAsync(buffer, 0, 1, CancellationToken.None);
            var done = await Task.WhenAny(readTask, Task.Delay(remaining, CancellationToken.None))
                .ConfigureAwait(false);
            if (done != readTask)
                return null;

            int n;
            try { n = await readTask.ConfigureAwait(false); }
            catch { return null; }

            if (n <= 0)
                return sb.Length > 0 ? sb.ToString() : null;

            var ch = (char)buffer[0];
            if (ch == '\n')
                return sb.ToString().TrimEnd('\r');
            if (ch != '\r')
                sb.Append(ch);

            if (sb.Length > 64_000)
                return sb.ToString();
        }

        return null;
    }

    private static bool IsOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
