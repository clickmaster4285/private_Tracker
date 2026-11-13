namespace client.Services.FaceVerify;

public sealed class FaceModelDownloader
{
    /// <summary>
    /// Ensures YuNet + AdaFace exist under LocalAppData …/face/models/.
    /// Both are downloaded from this fleet's API (DeviceAuth) — no GitHub on the PC.
    /// Optional <c>ALPHA_FACE_ADAFACE_URL</c> / session override only as AdaFace fallback.
    /// </summary>
    public async Task EnsureModelsAsync(
        FaceVerifySession session,
        FaceApiClient api,
        IProgress<(string step, double? fraction)> progress,
        CancellationToken ct)
    {
        FacePaths.EnsureDirectories();

        if (!File.Exists(FacePaths.YuNetPath) || new FileInfo(FacePaths.YuNetPath).Length < 100_000)
        {
            progress.Report(("Downloading face detector (YuNet) from API…", 0));
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageYuNet, FacePaths.YuNetPath, "YuNet", progress, ct);
        }
        else
        {
            progress.Report(("Face detector ready", 1));
        }

        if (File.Exists(FacePaths.AdaFacePath) && new FileInfo(FacePaths.AdaFacePath).Length > 1_000_000)
        {
            progress.Report(("Recognition model ready", 1));
            return;
        }

        progress.Report(("Downloading recognition model (AdaFace) from API…", 0));
        try
        {
            await api.DownloadModelPackageAsync(
                FaceApiClient.PackageAdaFace, FacePaths.AdaFacePath, "AdaFace", progress, ct);
            progress.Report(("Recognition model ready", 1));
            return;
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(session.AdaFaceUrl))
        {
            progress.Report(($"API package failed ({ex.Message}); trying ALPHA_FACE_ADAFACE_URL…", 0));
            await DownloadFileAsync(session.AdaFaceUrl!, FacePaths.AdaFacePath, progress, ct);
            progress.Report(("Recognition model ready", 1));
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "AdaFace ONNX missing locally and API download failed. " +
                "Ensure face_worker/models/adaface_ir50_webface4m.onnx exists on the API host " +
                "(served at GET /face-models/packages/adaface-ir50), " +
                "or set ALPHA_FACE_ADAFACE_URL. " +
                $"Local cache: {FacePaths.AdaFacePath}. ({ex.Message})", ex);
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
            using var http = client.Core.TlsCertificateTrust.CreateHttpClient(TimeSpan.FromMinutes(30));
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
