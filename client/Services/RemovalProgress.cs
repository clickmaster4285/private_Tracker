using client.Core;

namespace client.Services;

/// <summary>
/// Cross-process progress for protected-uninstall: the running tracker writes
/// phases; a second CLI process polls and updates its spinner label.
/// </summary>
public static class RemovalProgress
{
    public static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppInfo.DataFolderName, "dlp", "removal.progress");

    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { /* best-effort */ }
    }

    public static void Write(string phase, string label)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp,
                $"phase={phase}\nlabel={label}\nat={DateTime.UtcNow:o}\n");
            File.Copy(tmp, FilePath, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        catch { /* best-effort */ }
    }

    public static bool TryRead(out string phase, out string label)
    {
        phase = "";
        label = "";
        try
        {
            if (!File.Exists(FilePath)) return false;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                if (line.StartsWith("phase=", StringComparison.OrdinalIgnoreCase))
                    phase = line["phase=".Length..].Trim();
                else if (line.StartsWith("label=", StringComparison.OrdinalIgnoreCase))
                    label = line["label=".Length..].Trim();
            }
            return !string.IsNullOrEmpty(phase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsTerminal(string phase) =>
        phase is "done" or "failed";
}
