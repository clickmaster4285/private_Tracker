using System.Diagnostics;
using System.Text.Json;
using client.Core;

namespace client.Services.ManagedLauncher;

/// <summary>
/// Branded loader for managed shortcuts. Reads the signed-shape catalog published by
/// DlpService (<c>%ProgramData%\&lt;mutex&gt;\dlp\managed-launcher.json</c>), shows the
/// requested entry (or a picker), and launches only approved paths. Direct EXE launches
/// outside this path remain blocked by WDAC/AppLocker with the native Windows denial.
/// </summary>
internal static class ManagedLauncherHost
{
    public static void Run(string? entryId)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var entries = LoadCatalog();
        if (entries.Count == 0)
        {
            MessageBoxSafe(
                "No approved applications are available yet.\n\n" +
                "An administrator must publish an application allowlist, " +
                "then wait for this PC to apply policy.",
                "Alpha AI Tracker — Managed Apps");
            return;
        }

        ManagedEntry? chosen = null;
        if (!string.IsNullOrWhiteSpace(entryId))
        {
            chosen = entries.FirstOrDefault(e =>
                e.Id.Equals(entryId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (chosen is null)
            {
                MessageBoxSafe(
                    "That application is not on the approved list for this PC.",
                    "Alpha AI Tracker — Managed Apps");
                return;
            }
        }
        else if (entries.Count == 1)
        {
            chosen = entries[0];
        }
        else
        {
            chosen = Pick(entries);
            if (chosen is null)
                return;
        }

        Launch(chosen);
    }

    private static void Launch(ManagedEntry entry)
    {
        var path = Environment.ExpandEnvironmentVariables(entry.Path);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBoxSafe(
                "The approved application file was not found:\n" + path,
                "Alpha AI Tracker — Managed Apps");
            return;
        }

        // Refuse user-writable paths even if the catalog was tampered with.
        var lower = path.ToLowerInvariant();
        if (lower.Contains("\\users\\") || lower.Contains("\\appdata\\") ||
            lower.Contains("\\temp\\") || lower.Contains("\\downloads\\"))
        {
            MessageBoxSafe(
                "Refusing to launch from a user-writable path.",
                "Alpha AI Tracker — Managed Apps");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Exception ex)
        {
            // WDAC/AppLocker denial surfaces as Win32Exception — show native-ish text.
            MessageBoxSafe(
                "Windows blocked this launch.\n\n" + ex.Message,
                "Alpha AI Tracker — Managed Apps");
        }
    }

    private static ManagedEntry? Pick(IReadOnlyList<ManagedEntry> entries)
    {
        // Minimal console-less picker via WinForms when available; otherwise first entry.
        try
        {
#if FACE_WINFORMS_LOCKER
            return ManagedLauncherForm.ShowPicker(entries);
#else
            return entries[0];
#endif
        }
        catch
        {
            return entries[0];
        }
    }

    private static IReadOnlyList<ManagedEntry> LoadCatalog()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppInfo.AppMutex,
                "dlp",
                "managed-launcher.json");
            if (!File.Exists(path))
                return Array.Empty<ManagedEntry>();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("catalog", out var catalog) ||
                !catalog.TryGetProperty("apps", out var apps) ||
                apps.ValueKind != JsonValueKind.Array)
                return Array.Empty<ManagedEntry>();

            var list = new List<ManagedEntry>();
            foreach (var el in apps.EnumerateArray())
            {
                var id = ReadString(el, "Id", "id");
                var name = ReadString(el, "Name", "name");
                var appPath = ReadString(el, "Path", "path");
                if (string.IsNullOrWhiteSpace(appPath))
                    continue;
                list.Add(new ManagedEntry(id, string.IsNullOrWhiteSpace(name) ? appPath : name, appPath));
            }
            return list;
        }
        catch
        {
            return Array.Empty<ManagedEntry>();
        }
    }

    private static string ReadString(JsonElement el, string a, string b)
    {
        if (el.TryGetProperty(a, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? "";
        if (el.TryGetProperty(b, out var v2) && v2.ValueKind == JsonValueKind.String)
            return v2.GetString() ?? "";
        return "";
    }

    private static void MessageBoxSafe(string text, string caption)
    {
        try
        {
            System.Windows.Forms.MessageBox.Show(
                text, caption,
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
        }
        catch
        {
            Console.Error.WriteLine(caption + ": " + text);
        }
    }

    internal sealed record ManagedEntry(string Id, string Name, string Path);
}
