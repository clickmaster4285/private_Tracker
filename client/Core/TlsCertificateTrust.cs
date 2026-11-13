using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

namespace client.Core;

/// <summary>
/// Private-network HTTPS trust for fleet installs.
/// <list type="bullet">
/// <item>Default: OS trust store (org root CA via GPO / Intune / <c>install-org-ca.ps1</c>).</item>
/// <item><c>ALPHA_TLS_CA</c> or bundled <c>tls/rootCA.pem</c>: extra PEM CA for chain building.</item>
/// <item><c>ALPHA_TLS_INSECURE=true</c>: lab-only skip verify — never ship as fleet default.</item>
/// </list>
/// </summary>
public static class TlsCertificateTrust
{
    public const string InsecureEnvKey = "ALPHA_TLS_INSECURE";
    public const string CaEnvKey = "ALPHA_TLS_CA";

    public static bool InsecureLabOverride =>
        IsTruthy(Environment.GetEnvironmentVariable(InsecureEnvKey));

    public static string? ResolveCaPemPath()
    {
        var explicitPath = Environment.GetEnvironmentVariable(CaEnvKey)?.Trim();
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return Path.GetFullPath(explicitPath);

        foreach (var candidate in DefaultCaSearchPaths())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static IEnumerable<string> DefaultCaSearchPaths()
    {
        // Installer / fleet drop (ProgramData) — shared by user tracker + LocalSystem DLP.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlphaAITracker", "tls", "rootCA.pem");

        // Per-user lab copy
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphaAITracker", "tls", "rootCA.pem");

        // Next to the exe (optional bundle)
        yield return Path.Combine(AppContext.BaseDirectory, "tls", "rootCA.pem");

        // Dev tree: client/bin/.../../../../../certs/rootCA.pem
        yield return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "certs", "rootCA.pem"));
    }

    public static HttpMessageHandler CreateHandler()
    {
        var handler = new HttpClientHandler();
        Apply(handler);
        return handler;
    }

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(CreateHandler(), disposeHandler: true);
        client.Timeout = timeout ?? TimeSpan.FromSeconds(120);
        return client;
    }

    public static void Apply(HttpClientHandler handler)
    {
        if (InsecureLabOverride)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            return;
        }

        var caPath = ResolveCaPemPath();
        if (string.IsNullOrWhiteSpace(caPath))
            return; // system trust only (GPO-installed org CA)

        handler.ServerCertificateCustomValidationCallback =
            (_, cert, chain, errors) => ValidateWithExtraCa(cert, chain, errors, caPath);
    }

    public static void Apply(ClientWebSocketOptions options)
    {
        if (InsecureLabOverride)
        {
            options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            return;
        }

        var caPath = ResolveCaPemPath();
        if (string.IsNullOrWhiteSpace(caPath))
            return;

        options.RemoteCertificateValidationCallback =
            (_, cert, chain, errors) => ValidateWithExtraCa(cert, chain, errors, caPath);
    }

    private static bool ValidateWithExtraCa(
        X509Certificate? cert,
        X509Chain? chain,
        SslPolicyErrors errors,
        string caPemPath)
    {
        if (cert == null)
            return false;

        // Already trusted by OS (e.g. GPO already installed the same CA).
        if (errors == SslPolicyErrors.None)
            return true;

        try
        {
            using var ca = X509Certificate2.CreateFromPemFile(caPemPath);
            using var build = new X509Chain();
            build.ChainPolicy.ExtraStore.Add(ca);
            build.ChainPolicy.VerificationFlags =
                X509VerificationFlags.AllowUnknownCertificateAuthority;
            build.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var leaf = new X509Certificate2(cert);
            if (!build.Build(leaf))
                return false;

            // Require that our org CA actually participates in the chain (not any unknown CA).
            foreach (var el in build.ChainElements)
            {
                if (el.Certificate.Thumbprint.Equals(ca.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTruthy(string? v) =>
        v is "1" or "true" or "True" or "YES" or "yes" or "on" or "ON";
}
