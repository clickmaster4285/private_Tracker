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
        IProgress<FaceDownloadProgress> progress,
        CancellationToken ct)
    {
        FacePaths.EnsureDirectories();

        var needDet = !File.Exists(FacePaths.DetPath) || new FileInfo(FacePaths.DetPath).Length < 100_000;
        var needRec = !(File.Exists(FacePaths.RecPath) && new FileInfo(FacePaths.RecPath).Length > 1_000_000);
        if (!needDet && !needRec)
        {
            progress.Report(FaceDownloadProgress.Complete());
            return;
        }

        var fileCount = (needDet ? 1 : 0) + (needRec ? 1 : 0);
        var fileIndex = 0;

        if (needDet)
        {
            progress.Report(FaceDownloadProgress.Status("Downloading face detector (SCRFD det_10g) from API…", 0));
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageBuffaloDet, FacePaths.DetPath, "SCRFD", progress, ct,
                fileIndex: fileIndex, fileCount: fileCount);
            fileIndex++;
            progress.Report(FaceDownloadProgress.Status("Face detector ready",
                fileCount > 0 ? (double)fileIndex / fileCount : 1));
        }

        if (!needRec)
        {
            progress.Report(FaceDownloadProgress.Complete());
            return;
        }

        progress.Report(FaceDownloadProgress.Status("Downloading recognition model (buffalo_l w600k_r50) from API…",
            fileCount > 0 ? (double)fileIndex / fileCount : 0));
        try
        {
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageBuffaloRec, FacePaths.RecPath, "buffalo_l", progress, ct,
                fileIndex: fileIndex, fileCount: fileCount);
            progress.Report(FaceDownloadProgress.Complete());
            return;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(session.RecModelUrl))
        {
            progress.Report(FaceDownloadProgress.Status(
                $"API package failed ({ex.Message}); trying override URL…",
                fileCount > 0 ? (double)fileIndex / fileCount : 0));
            await DownloadFileAsync(session.RecModelUrl!, FacePaths.RecPath, "buffalo_l", progress, ct,
                fileIndex, fileCount);
            progress.Report(FaceDownloadProgress.Complete());
            return;
        }
        catch (Exception ex)
        {
            var msg =
                "buffalo_l w600k_r50 missing locally and API download failed. " +
                "Ensure face_worker/models/det_10g.onnx and w600k_r50.onnx exist on the API host " +
                "(served at GET /face-models/packages/buffalo-l-det|buffalo-l-rec), " +
                "or set ALPHA_FACE_BUFFALO_REC_URL. " +
                $"Local cache: {FacePaths.RecPath}. ({ex.Message})";
            progress.Report(FaceDownloadProgress.Failed(msg));
            throw new InvalidOperationException(msg, ex);
        }
    }

    private static async Task DownloadFileAsync(
        string url,
        string destPath,
        string label,
        IProgress<FaceDownloadProgress> progress,
        CancellationToken ct,
        int fileIndex,
        int fileCount)
    {
        var tmp = destPath + ".tmp";
        var eta = new FaceDownloadEtaTracker(label, fileIndex, fileCount);
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
            if (eta.TryReport(0, total, force: true) is { } p0) progress.Report(p0);
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (eta.TryReport(read, total) is { } p) progress.Report(p);
            }
            if (eta.TryReport(read, total, force: true) is { } p1) progress.Report(p1);
            await dst.FlushAsync(ct);
            dst.Close();
            File.Move(tmp, destPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }
}
