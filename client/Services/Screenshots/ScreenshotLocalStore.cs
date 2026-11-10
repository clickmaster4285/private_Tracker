using System.Text.Json;
using client.Core;

namespace client.Services.Screenshots;

/// <summary>
/// Offline-first screenshot pending store under LocalAppData (never the install dir).
/// Layout: screenshots/pending/*.webp + *.json sidecar; rejected/ for poison uploads.
/// </summary>
public static class ScreenshotLocalStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string RootDir
    {
        get
        {
            var baseDir = OperatingSystem.IsWindows()
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppInfo.DataFolderName)
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share", AppInfo.PackageName);
            return Path.Combine(baseDir, "screenshots");
        }
    }

    public static string PendingDir => Path.Combine(RootDir, "pending");
    public static string RejectedDir => Path.Combine(RootDir, "rejected");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(RejectedDir);
    }

    public static string BuildBaseName(string empId, DateTimeOffset capturedAt, string machineId, string screenshotId)
    {
        var safeEmp = Sanitize(empId);
        var safeMachine = Sanitize(machineId);
        var safeId = Sanitize(screenshotId);
        return $"{safeEmp}_{capturedAt:yyyyMMdd}_{capturedAt:HHmmss}_{capturedAt:fff}_{safeMachine}_{safeId}";
    }

    public static async Task WritePendingAsync(
        string baseName,
        byte[] webpBytes,
        ScreenshotMeta meta,
        CancellationToken ct)
    {
        EnsureDirs();
        var webpPath = Path.Combine(PendingDir, baseName + ".webp");
        var jsonPath = Path.Combine(PendingDir, baseName + ".json");
        var tmpWebp = webpPath + ".tmp";
        var tmpJson = jsonPath + ".tmp";

        await File.WriteAllBytesAsync(tmpWebp, webpBytes, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(tmpJson, JsonSerializer.Serialize(meta, JsonOpts), ct)
            .ConfigureAwait(false);
        File.Move(tmpWebp, webpPath, overwrite: true);
        File.Move(tmpJson, jsonPath, overwrite: true);
    }

    public static IReadOnlyList<PendingScreenshot> ListPendingOldest(int take)
    {
        EnsureDirs();
        if (take < 1) return Array.Empty<PendingScreenshot>();

        var files = Directory.EnumerateFiles(PendingDir, "*.webp")
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(take)
            .ToList();

        var list = new List<PendingScreenshot>(files.Count);
        foreach (var webp in files)
        {
            var json = Path.ChangeExtension(webp, ".json");
            ScreenshotMeta? meta = null;
            if (File.Exists(json))
            {
                try
                {
                    meta = JsonSerializer.Deserialize<ScreenshotMeta>(File.ReadAllText(json), JsonOpts);
                }
                catch
                {
                    /* fall through to name parse */
                }
            }

            meta ??= GuessMetaFromName(Path.GetFileNameWithoutExtension(webp));
            if (meta is null) continue;

            // Recover ScreenshotId from basename when the sidecar omitted it —
            // never mint a fresh Guid here (that broke idempotency and flooded
            // the server with byte-identical duplicates).
            if (string.IsNullOrWhiteSpace(meta.ScreenshotId))
            {
                var fromName = GuessMetaFromName(Path.GetFileNameWithoutExtension(webp));
                if (fromName is not null && !string.IsNullOrWhiteSpace(fromName.ScreenshotId))
                    meta.ScreenshotId = fromName.ScreenshotId;
            }

            // Missing/invalid id is left for SyncWorker to MoveToRejected (do not mint).
            list.Add(new PendingScreenshot(webp, json, meta));
        }
        return list;
    }

    public static (int count, long bytes) PendingStats()
    {
        EnsureDirs();
        long bytes = 0;
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(PendingDir, "*.webp"))
        {
            count++;
            try { bytes += new FileInfo(f).Length; } catch { /* ignored */ }
        }
        return (count, bytes);
    }

    public static void DeleteAccepted(PendingScreenshot item)
    {
        TryDelete(item.WebpPath);
        TryDelete(item.JsonPath);
        TryDelete(item.WebpPath + ".uploading");
    }

    public static void MoveToRejected(PendingScreenshot item)
    {
        EnsureDirs();
        try
        {
            var destWebp = Path.Combine(RejectedDir, Path.GetFileName(item.WebpPath));
            var destJson = Path.Combine(RejectedDir, Path.GetFileName(item.JsonPath));
            if (File.Exists(item.WebpPath))
                File.Move(item.WebpPath, destWebp, overwrite: true);
            if (File.Exists(item.JsonPath))
                File.Move(item.JsonPath, destJson, overwrite: true);
            TryDelete(item.WebpPath + ".uploading");
        }
        catch
        {
            /* leave in pending for another attempt */
        }
    }

    private static ScreenshotMeta? GuessMetaFromName(string baseName)
    {
        // {emp}_{yyyyMMdd}_{HHmmss}_{fff}_{machine}_{screenshotId}
        // machine may itself contain '_' — screenshotId is the trailing UUID.
        var parts = baseName.Split('_');
        if (parts.Length < 6) return null;
        var emp = parts[0];
        if (!DateTime.TryParseExact(
                parts[1] + parts[2] + parts[3],
                "yyyyMMddHHmmssfff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var dt))
            return null;

        var last = parts[^1];
        if (!Guid.TryParse(last, out _))
            return null;

        var machine = string.Join('_', parts.Skip(4).Take(parts.Length - 5));
        if (string.IsNullOrWhiteSpace(machine))
            machine = "unknown";

        return new ScreenshotMeta
        {
            ScreenshotId = last,
            EmployeeId = emp,
            MachineId = machine,
            CapturedAtUtc = dt,
            Width = 0,
            Height = 0,
            ByteSize = 0,
            ContentType = "image/webp",
        };
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
                chars[i] = '-';
        }
        var s = new string(chars);
        return string.IsNullOrWhiteSpace(s) ? "unknown" : s;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignored */ }
    }
}

public sealed class ScreenshotMeta
{
    /// <summary>Client-generated UUID — server primary key / idempotency key.</summary>
    public string ScreenshotId { get; set; } = "";
    public string EmployeeId { get; set; } = "";
    public string MachineId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long ByteSize { get; set; }
    public string ContentType { get; set; } = "image/webp";
}

public sealed record PendingScreenshot(string WebpPath, string JsonPath, ScreenshotMeta Meta);
