using System.Diagnostics;
using System.Text.Json;
using client.Services.FaceVerify;
using Xunit;

namespace client.Tests;

/// <summary>
/// Lab parity: Python face_worker embed vs .NET FaceRecognitionEngine on the same JPEG.
/// Skips unless models + fixture exist (set FACE_PARITY=1 to force fail-when-missing).
/// </summary>
[Collection("FacePathsSerial")]
public class FaceAlignParityTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WorkerModels = Path.Combine(RepoRoot, "server", "face_worker", "models");
    private static readonly string FixtureJpeg = Path.Combine(
        RepoRoot,
        "server", "media", "faces", "EMP-10008",
        "90c4c3f7-442a-4fd8-a410-b85b328a297f",
        "eaf692cf-e437-4ca5-b9d8-e4c2746dfd0c.jpg");

    [Fact]
    public void DotNet_embedding_matches_python_worker_within_0_95()
    {
        if (!OperatingSystem.IsWindows())
            return;

        FacePaths.RootDirectoryOverride = null;

        var force = string.Equals(
            Environment.GetEnvironmentVariable("FACE_PARITY"), "1", StringComparison.Ordinal);
        if (!File.Exists(FixtureJpeg)
            || !File.Exists(Path.Combine(WorkerModels, "w600k_r50.onnx"))
            || !File.Exists(Path.Combine(WorkerModels, "det_10g.onnx")))
        {
            if (force)
                Assert.Fail("FACE_PARITY=1 but fixture/models missing");
            return;
        }

        FacePaths.EnsureDirectories();
        CopyIfNeeded(
            Path.Combine(WorkerModels, "det_10g.onnx"),
            FacePaths.DetPath);
        CopyIfNeeded(
            Path.Combine(WorkerModels, "w600k_r50.onnx"),
            FacePaths.RecPath);

        var staging = Path.Combine(FacePaths.StagingDirectory, "parity");
        Directory.CreateDirectory(staging);
        var pyJson = Path.Combine(staging, "python_vec.json");
        var dotJson = Path.Combine(staging, "dotnet_vec.json");

        RunPythonParityWrite(FixtureJpeg, pyJson);

        using var engine = new FaceRecognitionEngine();
        engine.LoadModels();
        var vec = engine.EmbedImageFile(FixtureJpeg);
        Assert.NotNull(vec);
        Assert.Equal(512, vec!.Length);
        File.WriteAllText(dotJson, JsonSerializer.Serialize(vec));

        var py = JsonSerializer.Deserialize<float[]>(File.ReadAllText(pyJson));
        Assert.NotNull(py);
        Assert.Equal(vec.Length, py!.Length);

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < vec.Length; i++)
        {
            dot += vec[i] * py[i];
            na += vec[i] * vec[i];
            nb += py[i] * py[i];
        }
        var cos = dot / (Math.Sqrt(na) * Math.Sqrt(nb) + 1e-12);
        Assert.True(cos >= 0.95, $"python↔dotnet cosine {cos:0.0000} < 0.95 (LMEDS align parity)");
    }

    private static void RunPythonParityWrite(string image, string outJson)
    {
        var script = Path.Combine(RepoRoot, "server", "face_worker", "parity_check.py");
        var psi = new ProcessStartInfo
        {
            FileName = "python",
            ArgumentList = { script, image, "--write-json", outJson },
            WorkingDirectory = Path.Combine(RepoRoot, "server", "face_worker"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("python start failed");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        Assert.True(p.ExitCode == 0, $"parity_check.py failed ({p.ExitCode}): {stderr}\n{stdout}");
        Assert.True(File.Exists(outJson), "python did not write embedding JSON");
    }

    private static void CopyIfNeeded(string src, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest) && new FileInfo(dest).Length == new FileInfo(src).Length)
            return;
        File.Copy(src, dest, overwrite: true);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "server", "face_worker")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // client.Tests runs from bin/.../ — walk up from cwd as fallback
        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
