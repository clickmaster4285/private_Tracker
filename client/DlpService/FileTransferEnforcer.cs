using Microsoft.Win32;

namespace AlphaAITracker.Service;

/// <summary>
/// File Transfer control (Windows). Writes Chromium enterprise policies under HKLM so
/// standard users cannot change them:
/// <list type="bullet">
/// <item><c>DownloadRestrictions = 3</c> — block every download.</item>
/// <item><c>AllowFileSelectionDialogs = 0</c> — the file picker never opens, so files
/// cannot be attached/uploaded to any website (also disables "Save as").</item>
/// </list>
/// Roots are the vendor-defined policy keys of the Chromium-family browsers that read
/// HKLM policy (not detection — the policy location is fixed by each vendor). Other
/// browsers must be blocked with the App Allow List (AppLocker).
///
/// Apply is cheap (a few registry reads) and runs every EnforceWorker tick; it only writes values that drifted and reports which ones it wrote, so the
/// caller can raise a tamper alert. Clear removes only the values this enforcer owns
/// (the exact value we write) — an org's own different policy value is left alone.
/// Drag-and-drop of a file into a page is NOT stopped by these policies.
/// </summary>
internal static class FileTransferEnforcer
{
    private static readonly object Gate = new();

    private static readonly string[] PolicyRoots =
    [
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
        @"SOFTWARE\Policies\Chromium",
    ];

    private const string DownloadValue = "DownloadRestrictions";
    private const int DownloadBlocked = 3;
    private const string UploadValue = "AllowFileSelectionDialogs";
    private const int UploadBlocked = 0;

    /// <param name="Changed">True when at least one value was written or removed.</param>
    /// <param name="Applied">True when the OS state now matches the request.</param>
    /// <param name="Written">Policy values written or removed this pass. When the desired state
    /// did not change since the last successful pass, a non-empty list means someone
    /// removed/changed the policy (tamper) — the caller raises the alert.</param>
    public sealed record Result(bool Changed, bool Applied, string Detail, IReadOnlyList<string> Written);

    public static Result Apply(bool blockDownload, bool blockUpload)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, false, "unsupported_os", Array.Empty<string>());

        lock (Gate)
        {
            var written = 0;
            var writtenNames = new List<string>();
            var errors = new List<string>();
            foreach (var root in PolicyRoots)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(root, writable: true);
                    if (key is null)
                    {
                        errors.Add(root + ":open_failed");
                        continue;
                    }
                    written += Sync(key, root, DownloadValue, DownloadBlocked, blockDownload, writtenNames);
                    written += Sync(key, root, UploadValue, UploadBlocked, blockUpload, writtenNames);
                }
                catch (Exception ex)
                {
                    errors.Add($"{root}:{ex.GetType().Name}");
                }
            }

            var detail = $"download={(blockDownload ? "block" : "allow")} upload={(blockUpload ? "block" : "allow")} written={written}";
            if (errors.Count > 0)
                detail += " err=" + string.Join(",", errors.Take(4));
            return new Result(written > 0, errors.Count == 0, detail, writtenNames);
        }
    }

    public static Result Clear() => Apply(blockDownload: false, blockUpload: false);

    /// <summary>
    /// want=true → ensure the value equals <paramref name="blocked"/>.
    /// want=false → delete it only when it holds our value (never touch an org's own policy).
    /// Returns 1 when a write/delete happened.
    /// </summary>
    private static int Sync(RegistryKey key, string root, string name, int blocked, bool want, List<string> written)
    {
        var current = key.GetValue(name);
        var isOurs = current is int i && i == blocked;
        if (want)
        {
            if (isOurs)
                return 0;
            written.Add($"{ShortRoot(root)}\\{name}={current ?? "missing"}");
            key.SetValue(name, blocked, RegistryValueKind.DWord);
            return 1;
        }

        if (!isOurs)
            return 0;
        key.DeleteValue(name, throwOnMissingValue: false);
        written.Add($"{ShortRoot(root)}\\{name}=removed");
        return 1;
    }

    private static string ShortRoot(string root) =>
        root.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase)
            ? root[@"SOFTWARE\Policies\".Length..]
            : root;
}
