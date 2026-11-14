namespace client.Services.FaceVerify;

public sealed class FaceModelDownloader
{
    /// <summary>
    /// Ensures buffalo_l det_10g + w600k_r50 exist under LocalAppData …/face/models/.
    /// Both are downloaded from this fleet's API (DeviceAuth) — no GitHub on the PC.
    /// Optional recognition URL override only as fallback.
    /// </summary>
    public async Task EnsureModelsAsync(
        FaceVerifySession session,
        FaceApiClient api,
        IProgress<(string step, double? fraction)> progress,
        CancellationToken ct)
    {
        FacePaths.EnsureDirectories();

        if (!File.Exists(FacePaths.DetPath) || new FileInfo(FacePaths.DetPath).Length < 100_000)
        {
            progress.Report(("Downloading face detector (SCRFD det_10g) from API…", 0));
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageBuffaloDet, FacePaths.DetPath, "SCRFD", progress, ct);
        }
        else
        {
            progress.Report(("Face detector ready", 1));
        }

        if (File.Exists(FacePaths.RecPath) && new FileInfo(FacePaths.RecPath).Length > 1_000_000)
        {
            progress.Report(("Recognition model ready", 1));
            return;
        }

        progress.Report(("Downloading recognition model (buffalo_l w600k_r50) from API…", 0));
        try
        {
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageBuffaloRec, FacePaths.RecPath, "buffalo_l", progress, ct);
            progress.Report(("Recognition model ready", 1));
            return;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(session.RecModelUrl))
        {
            progress.Report(($"API package failed ({ex.Message}); trying override URL…", 0));
            await DownloadFileAsync(session.RecModelUrl!, FacePaths.RecPath, progress, ct);
            progress.Report(("Recognition model ready", 1));
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "buffalo_l w600k_r50 missing locally and API download failed. " +
                "Ensure face_worker/models/det_10g.onnx and w600k_r50.onnx exist on the API host " +
                "(served at GET /face-models/packages/buffalo-l-det|buffalo-l-rec), " +
                "or set ALPHA_FACE_BUFFALO_REC_URL. " +
                $"Local cache: {FacePaths.RecPath}. ({ex.Message})", ex);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        IProgress<(string step, double? fraction)> progress,
        CancellationToken ct)
    {
        var tmp = destPath + ".tmp";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (total is > 0)
                    progress.Report(($"Downloading… {read / (1024 * 1024)} / {total.Value / (1024 * 1024)} MB",
                        (double)read / total.Value));
            }
            await dst.FlushAsync(ct);
            dst.Close();
            File.Move(tmp, destPath, overwrite: true);
            progress.Report(("Download complete", 1));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
