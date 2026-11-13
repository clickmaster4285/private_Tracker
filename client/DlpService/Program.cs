using AlphaAITracker.Service;
using Microsoft.Extensions.Hosting.WindowsServices;

// AlphaAITracker.Service — privileged DLP spine (Windows LocalSystem / Linux root systemd).
// Parked unless ALPHA_DLP_ENABLED=true. Does not host Avalonia UI.

EnvBootstrap.Apply();

var builder = Host.CreateApplicationBuilder(args);

if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = ServiceIdentity.ServiceName;
    });

    if (WindowsServiceHelpers.IsWindowsService())
    {
        // SCM starts us with System32 as cwd — pin to the exe folder so relative
        // assets and logs stay next to the binary under Program Files.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        builder.Environment.ContentRootPath = AppContext.BaseDirectory;
    }
}
else if (OperatingSystem.IsLinux())
{
    // systemd Type=simple — pin cwd to the installed share directory.
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
    builder.Environment.ContentRootPath = AppContext.BaseDirectory;
}

var cfg = DlpConfig.FromEnvironment();
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<PolicyStore>();
builder.Services.AddSingleton<WebBlockState>();
builder.Services.AddSingleton<RemovableDriveMonitor>();
builder.Services.AddHttpClient("dlp", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    // DlpService cannot reference client.dll — duplicate minimal trust from env / ProgramData.
    var handler = new HttpClientHandler();
    ApplyFleetTls(handler);
    return handler;
});
builder.Services.AddHttpClient("update-download", c =>
{
    // Multi-hundred-MB installers — same ceiling as the Avalonia AppUpdateService.
    c.Timeout = TimeSpan.FromMinutes(15);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var handler = new HttpClientHandler();
    ApplyFleetTls(handler);
    return handler;
});

builder.Services.AddSingleton<SilentUpdateState>();
builder.Services.AddSingleton<SilentUpdateWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SilentUpdateWorker>());

builder.Services.AddHostedService<ServiceAclWorker>();
builder.Services.AddSingleton<EnforceWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EnforceWorker>());
builder.Services.AddHostedService<PolicyPullWorker>();
builder.Services.AddHostedService<AlertSyncWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RemovableDriveMonitor>());
builder.Services.AddHostedService<PortGuardWorker>();
builder.Services.AddHostedService<DnsBlockPinWorker>();
builder.Services.AddHostedService<NamedPipeHost>();
builder.Services.AddHostedService<TrackerWatchdogWorker>();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DlpService");
var asService = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();
logger.LogInformation(
    "{Service} starting (enabled={Enabled}, dataDir={DataDir}, asService={AsService}, os={Os})",
    ServiceIdentity.ServiceName, cfg.Enabled, cfg.DataDir, asService,
    OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "other");

await host.RunAsync();

// Fleet private-network HTTPS (mirrors client.Core.TlsCertificateTrust — DlpService is a separate assembly).
static void ApplyFleetTls(HttpClientHandler handler)
{
    var insecure = Environment.GetEnvironmentVariable("ALPHA_TLS_INSECURE");
    if (insecure is "1" or "true" or "True" or "yes" or "YES" or "on" or "ON")
    {
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        return;
    }

    var caPath = Environment.GetEnvironmentVariable("ALPHA_TLS_CA")?.Trim();
    if (string.IsNullOrWhiteSpace(caPath) || !File.Exists(caPath))
    {
        var progData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlphaAITracker", "tls", "rootCA.pem");
        if (File.Exists(progData)) caPath = progData;
        else
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "tls", "rootCA.pem");
            caPath = File.Exists(beside) ? beside : null;
        }
    }

    if (string.IsNullOrWhiteSpace(caPath) || !File.Exists(caPath))
        return;

    var path = caPath;
    handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
    {
        if (cert == null) return false;
        if (errors == System.Net.Security.SslPolicyErrors.None) return true;
        try
        {
            using var ca = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(path);
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.ExtraStore.Add(ca);
            chain.ChainPolicy.VerificationFlags =
                System.Security.Cryptography.X509Certificates.X509VerificationFlags.AllowUnknownCertificateAuthority;
            chain.ChainPolicy.RevocationMode =
                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
            using var leaf = new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
            if (!chain.Build(leaf)) return false;
            foreach (var el in chain.ChainElements)
            {
                if (el.Certificate.Thumbprint.Equals(ca.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }
    };
}
