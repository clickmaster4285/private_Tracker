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
});
builder.Services.AddHttpClient("update-download", c =>
{
    // Multi-hundred-MB installers — same ceiling as the Avalonia AppUpdateService.
    c.Timeout = TimeSpan.FromMinutes(15);
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
