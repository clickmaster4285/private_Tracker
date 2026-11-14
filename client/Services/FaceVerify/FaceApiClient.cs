using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace client.Services.FaceVerify;

public sealed class FaceEmbeddingsResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("normalization")]
    public string? Normalization { get; set; }

    [JsonPropertyName("preprocessVersion")]
    public string? PreprocessVersion { get; set; }

    [JsonPropertyName("profile")]
    public FaceProfileDto? Profile { get; set; }

    [JsonPropertyName("embeddings")]
    public List<FaceEmbeddingDto>? Embeddings { get; set; }
}

public sealed class FaceProfileDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("employeeId")]
    public string EmployeeId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("profileVersion")]
    public int ProfileVersion { get; set; }

    [JsonPropertyName("enrollmentVersion")]
    public int EnrollmentVersion { get; set; }

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("modelVersion")]
    public string ModelVersion { get; set; } = "";

    [JsonPropertyName("preprocessVersion")]
    public string PreprocessVersion { get; set; } = "face-preprocess-v1";

    [JsonPropertyName("embeddingDims")]
    public int EmbeddingDims { get; set; } = 512;

    [JsonPropertyName("embeddingCount")]
    public int EmbeddingCount { get; set; }
}

public sealed class FaceEmbeddingDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("profileId")]
    public string ProfileId { get; set; } = "";

    [JsonPropertyName("employeeId")]
    public string EmployeeId { get; set; } = "";

    [JsonPropertyName("sampleId")]
    public string? SampleId { get; set; }

    [JsonPropertyName("poseId")]
    public string? PoseId { get; set; }

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("modelVersion")]
    public string ModelVersion { get; set; } = "";

    [JsonPropertyName("preprocessVersion")]
    public string PreprocessVersion { get; set; } = "face-preprocess-v1";

    [JsonPropertyName("embeddingDims")]
    public int EmbeddingDims { get; set; } = 512;

    [JsonPropertyName("qualityScore")]
    public double? QualityScore { get; set; }

    [JsonPropertyName("vector")]
    public float[]? Vector { get; set; }
}

public sealed class FaceApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiBase;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public FaceApiClient(string apiBase, string deviceToken)
    {
        _apiBase = apiBase.TrimEnd('/');
        // apiBase may be https://host:8000 or …/api/v1
        if (_apiBase.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            _apiBase = _apiBase[..^"/api/v1".Length];

        // Same fleet TLS policy as login/sync (OS trust + ALPHA_TLS_CA / bundled rootCA).
        _http = client.Core.TlsCertificateTrust.CreateHttpClient(TimeSpan.FromMinutes(30));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Device", deviceToken);
    }

    public const string PackageBuffaloDet = "buffalo-l-det";
    public const string PackageBuffaloRec = "buffalo-l-rec";

    /// <summary>
    /// Downloads a face ONNX package from the API into LocalAppData (buffalo_l det/rec).
    /// </summary>
    public async Task DownloadModelPackageAsync(
        string packageId,
        string destPath,
        string label,
        IProgress<(string step, double? fraction)> progress,
        CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-models/packages/{packageId}";
        var tmp = destPath + ".tmp";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
                    progress.Report(($"Downloading {label}… {read / (1024 * 1024)} / {total.Value / (1024 * 1024)} MB",
                        (double)read / total.Value));
            }
            await dst.FlushAsync(ct);
            dst.Close();
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Move(tmp, destPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    /// <summary>Downloads buffalo_l recognition ONNX from the API into LocalAppData.</summary>
    public Task DownloadRecPackageAsync(
        string destPath,
        IProgress<(string step, double? fraction)> progress,
        CancellationToken ct) =>
        DownloadModelPackageAsync(PackageBuffaloRec, destPath, "buffalo_l", progress, ct);

    public async Task<FaceEmbeddingsResponse> GetEmbeddingsAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/me/embeddings";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<FaceEmbeddingsResponse>(JsonOpts, ct);
        return body ?? new FaceEmbeddingsResponse { Status = "none", Embeddings = new() };
    }

    public async Task SyncAckAsync(int profileVersion, string modelVersion, string machineId, CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/sync-ack";
        using var resp = await _http.PostAsJsonAsync(url, new
        {
            profileVersion,
            modelVersion,
            clientDeviceId = machineId,
        }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task VerifyEventAsync(
        string result,
        string machineId,
        double? score,
        double? threshold,
        string? detail,
        CancellationToken ct,
        string? clientEventId = null,
        string? mode = null,
        string? trigger = null,
        string? poseId = null,
        string? createdAt = null)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/verify-event";
        using var resp = await _http.PostAsJsonAsync(url, new
        {
            result,
            score,
            threshold,
            clientDeviceId = machineId,
            detail,
            clientEventId,
            mode,
            trigger,
            poseId,
            createdAt,
        }, ct);
        _ = resp;
    }

    public async Task<FaceSamplesResponse> GetMySamplesAsync(CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/me/samples";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<FaceSamplesResponse>(JsonOpts, ct);
        return body ?? new FaceSamplesResponse { Samples = new() };
    }

    public async Task DownloadSampleImageAsync(string sampleId, string destPath, CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/me/samples/{Uri.EscapeDataString(sampleId)}/image";
        var tmp = destPath + ".tmp";
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst, ct);
            await dst.FlushAsync(ct);
            dst.Close();
            File.Move(tmp, destPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    public async Task<FaceVerifySyncResponse> SyncVerifyEventsAsync(
        IReadOnlyList<LocalFaceVerifyEvent> events,
        CancellationToken ct)
    {
        var url = $"{_apiBase}/api/v1/face-profiles/verify-events/sync";
        var payload = new
        {
            events = events.Select(e => new
            {
                clientEventId = e.Id,
                machineId = e.MachineId,
                mode = e.Mode,
                trigger = e.Trigger,
                poseId = e.PoseId,
                result = e.Result,
                score = e.Score,
                threshold = e.Threshold,
                detail = e.Detail,
                createdAt = e.CreatedAt,
            }).ToList(),
        };
        using var resp = await _http.PostAsJsonAsync(url, payload, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<FaceVerifySyncResponse>(JsonOpts, ct);
        return body ?? new FaceVerifySyncResponse { AcceptedIds = new() };
    }

    public void Dispose() => _http.Dispose();
}

public sealed class FaceSamplesResponse
{
    [JsonPropertyName("samples")]
    public List<FaceSamplePackageDto>? Samples { get; set; }
}

public sealed class FaceSamplePackageDto
{
    [JsonPropertyName("sampleId")]
    public string SampleId { get; set; } = "";

    [JsonPropertyName("poseId")]
    public string PoseId { get; set; } = "";

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("byteSize")]
    public long ByteSize { get; set; }

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }
}

public sealed class FaceVerifySyncResponse
{
    [JsonPropertyName("acceptedIds")]
    public List<string>? AcceptedIds { get; set; }
}
