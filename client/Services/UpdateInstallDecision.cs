using System.Text.Json;

namespace client.Services;

/// <summary>
/// How the GUI should react to the Windows service update pipe.
/// A timeout is not proof the service failed to queue an update.
/// </summary>
public enum UpdateInstallRoute
{
    /// <summary>Service accepted the check. Poll <c>GetUpdateStatus</c>. Do not run an installer here.</summary>
    PollService,
    /// <summary>Installed service does not implement <c>RequestUpdateCheck</c> (<c>unknown_op</c>).</summary>
    GuiFallback,
    /// <summary>Service has updates turned off. Do not elevate.</summary>
    UpdatesDisabled,
    /// <summary>A download or apply is already running.</summary>
    AlreadyInProgress,
    /// <summary>Pipe timed out or status is unusable. Do not start a second installer.</summary>
    ServiceUnanswered,
}

public readonly record struct UpdatePipeView(bool Connected, bool TimedOut, string Body);

public static class UpdateInstallDecision
{
    public static UpdateInstallRoute Decide(UpdatePipeView request, UpdatePipeView? status)
    {
        if (ShowsInProgress(status))
            return UpdateInstallRoute.AlreadyInProgress;

        if (Answered(request))
        {
            var error = ReadError(request.Body);
            if (error == "update_disabled")
                return UpdateInstallRoute.UpdatesDisabled;
            if (error == "unknown_op")
                return UpdateInstallRoute.GuiFallback;
            if (ReadOk(request.Body))
                return UpdateInstallRoute.PollService;
        }

        // The request did not get a usable answer. An old service still answers
        // GetUpdateStatus with unknown_op and cannot be mid silent-apply.
        if (status is { } known && Answered(known) && ReadError(known.Body) == "unknown_op")
            return UpdateInstallRoute.GuiFallback;

        return UpdateInstallRoute.ServiceUnanswered;
    }

    /// <summary>
    /// A published size of 0 means the release list did not include one.
    /// Any other size must match the file before the installer is started.
    /// </summary>
    public static bool PublishedSizeMatches(long publishedSize, long fileLength) =>
        publishedSize <= 0 || fileLength == publishedSize;

    /// <summary>
    /// User-facing StatusText when the Update button cannot hand off to the service.
    /// Surfaces the real pipe error (connect/write/response timeout) instead of a
    /// generic "did not accept" line.
    /// </summary>
    public static string RefuseMessage(UpdateInstallRoute route, string? pipeError)
    {
        var err = string.IsNullOrWhiteSpace(pipeError) ? null : pipeError.Trim();
        return route switch
        {
            UpdateInstallRoute.GuiFallback =>
                "AlphaAITracker.Service is too old to start the update. Install the new service, then click Update again.",
            UpdateInstallRoute.UpdatesDisabled =>
                "Updates are turned off on AlphaAITracker.Service.",
            UpdateInstallRoute.AlreadyInProgress =>
                "An update is already in progress via AlphaAITracker.Service.",
            UpdateInstallRoute.PollService =>
                "Update handed to AlphaAITracker.Service.",
            _ => err switch
            {
                "connect_timeout" =>
                    "AlphaAITracker.Service is not reachable (pipe connect timed out). The app was left open.",
                "write_timeout" =>
                    "AlphaAITracker.Service did not accept the write (pipe write timed out). The app was left open.",
                "response_timeout" =>
                    "AlphaAITracker.Service did not respond in time (pipe response timed out). The app was left open.",
                "empty_response" =>
                    "AlphaAITracker.Service returned an empty pipe response. The app was left open.",
                { Length: > 0 } e =>
                    $"AlphaAITracker.Service did not accept the update ({e}). The app was left open.",
                _ =>
                    "AlphaAITracker.Service did not accept the update. The app was left open.",
            },
        };
    }

    public static bool ShowsInProgress(UpdatePipeView? status)
    {
        if (status is not { } view || !Answered(view))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(view.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (doc.RootElement.TryGetProperty("updateInProgress", out var busy)
                && busy.ValueKind == JsonValueKind.True)
                return true;
            if (doc.RootElement.TryGetProperty("phase", out var phase)
                && phase.ValueKind == JsonValueKind.String)
            {
                var name = phase.GetString();
                return name is "downloading" or "applying";
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    private static bool Answered(UpdatePipeView view) => view.Connected && !view.TimedOut;

    private static bool ReadOk(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return null;
            return error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
