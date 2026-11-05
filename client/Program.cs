using System.Diagnostics;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using client;
using client.Configuration;
using client.Core;
using client.Core.Abstractions;
using client.Core.BrowserAccessibility;
using client.Core.DesktopEventBus;
using client.Services;
using client.Services.Watchers;
using client.Storage;
using client.ViewModels;

// ─── CLI modes (checked before anything else) ───
if (args.Contains("--encrypt-config"))
{
    // Build-time encryption: .env → config.enc using transport key
    // Usage: dotnet run --project client -- --encrypt-config [input] [output]
    var inputPath = args.ElementAtOrDefault(1) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
    var outputPath = args.ElementAtOrDefault(2) ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.enc");

    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"Error: .env not found at {inputPath}");
        return;
    }

    Console.WriteLine($"Encrypting {inputPath} → {outputPath} (AES-256-GCM)...");
    EnvLoader.EncryptToFile(inputPath, outputPath);
    Console.WriteLine($"Done: {new FileInfo(outputPath).Length} bytes written.");
    return;
}

// Headless diagnostic: load config exactly like a normal launch, then print the
// resolved values. Use this to verify which server URL / settings an INSTALLED
// build actually uses (plain `dotnet run` always reads .env, so it can't show
// what config.enc will load). EnvLoader prints the config source it picked, too.
if (args.Contains("--print-config"))
{
    EnvLoader.Load();
    var cfg = AppConfig.FromEnv();
    Console.WriteLine($"ServerUrl={cfg.ServerUrl}");
    Console.WriteLine($"BrowserTrackingEnabled={cfg.BrowserTrackingEnabled}");
    Console.WriteLine($"BrowserAccessibilityPollSec={cfg.BrowserAccessibilityPollSec}");
    Console.WriteLine($"BrowserJourneyIdleMinutes={cfg.BrowserJourneyIdleMinutes}");
    Console.WriteLine($"BrowserCaptureIncognito={cfg.BrowserCaptureIncognito}");
    Console.WriteLine($"BrowserHistoryEnabled={cfg.BrowserHistoryEnabled}");
    Console.WriteLine($"BrowserHistoryPollSec={cfg.BrowserHistoryPollSec}");
    Console.WriteLine($"FileJourneyEnabled={cfg.FileJourneyEnabled}");
    Console.WriteLine($"DbPath={cfg.DbPath}");
    Console.WriteLine($"ApiKeySet={!string.IsNullOrEmpty(cfg.ApiKey)}");
    Console.WriteLine($"SyncIntervalSec={cfg.SyncIntervalSec}");
    Console.WriteLine($"SyncMaxRows={cfg.SyncMaxRows}");
    Console.WriteLine($"SyncMaxBytes={cfg.SyncMaxBytes}");
    Console.WriteLine($"SyncCompression={cfg.SyncCompression}");
    Console.WriteLine($"SyncRetentionHours={cfg.SyncRetentionHours}");
    Console.WriteLine($"TaEnabled={cfg.TaEnabled}");
    Console.WriteLine($"IdleThresholdSeconds={cfg.IdleThresholdSeconds}");
    Console.WriteLine($"IdleAwayThresholdSeconds={cfg.IdleAwayThresholdSeconds}");
    Console.WriteLine($"IdlePollSeconds={cfg.IdlePollSeconds}");
    Console.WriteLine($"LockHysteresisSeconds={cfg.LockHysteresisSeconds}");
    Console.WriteLine($"EventAggregationWindowSec={cfg.EventAggregationWindowSec}");
    Console.WriteLine($"TaMaxLocalRows={cfg.TaMaxLocalRows}");
    Console.WriteLine($"TermsEnabled={cfg.TermsEnabled}");
    Console.WriteLine($"TermsCheckHours={cfg.TermsCheckHours}");
    Console.WriteLine($"LocationEnabled={cfg.LocationEnabled}");
    Console.WriteLine($"LocationIpFallback={cfg.LocationIpFallback}");
    Console.WriteLine($"LocationPollSec={cfg.LocationPollSec}");
    Console.WriteLine($"StreamEnabled={cfg.StreamEnabled}");
    Console.WriteLine($"StreamFps={cfg.StreamFps}");
    Console.WriteLine($"StreamMaxWidth={cfg.StreamMaxWidth}");
    Console.WriteLine($"StreamMaxBitrateKbps={cfg.StreamMaxBitrateKbps}");
    Console.WriteLine($"StreamKeyframeIntervalSec={cfg.StreamKeyframeIntervalSec}");
    Console.WriteLine($"StreamMinUplinkKbps={cfg.StreamMinUplinkKbps}");
    Console.WriteLine($"StreamVp8LagFrames={cfg.StreamVp8LagFrames}");
    Console.WriteLine($"StreamDebugSendDelayMs={cfg.StreamDebugSendDelayMs}");
    Console.WriteLine($"WsEnabled={cfg.WsEnabled}");
    Console.WriteLine($"WsPingSec={cfg.WsPingSec}");
    Console.WriteLine($"WsReconnectBaseSec={cfg.WsReconnectBaseSec}");
    return;
}

// Headless Linux capture smoke: portal ScreenCast → framed BGRA. Exits 0 after
// N frames (default 15) or 1 on timeout/error. Wayland may show a Share dialog
// once; restore token is persisted under ~/.local/share/alpha-ai-tracker/.
if (args.Contains("--smoke-linux-capture"))
{
    if (!OperatingSystem.IsLinux())
    {
        Console.Error.WriteLine("smoke-linux-capture: Linux only");
        Environment.Exit(2);
    }
    EnvLoader.Load();
    var cfg = AppConfig.FromEnv();
    using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
    var logger = loggerFactory.CreateLogger("smoke-linux-capture");
    var monitors = client.Services.Streaming.LinuxScreenCapturer.EnumerateMonitors(logger);
    Console.WriteLine($"monitors={monitors.Count}");
    foreach (var m in monitors)
        Console.WriteLine($"  [{m.Index}] {m.Name} {m.Width}x{m.Height} primary={m.IsPrimary}");

    using var capturer = new client.Services.Streaming.LinuxScreenCapturer(cfg, logger);
    capturer.SetSelectedMonitor(0);
    capturer.SetMaxWidth(Math.Min(640, cfg.StreamMaxWidth));
    capturer.Start();
    var want = 15;
    var got = 0;
    var deadline = DateTime.UtcNow.AddSeconds(45);
    while (got < want && DateTime.UtcNow < deadline)
    {
        if (capturer.TryDequeueLatest(out var frame) && frame is not null)
        {
            got++;
            Console.WriteLine($"frame#{got} {frame.Width}x{frame.Height} bytes={frame.Bgra.Length}");
        }
        else
            Thread.Sleep(50);
    }
    capturer.Stop();
    if (got < want)
    {
        Console.Error.WriteLine($"FAIL frames={got}/{want} err={capturer.LastError}");
        Environment.Exit(1);
    }
    Console.WriteLine($"OK frames={got}");
    Environment.Exit(0);
}

// Headless: verify uplink bitrate ladder math (no GUI / no network required).
if (args.Contains("--self-test-stream-ladder"))
{
    var failed = 0;
    void Expect(int uplink, int max, uint want, string label)
    {
        var got = NetProbeService.SelectBitrateKbps(uplink, max);
        if (got != want)
        {
            Console.Error.WriteLine($"FAIL {label}: uplink={uplink} max={max} → {got} (want {want})");
            failed++;
        }
        else
        {
            Console.WriteLine($"OK   {label}: uplink={uplink} → {got} kbps");
        }
    }

    // budget = uplink * 0.7; tiers 15k→12k, 8k→6k, 4k→3k, 2.5k→1.5k, 1.5k→1k, else 500
    Expect(25000, 12000, 12000, "fat-link capped at max");
    Expect(12000, 12000, 6000, "12 Mbps uplink → 6k tier");
    Expect(6000, 12000, 3000, "6 Mbps uplink → 3k tier");
    Expect(4000, 12000, 1500, "4 Mbps uplink → 1.5k tier");
    Expect(2000, 12000, 500, "2 Mbps uplink → 500 floor (thin)");
    Expect(1000, 12000, 500, "1 Mbps uplink → 500 floor");
    Expect(8000, 1000, 1000, "tier clamped by maxBitrate");

    if (failed > 0)
    {
        Console.Error.WriteLine($"stream ladder self-test: {failed} failure(s)");
        Environment.ExitCode = 1;
        return;
    }
    Console.WriteLine("stream ladder self-test: PASS");
    return;
}

// ─── CLI vs GUI console ownership (must be before EnvLoader noise) ───
// OutputType=Exe so PowerShell waits on `--protected-uninstall` / `--remove-account`.
// Every other launch frees the console so tracking/GUI has no black flash window.
var isRemoveAccount = args.Contains("--remove-account");
var isProtectedUninstall = args.Contains("--protected-uninstall");
var isTeardownCleanupElevated = args.Contains("--teardown-cleanup-elevated");
string? protectedUninstallCode = null;
// --code: required for protected-uninstall; optional/ignored for plain --remove-account.
if (isProtectedUninstall || isRemoveAccount)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--code", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            protectedUninstallCode = ProtectedUninstallService.NormalizeCode(args[i + 1]);
            break;
        }
        if (args[i].StartsWith("--code=", StringComparison.OrdinalIgnoreCase))
        {
            protectedUninstallCode = ProtectedUninstallService.NormalizeCode(args[i]["--code=".Length..]);
            break;
        }
    }
}

var isCliConsoleMode = isProtectedUninstall || isRemoveAccount || isTeardownCleanupElevated
    || args.Contains("--print-config")
    || args.Contains("--encrypt-config");
if (OperatingSystem.IsWindows())
{
    if (isCliConsoleMode || isProtectedUninstall || isRemoveAccount || isTeardownCleanupElevated)
        client.Services.CliConsole.Ensure();
    else
        client.Services.CliConsole.DetachForGui();
}

EnvLoader.Load();

// Elevated-only hosts/USB/DLP cleanup after unelevated protected-uninstall wrote teardown.ok.
// Spawned via UAC (runas) from WindowsTeardownCleanup.TryElevateCleanupViaUac.
if (isTeardownCleanupElevated)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("teardown-cleanup-elevated is Windows-only");
        Environment.ExitCode = 1;
        return;
    }

    if (!File.Exists(ProtectedUninstallService.TeardownOkPath))
    {
        Console.Error.WriteLine("teardown-cleanup-elevated refused — teardown.ok missing");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine("Elevated teardown cleanup (hosts + USBSTOR + DLP service)...");
    var cleanup = WindowsTeardownCleanup.RestoreUsbAndRemoveDlpService();
    foreach (var s in cleanup.Steps)
        Console.WriteLine("  · " + s);
    Console.WriteLine(
        $"usb={cleanup.UsbRestored} hostsCleared={cleanup.HostsCleared} " +
        $"dlpGone={cleanup.ServiceGone} summary={cleanup.Summary}");
    Environment.ExitCode = (cleanup.UsbRestored && cleanup.HostsCleared && cleanup.ServiceGone) ? 0 : 2;
    return;
}

// ─── Terms agent mode (instance 3, 2026-09-16) ───
// `client.exe --terms` runs a STANDALONE Terms & Conditions app: its own process,
// its own neutral GUI (TermsWindow), no tracking, no sync engine, no shell. It owns
// the whole T&C lifecycle — pull, diff, show, accept, sync consent — against the
// SAME local SQLite, then exits. The tracker GUI never shows terms itself; it
// spawns this agent when pending terms exist (TermsService.SpawnTermsAgent).
if (args.Contains("--terms"))
{
    await RunTermsAgentAsync(args);
    return;
}

// Legacy --celebrate-update: no UI — exit immediately (flag kept so old helpers no-op).
if (args.Contains("--celebrate-update") && !args.Contains("--background"))
    return;

// Roaming Control Shell / tracking popup — Avalonia-only agent.
if ((args.Contains("--control-shell") || args.Contains("--tracking-controls"))
    && !args.Contains("--background"))
{
    await RunTrackingControlsAgentAsync(args);
    return;
}

var isBackground = args.Contains("--background");
var isMinimized = args.Contains("--minimized");
var isGuiAgent = args.Contains("--gui");
// A "user launch" is one without --background / --minimized — i.e. the user
// explicitly opened the app (clicked the desktop entry / ran the binary).
// Only user launches should bring an already-running instance to the front;
// background/minimized relaunches (e.g. auto-start firing twice, or systemd
// Restart=always racing) must exit quietly so they never disturb the user.
var isTrackingControls = args.Contains("--tracking-controls") || args.Contains("--control-shell");
var isUserLaunch = !isBackground && !isMinimized && !isGuiAgent && !isTrackingControls
    && !args.Contains("--celebrate-update");

// L2: unsupervised `client --background` (autostart / menu / scripts) must hand
// off to the user systemd unit BEFORE taking the mutex. Restart=always only
// covers systemd's MainPID — Resources Kill of an app-scope process never reopens.
if (OperatingSystem.IsLinux()
    && isBackground
    && !isRemoveAccount
    && !isProtectedUninstall
    && LinuxUserSystemdUnit.TryHandoffIfUnsupervised())
{
    return;
}

if (isGuiAgent)
{
    // Separate shell process alongside the headless tracker (Chrome-like).
    client.Services.CliConsole.DetachForGui();
    App.GuiAgentMode = true;
    ClientProcessMode.GuiAgent = true;
}

// Fixed/Roaming boot gate (P0: always Full for tracker processes — fail-safe).
TrackingModeGate.Resolve(args);

// Tracker uses Global\<mutex>; GUI uses a sibling mutex so both can run.
var mutexName = isGuiAgent
    ? $@"Global\{AppInfo.AppMutex}-gui"
    : SingleInstanceService.MutexName;
var appMutex = new Mutex(true, mutexName, out var mutexCreated);
// When the CLI kills a stuck tracker and takes over, skip re-approve (code already used).
var coldCodeAlreadyApproved = false;
if (!mutexCreated)
{
    // ── --protected-uninstall while a tracker is already running ──
    // CLI verifies FIRST (instant 401). Then hands off stop→sync→wipe→DLP to the
    // running instance. If it does not heartbeat within 5s, kill it and take over.
    if (isProtectedUninstall)
    {
        if (string.IsNullOrWhiteSpace(protectedUninstallCode))
        {
            Console.Error.WriteLine("Usage: client --protected-uninstall --code <removal-code>");
            Console.Error.WriteLine("protected-uninstall failed");
            Environment.ExitCode = 1;
            client.Services.CliConsole.PauseBeforeExit(args);
            return;
        }

        ProtectedUninstallService.HandoffResult handoff = ProtectedUninstallService.HandoffResult.Failed;
        _ = client.Services.CliConsole.RunWithSpinnerAsync(
                "Protected uninstall",
                async (setPhase, ct) =>
                {
                    handoff = await ProtectedUninstallService.ApproveAndHandoffAsync(
                        protectedUninstallCode.Trim(), setPhase, ct);
                    // Always TakeOver or Failed — never wait on the other process.
                    return handoff != ProtectedUninstallService.HandoffResult.Failed;
                },
                successMessage: "Approved — finishing removal in this process...",
                failureMessage: "protected-uninstall failed")
            .GetAwaiter().GetResult();

        if (handoff == ProtectedUninstallService.HandoffResult.Failed)
        {
            Environment.ExitCode = 1;
            client.Services.CliConsole.PauseBeforeExit(args);
            return;
        }

        // TakeOver: siblings killed — their mutex is abandoned; WaitOne throws
        // AbandonedMutexException which still means WE now own it.
        coldCodeAlreadyApproved = true;
        if (!TryAcquireAbandonedMutex(appMutex, TimeSpan.FromSeconds(8)))
        {
            Console.Error.WriteLine("protected-uninstall failed — could not acquire tracker lock after stop");
            Environment.ExitCode = 1;
            client.Services.CliConsole.PauseBeforeExit(args);
            return;
        }
        mutexCreated = true;
        // fall through to host start + pipeline with codeAlreadyVerified
    }
    else if (isRemoveAccount)
    {
        var finished = false;
        var ok = client.Services.CliConsole.RunWithSpinnerAsync(
                "Removing account",
                async (setPhase, ct) =>
                {
                    finished = await ProtectedUninstallService.WaitForRemoveAccountAsync(setPhase, ct);
                    return finished;
                },
                successMessage: "remove-account successfully",
                failureMessage: "remove-account — taking over locally")
            .GetAwaiter().GetResult();

        if (finished && ok)
        {
            Environment.ExitCode = 0;
            client.Services.CliConsole.PauseBeforeExit(args);
            return;
        }

        // Take over after kill (WaitForRemoveAccountAsync kills on stuck / no peer work).
        // Retry briefly — Windows can hold the abandoned mutex a beat after Kill.
        if (!TryAcquireAbandonedMutex(appMutex, TimeSpan.FromSeconds(15)))
        {
            ProtectedUninstallService.KillOtherClientProcesses();
            if (!TryAcquireAbandonedMutex(appMutex, TimeSpan.FromSeconds(8)))
            {
                Console.Error.WriteLine("remove-account failed — could not acquire tracker lock");
                Environment.ExitCode = 1;
                client.Services.CliConsole.PauseBeforeExit(args);
                return;
            }
        }
        mutexCreated = true;
        // fall through — isRemoveAccount cold path
    }
    else
    {
        // ── --restart (post-update relaunch) ──
        // The updater launches us with --restart and then shuts the OLD process down
        // so the newly-installed binary can take over. The old process may still own
        // the mutex for a moment, so instead of treating this like a normal second
        // launch (signal-and-exit), wait up to 8s for it to release ownership.
        if (args.Contains("--restart"))
        {
            if (!TryAcquireAbandonedMutex(appMutex, TimeSpan.FromSeconds(8)))
                return;
            mutexCreated = true;
        }
        else
        {
            // Another instance already owns the mutex — the persistent background
            // instance launched by auto-start / systemd, or a previously-opened GUI
            // that is hidden in the tray. Instead of exiting silently (which was
            // making the GUI "open and instantly close"), ask that instance to show
            // its window, then exit. This lets the user open the GUI as many times
            // as they want without ever stopping the running background tracker.
            // Tracker holds Global\<mutex>. Spawn separate --gui (never Avalonia in tracker).
            Console.Error.WriteLine("Another instance is already running.");
            if (isUserLaunch)
                LaunchSeparateGuiProcess();
            return;
        }
    }
}

// We are the primary (mutex-owning) instance: start listening for SHOW
// requests from any future second launch so it can bring our window forward.
SingleInstanceService.StartServer();

// Cold-start protected-uninstall: APPROVE only before DI host (instant fail),
// unless we already approved during handoff takeover (code is one-time).
if (isProtectedUninstall && !coldCodeAlreadyApproved)
{
    CliConsole.Ensure();
    if (string.IsNullOrWhiteSpace(protectedUninstallCode))
    {
        Console.Error.WriteLine("Usage: client --protected-uninstall --code <removal-code>");
        Console.Error.WriteLine("protected-uninstall failed");
        Environment.ExitCode = 1;
        CliConsole.PauseBeforeExit(args);
        return;
    }

    var approved = CliConsole.RunWithSpinnerAsync(
            "Approving with server",
            (setPhase, ct) => ProtectedUninstallService.ApproveAsync(
                protectedUninstallCode.Trim(), "both", ct, setPhase),
            successMessage: "Approved.",
            failureMessage: "protected-uninstall failed — issue a fresh removal code on the web and retry.")
        .GetAwaiter().GetResult();
    if (!approved)
    {
        Environment.ExitCode = 1;
        CliConsole.PauseBeforeExit(args);
        return;
    }

    coldCodeAlreadyApproved = true;
}

var config = AppConfig.FromEnv();

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// CLI removal must stay readable: suppress hosted-service chatter that drowned
// the spinner and looked like a hang ("IdleDetector starting…") during uninstall.
var logLevel = (isProtectedUninstall || isRemoveAccount)
    ? LogLevel.Warning
    : config.LogLevel?.ToLowerInvariant() switch
    {
        "verbose" => LogLevel.Trace,
        "debug"   => LogLevel.Debug,
        "info"    => LogLevel.Information,
        "warn"    => LogLevel.Warning,
        "error"   => LogLevel.Error,
        _         => LogLevel.Information
    };
builder.Logging.SetMinimumLevel(logLevel);

// Log file location. In dev (`dotnet run`) the app dir (bin/...) is writable,
// so the log lands next to the build output as before. When installed via the
// .deb, the app dir is /usr/share/alpha-ai-tracker — root-owned and READ-ONLY
// for a normal user. Writing dotnetrunlog.txt there throws
// UnauthorizedAccessException from the FileLoggerProvider ctor, which aborts
// startup before the GUI opens (the "open and instantly close" symptom).
// Probe writability and fall back to the user-writable data dir (the same
// place the SQLite db lives) when the install dir is not writable.
var logFilePath = ResolveLogPath();
builder.Logging.AddFile(logFilePath);

builder.Services.AddSingleton(config);

// SQLite Log Store
builder.Services.AddSingleton<ILogStore>(sp =>
{
    // Lazy provider: after `--remove-account` resets ClientId, a re-login in this same
    // process must persist the NEW machine id, not the retired one (R2/R7).
    return new SqliteLogStore(ResolveDbPath(config.DbPath), config.DbEncryptionKey, null, () => config.ClientId);
});

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, finalplan section 3 / R7): the ShutdownSentinel
// is registered FIRST so it is StartAsync'd first and StopAsync'd last by the
// .NET host. That ordering guarantees the sentinel's ApplicationStopping hook
// fires before any other hosted service is torn down (including the SQLite
// store). The IEventRecorder that the sentinel writes through is registered
// immediately after so the dependency is satisfied.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.Watchers.ShutdownSentinel>();
builder.Services.AddSingleton<IEventRecorder, SessionEventRecorder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.Watchers.ShutdownSentinel>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.3): SystemEventWatcher subscribes to OS power /
// lock / sleep / login signals and writes them through the IEventRecorder. It
// runs as a hosted service so the host's lifecycle (StartAsync/StopAsync) governs
// its D-Bus / SystemEvents subscriptions.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.Watchers.SystemEventWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.Watchers.SystemEventWatcher>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.4): IdleDetector polls the OS idle source
// (Mutter.IdleMonitor / XScreenSaver / GetLastInputInfo) and emits idle_start /
// idle_end threshold crossings through the IEventRecorder.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.IdleDetector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.IdleDetector>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.7): LocalTimeSkewService measures the client
// clock's skew against the server's HTTP Date header every 15 min and stores
// it per server URL (BUG-7 + BUG-12 fix). No-op when ALPHA_TA_ENABLED=false.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.LocalTimeSkewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.LocalTimeSkewService>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.6): ScheduleCacheService mirrors the employee's
// shift + holidays from GET /api/v1/schedules/me every 6h (BUG-6 fix). No-op
// when ALPHA_TA_ENABLED=false or the Phase 2 endpoint is absent (404).
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.ScheduleCacheService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.ScheduleCacheService>());

// ────────────────────────────────────────────────────────────────────────────
// Terms & Conditions acceptance gate (2026-09-16): TermsService pulls the server's
// active terms (GET /terms-content/active, DeviceAuth), diffs them into the local
// client_terms table and records consent via POST /terms-consent/sync. The
// MainViewModel router raises the locked TermsPage while pending terms exist.
// No-op when ALPHA_TERMS_ENABLED=false.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.TermsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.TermsService>());

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, A.8): AttendanceAggregator rolls up today's
// session-idle activity into daily_attendance_cache every 5 min. Reads use the
// read-only connection (R8). No-op when ALPHA_TA_ENABLED=false.
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.AttendanceAggregator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.AttendanceAggregator>());

// ────────────────────────────────────────────────────────────────────────────
// Phase 3 GPS (finalplan §16 B.1): LocationSamplerService polls OS location /
// IP geolocation on ALPHA_LOCATION_POLL_SEC. Default OFF (ALPHA_LOCATION_ENABLED).
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.LocationSamplerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.LocationSamplerService>());

// ────────────────────────────────────────────────────────────────────────────
// Live stream (Phase 1): ScreenCaptureService + LiveStreamClient push socket.
// Parked when ALPHA_STREAM_ENABLED=false. Frames only after server "start".
// ────────────────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<client.Services.ScreenCaptureService>();
builder.Services.AddSingleton<client.Services.NetProbeService>();
builder.Services.AddSingleton<client.Services.LiveStreamClient>();
// Skip live stream / capture loops during CLI removal — they only fight teardown.
if (!isProtectedUninstall && !isRemoveAccount)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.ScreenCaptureService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.LiveStreamClient>());
}

// HTTP Client
builder.Services.AddSingleton<HttpClient>(sp =>
{
    var client = new HttpClient();
    client.Timeout = TimeSpan.FromSeconds(30);
    return client;
});

// Installed App Detector (cross-platform)
builder.Services.AddSingleton<IInstalledAppDetector, InstalledAppDetector>();

// Package Detector (cross-platform — npm, pip, apt, brew, etc.)
builder.Services.AddSingleton<IPackageDetector, PackageDetector>();

// Platform-specific services
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Windows.ProcessCollector(config.ClientId));
}
else if (OperatingSystem.IsLinux())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.Linux.ProcessCollector(config.ClientId));
}
else if (OperatingSystem.IsMacOS())
{
    builder.Services.AddSingleton<IActivityCollector>(
        _ => new client.Platform.MacOS.ProcessCollector(config.ClientId));
}
else
{
    throw new PlatformNotSupportedException($"Unsupported OS: {Environment.OSVersion}");
}

// Background services (order matters: guard first for most resilience).
// Protected-uninstall / remove-account must NOT start the watchdog — it reinstalls
// the Run key while we are trying to tear the product down (root cause of
// auto-start surviving a "successful" uninstall).
builder.Services.AddSingleton<AutoStartService>();
if (!isProtectedUninstall && !isRemoveAccount)
    builder.Services.AddHostedService<BackgroundGuardService>();
builder.Services.AddSingleton<LogCollectorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogCollectorService>());

// Dedicated sync engine — runs on its OWN loop so collection never blocks on the network.
// Drains unsent rows in adaptive byte-bounded chunks (gzip, polite pauses, backoff); a
// 50k+ row backlog drains in minutes without spiking CPU or adding collection latency.
// Registered as a singleton + hosted so the login flow can inject it and trigger an
// IMMEDIATE drain the moment credentials are persisted (RequestImmediateSync).
builder.Services.AddSingleton<SyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncService>());
builder.Services.AddSingleton<TrackingEventReporter>();

// P2 Roaming Stop follower — PauseTracking + drain + purge when desired=stopped.
if (!isProtectedUninstall && !isRemoveAccount)
    builder.Services.AddHostedService<TrackingControlFollower>();

// Linux/macOS Fixed/Roaming control poll (Windows keeps this in DlpService).
if (!isProtectedUninstall && !isRemoveAccount && !OperatingSystem.IsWindows())
    builder.Services.AddHostedService<DeviceControlPollWorker>();

// Linux: keep roaming Start/Stop modal open (Windows = DlpService TrackerWatchdogWorker).
if (!isProtectedUninstall && !isRemoveAccount && OperatingSystem.IsLinux())
    builder.Services.AddHostedService<TrackingControlsSupervisor>();

// Account removal (R4/R5/R6, 2026-10-19): single routine shared by the `--remove-account`
// CLI flow, the running-instance signal handler and the server-initiated wipe
// ({"action":"wipe_local"}). It drains, calls /devices/self-remove, then wipes.
builder.Services.AddSingleton<AccountRemovalService>();
builder.Services.AddSingleton<ProtectedUninstallService>();

// Control WebSocket (presence / keep-alive). Parked when ALPHA_WS_ENABLED=false.
// Connects only after SyncService proves a 2xx (idle timeout fallback + reconnect).
// Registered after SyncService so the sync loop is running before we wait on it.
builder.Services.AddSingleton<client.Services.WsClient>();
builder.Services.AddSingleton<AppUpdateService>();
if (!isProtectedUninstall && !isRemoveAccount)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.WsClient>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AppUpdateService>());
}

// Near-real-time install/uninstall detection: watches .desktop/dpkg/Start Menu//Applications
// and triggers an immediate inventory rescan on change — no GUI interaction needed.
if (config.InventoryWatchEnabled && !isProtectedUninstall && !isRemoveAccount)
{
    builder.Services.AddHostedService<InstalledSoftwareWatcher>();
}

// Browser Journey (Option B — accessibility-based: reads the OS accessibility tree.
// No debugger, no extension, no browser catalog dependency; works on every browser
// and every Chrome version, including install→use→uninstall-in-5-min scenarios.)
//
// IBrowserRegistry is registered unconditionally because LogCollectorService and
// SessionLabelResolver consume it regardless of the browser-tracking master switch.
builder.Services.AddSingleton<IBrowserRegistry, BrowserRegistry>();

if (config.BrowserTrackingEnabled)
{
    builder.Services.AddSingleton<IAccessibilityBrowserReader>(sp =>
        AccessibilityBrowserReaderFactory.Create(
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IBrowserRegistry>()));
    // Hybrid URL fallback: reads the browser's own profile history DB when the a11y
    // tree cannot expose the omnibox (Linux Chrome 136+ / snap Firefox). No restart,
    // no flag, no extension; works on all platforms and browsers incl. brand-new ones.
    builder.Services.AddSingleton<BrowserHistoryReader>();
    if (!isProtectedUninstall && !isRemoveAccount)
    {
        builder.Services.AddHostedService<AccessibilityBrowserTracker>();
    }
}

// Desktop Event Bus (File Explorer tracking via AT-SPI on Linux, Shell COM on Windows,
// plus FileSystemWatcher + recent-files sources on every platform). Master switch:
// ALPHA_FILE_JOURNEY_ENABLED — when false, no file-journey data is collected at all.
if (config.FileJourneyEnabled)
{
    builder.Services.AddSingleton<EventCoordinator>();
    builder.Services.AddSingleton<JourneyEngine>();
    builder.Services.AddSingleton<ATSPIEventWatcher>();
    builder.Services.AddSingleton<WindowsExplorerWatcher>();
    builder.Services.AddSingleton<IExplorerWindowProvider>(
        sp => sp.GetRequiredService<WindowsExplorerWatcher>());
    builder.Services.AddSingleton<FileSystemEventWatcher>();
    builder.Services.AddSingleton<RecentFilesWatcher>();
    if (!isProtectedUninstall && !isRemoveAccount)
        builder.Services.AddHostedService<DesktopEventService>();
}

// USB / peripheral hotplug tracker (local SQLite only; no server sync yet)
if (config.HardwareDevicesEnabled && !isProtectedUninstall && !isRemoveAccount)
{
    builder.Services.AddHostedService<HardwareDeviceWatcherService>();
}

// Main ViewModel + the per-page ViewModels it composes (pages 4–6).
// Transient alongside MainViewModel so each window gets its own page state.
builder.Services.AddTransient<DashboardViewModel>();
builder.Services.AddTransient<SystemSpecsViewModel>();
builder.Services.AddTransient<InstalledAppsViewModel>();
builder.Services.AddTransient<MainViewModel>();

var host = builder.Build();

// ────────────────────────────────────────────────────────────────────────────
// Time & Attendance (Phase 1, finalplan section 2.6 / R7): wire the sentinel to
// the IHostApplicationLifetime AFTER the host is built (the lifetime is
// created during host.StartAsync, so we have to reach into Services now).
// The Console.CancelKeyPress hook is also installed here so a Ctrl+C in a
// terminal emits the power_off event before the host's normal stop path.
// ────────────────────────────────────────────────────────────────────────────
var shutdownSentinel = host.Services.GetRequiredService<client.Services.Watchers.ShutdownSentinel>();
shutdownSentinel.HookConsoleCancelKeyPress();

try
{
    // Initialize SQLite before any hosted service starts. SystemEventWatcher is
    // intentionally first in DI and emits power_on immediately; starting the
    // host before the store was ready made that write a silent no-op, while its
    // dedup bucket then suppressed LogCollectorService's valid retry.
    await host.Services.GetRequiredService<ILogStore>()
        .InitializeAsync(CancellationToken.None);

    // ─── Account removal wiring (R4/R5/R6, 2026-10-19) ───────────────────────
    // Wire BEFORE the host starts so a server-initiated wipe can never be missed
    // (SyncService raises OnServerRequestedWipe at most once per process).
    var syncService = host.Services.GetRequiredService<SyncService>();
    var accountRemoval = host.Services.GetRequiredService<AccountRemovalService>();

    // R6: an admin removed this device; the server answered a sync with
    // {"action":"wipe_local"} AFTER persisting that batch. Run the canonical removal
    // routine in the background — it must not block the sync loop it was raised from.
    syncService.OnServerRequestedWipe += () =>
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Admin remove already accepted on the server — Fixed gate does not apply.
                await accountRemoval.RunAsync(
                    CancellationToken.None, allowServerInitiated: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[client] Server-requested wipe failed: {ex.Message}");
            }
        });
    };

    // After ANY wipe (CLI or server-initiated) let the shell react: a live GUI resets to
    // the sign-in page, a headless tracker raises the lazy GUI. Reusing the existing
    // REMOVE_ACCOUNT handler keeps one UI reaction path; it is idempotent because
    // AccountRemovalService short-circuits once the wipe has happened.
    accountRemoval.WipeCompleted += () =>
    {
        App.PendingRemoveAccount = true;
        try
        {
            SingleInstanceService.OnRemoveAccountRequested?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[client] Post-wipe UI reaction failed: {ex.Message}");
        }
    };

    await host.StartAsync(CancellationToken.None);

    // The lifetime is now available - wire the ApplicationStopping hook.
    shutdownSentinel.HookLifetime(host.Services.GetRequiredService<IHostApplicationLifetime>());

    // Set service provider for App to resolve ViewModels from DI
    App.ServiceProvider = host.Services;

    // ─── Cold-start / background `client --remove-account` (R4/R5) ────────────
    // The host is running now, so the sync engine is alive and the final drain is
    // real (previously this fired at a host that had not been started — the drain
    // never ran and the server was never told). AccountRemovalService stops
    // collection, waits for the drain, POSTs /devices/self-remove, then wipes the
    // local DB + machine id. Killed siblings and a clean shutdown follow.
    if (isProtectedUninstall || isRemoveAccount)
    {
        CliConsole.Ensure();
        if (isProtectedUninstall)
        {
            var protectedSvc = host.Services.GetRequiredService<ProtectedUninstallService>();
            TeardownReport? report = null;
            var ok = await CliConsole.RunWithSpinnerAsync(
                "Protected uninstall",
                async (setPhase, ct) =>
                {
                    report = await protectedSvc.RunPipelineDetailedAsync(
                        protectedUninstallCode!,
                        "both",
                        ct,
                        setPhase,
                        codeAlreadyVerified: coldCodeAlreadyApproved);
                    // Spinner "ok" line only for full verified success; partial uses exit 2.
                    return report.FullLocalSuccess;
                },
                successMessage: "protected-uninstall successfully",
                failureMessage: "protected-uninstall incomplete or failed");

            report ??= protectedSvc.LastReport;
            if (report is not null)
            {
                Console.WriteLine(report.SummaryLine);
                Console.WriteLine(
                    $"account={report.AccountRemoved} serverNotified={report.ServerNotified} " +
                    $"gate={report.GateWritten} pipeUnlock={report.PipeUnlockOk} " +
                    $"usb={report.UsbRestored} hostsCleared={report.HostsCleared} " +
                    $"dlpGone={report.DlpServiceGone} autoStartCleared={report.AutoStartCleared}");
                if (report.GateWritten)
                    Console.WriteLine($"Gate: {ProtectedUninstallService.TeardownOkPath}");
                if (report.FullLocalSuccess)
                    Console.WriteLine("You can now uninstall from Apps & features.");
                else if (report.AccountRemoved && report.CleanupPendingElevation)
                    Console.Error.WriteLine(
                        "Re-run this command from an elevated (Administrator) prompt, " +
                        "or finish removal via Apps & features (gate already written).");
                Environment.ExitCode = report.ExitCode;
            }
            else
            {
                Environment.ExitCode = ok ? 0 : 1;
            }
        }
        else
        {
            // Plain --remove-account: bounded drain (if online) → self-remove → wipe.
            // Offline / unreachable server: wipe anyway. Fixed mode does not block this;
            // product uninstall still needs --protected-uninstall --code.
            var notified = false;
            var ok = await CliConsole.RunWithSpinnerAsync(
                "Removing account (drain + notify server + wipe)",
                async ct =>
                {
                    notified = await accountRemoval.RunAsync(ct);
                    return true;
                },
                successMessage: "remove-account successfully",
                failureMessage: "remove-account failed");
            if (ok)
            {
                Console.WriteLine(notified
                    ? "Server notified — this machine is marked old and the employee is untracked."
                    : "Server could not be reached — local account wiped (unsynced rows may be lost).");
                Environment.ExitCode = 0;
            }
            else
            {
                Environment.ExitCode = 1;
            }
        }

        // Cold start must not leave a sibling tracker behind (the mutex branch above
        // handles the already-running case by signalling instead).
        try
        {
            var currentId = Environment.ProcessId;
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("client"))
            {
                if (proc.Id != currentId)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
            foreach (var proc in System.Diagnostics.Process.GetProcessesByName("AlphaAI"))
            {
                if (proc.Id != currentId)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[remove-account] Failed to kill sibling processes: {ex.Message}");
        }

        // Bounded stop — a hung hosted service must not leave the CLI spinner forever.
        try
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await host.StopAsync(stopCts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[uninstall] Host stop: {ex.Message}");
        }
        shutdownSentinel.PowerOffWritten.Wait(TimeSpan.FromSeconds(2));
        try { host.Dispose(); } catch { /* ignore */ }

        // After a successful local wipe, relaunch the tracker signed-out (login page).
        // Detached handoff waits for this PID so the Global\ mutex is free first.
        if (isRemoveAccount && Environment.ExitCode == 0)
            ScheduleRemoveAccountRelaunch();

        CliConsole.PauseBeforeExit(args);
        return;
    }

    if (isBackground)
    {
        // Headless tracker: NEVER initialize Avalonia here. The shell is a separate
        // `client.exe --gui` process. Avalonia allows one StartWithClassicDesktopLifetime
        // per process.
        SingleInstanceService.OnShowRequested = LaunchSeparateGuiProcess;

        // Roaming Start/Stop: Windows DLP TrackerWatchdogWorker launches
        // --tracking-controls; Linux TrackingControlsSupervisor (this host) does.

        // ─── Account removal while running HEADLESS (client --remove-account) ───
        // This process owns the SQLite connection, so the routine runs HERE, in place:
        // stop collecting → bounded drain → POST /devices/self-remove → wipe + new
        // machine id on next login (AccountRemovalService). App.PendingRemoveAccount
        // additionally tells a GUI that is still booting to reconcile the state it may
        // have read before the wipe (App.axaml.cs); once the GUI is live it replaces
        // this handler with its own, which resets the shell.
        SingleInstanceService.OnRemoveAccountRequested = () =>
        {
            _ = Task.Run(() =>
            {
                try
                {
                    accountRemoval.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
                    RemovalProgress.Write("done", "remove-account successfully");
                    Console.WriteLine("remove-account successfully");
                }
                catch (Exception ex)
                {
                    RemovalProgress.Write("failed", ex.Message);
                    Console.Error.WriteLine($"remove-account failed — {ex.Message}");
                }

                App.PendingRemoveAccount = true;
                LaunchSeparateGuiProcess();
            });
        };

        var protectedUninstall = host.Services.GetRequiredService<ProtectedUninstallService>();
        SingleInstanceService.OnProtectedUninstallRequested = code =>
        {
            // Background thread — must not block the pipe listener; CLI polls RemovalProgress.
            _ = Task.Run(async () =>
            {
                try
                {
                    // Empty code / approved marker ⇒ CLI already verified (GO command).
                    var already = string.IsNullOrEmpty(code) ||
                                  ProtectedUninstallService.HasApprovedMarker();
                    var ok = await protectedUninstall.RunPipelineAsync(
                        code ?? "", "both", CancellationToken.None,
                        codeAlreadyVerified: already);
                    Console.WriteLine(ok
                        ? "protected-uninstall successfully"
                        : "protected-uninstall failed — check the removal code.");
                }
                catch (Exception ex)
                {
                    RemovalProgress.Write("failed", ex.Message);
                    Console.Error.WriteLine($"protected-uninstall failed — {ex.Message}");
                }

                App.PendingRemoveAccount = true;
                LaunchSeparateGuiProcess();
            });
        };
        // WaitForShutdownAsync is tied to IHostApplicationLifetime. The previous
        // uncancellable Task.Delay kept the process alive after SIGTERM even though
        // ConsoleLifetime had fired ApplicationStopping; systemd eventually had to
        // SIGKILL it and the code below never ran.
        await host.WaitForShutdownAsync();
    }
    else
    {
        // Interactive / --gui / --minimized: Avalonia in THIS process.
        if (isGuiAgent || isUserLaunch || isMinimized)
            client.Services.CliConsole.DetachForGui();
        App.LaunchedHidden = isMinimized;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        await host.StopAsync(CancellationToken.None);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Time & Attendance (Phase 1, R7): the ShutdownSentinel writes the power_off
    // event inside its ApplicationStopping handler. host.StopAsync() returns
    // AFTER the sentinel's StopAsync runs (reverse DI order), but the SQL write
    // inside the sentinel is async; the SQLite store's Dispose() may run on
    // a parallel path. Wait on the sentinel's MRE with a hard 3-second ceiling
    // (slightly above the recorder's 2s internal timeout) to guarantee the
    // write finishes before we move into the finally block that disposes the
    // single-instance service and the mutex.
    // ────────────────────────────────────────────────────────────────────────────
    shutdownSentinel.PowerOffWritten.Wait(TimeSpan.FromSeconds(3));
    host.Dispose();
}
finally
{
    SingleInstanceService.StopServer();
    appMutex.Dispose();
}

/// <summary>
/// After we kill the process that owned the single-instance mutex, WaitOne throws
/// <see cref="AbandonedMutexException"/> — that still means this process acquired it.
/// Never let that exception escape as an unhandled crash (and never dump source paths).
/// </summary>
static bool TryAcquireAbandonedMutex(Mutex mutex, TimeSpan timeout)
{
    try
    {
        return mutex.WaitOne(timeout);
    }
    catch (AbandonedMutexException)
    {
        return true;
    }
}

static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

/// <summary>
/// Start the Avalonia shell in a <b>new</b> process so the headless tracker never
/// loads Avalonia. Suppresses the console flash (OutputType=Exe).
/// </summary>
static void LaunchSeparateGuiProcess()
{
    try
    {
        TrackingControlsActivation.RefreshLinuxDesktopEnvironment();
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = "--gui",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[client] Failed to launch GUI process: {ex.Message}");
    }
}

/// <summary>
/// Roaming Start/Stop modal — own process + tray; must not take the tracker mutex.
/// </summary>
static async Task RunTrackingControlsAgentAsync(string[] args)
{
    // Absolute separate instance: no SQLite host, no MainWindow.
    // Stays alive in its own tray after close so the watchdog does not relaunch.
    // Global mutex so the LocalSystem watchdog can see the UI agent.
    client.Services.CliConsole.DetachForGui();
    using var agentMutex = new Mutex(
        true, client.Services.TrackingControlsActivation.MutexName, out var created);
    if (!created)
    {
        // Already running (probably hidden in tray) — ask it to show, then exit.
        client.Services.TrackingControlsActivation.TrySignalShow();
        return;
    }

    App.TrackingControlsMode = true;
    client.Services.TrackingControlsActivation.StartServer();

    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    var host = builder.Build();
    App.ServiceProvider = host.Services;
    await host.StartAsync(CancellationToken.None);

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    client.Services.TrackingControlsActivation.StopServer();
    try
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await host.StopAsync(stopCts.Token);
    }
    catch { /* ignore */ }
    host.Dispose();
    Environment.Exit(0);
}

// ─── Terms agent (instance 3) ───
// Minimal host: config + SQLite + HttpClient + TermsService ONLY. None of the
// tracking/sync/inventory services are registered — this process is exclusively
// the T&C app. Single-instanced by its OWN mutex (derived from AppInfo.AppMutex)
// so it runs ALONGSIDE the tracker instance without tripping its guard.
static async Task RunTermsAgentAsync(string[] args)
{
    using var agentMutex = new Mutex(true, client.Core.AppInfo.AppMutex + "-terms-agent", out var agentCreated);
    if (!agentCreated)
    {
        return; // an agent instance is already showing terms
    }

    client.Services.TermsService.IsAgentMode = true;

    var config = client.Configuration.AppConfig.FromEnv();
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddFile(ResolveLogPath());
    builder.Logging.SetMinimumLevel(LogLevel.Information);

    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<ILogStore>(sp =>
        new SqliteLogStore(ResolveDbPath(config.DbPath), config.DbEncryptionKey, null, () => config.ClientId));
    builder.Services.AddSingleton<HttpClient>(sp => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
    builder.Services.AddSingleton<client.Services.TermsService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<client.Services.TermsService>());
    builder.Services.AddTransient<client.ViewModels.TermsViewModel>();

    var host = builder.Build();

    await host.Services.GetRequiredService<ILogStore>()
        .InitializeAsync(CancellationToken.None);

    App.ServiceProvider = host.Services;
    App.TermsAgentMode = true;

    await host.StartAsync(CancellationToken.None);

    // Own Avalonia lifetime — when the terms window closes (all accepted or user
    // dismissed), StartWithClassicDesktopLifetime returns and the process exits.
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // The window is gone — the agent's job is done. Stop the host with a BOUNDED
    // wait and then exit deterministically: a hung hosted-service stop must never
    // leave a zombie windowless agent process behind (2026-09-16 — the agent
    // outlived its own closed window because the unbounded StopAsync blocked).
    // Safe here: the agent has no collector/sync in flight, and consent writes
    // are already flushed synchronously at accept time.
    try
    {
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.StopAsync(stopCts.Token);
    }
    catch { /* stop failures are irrelevant for the agent */ }
    host.Dispose();
    Environment.Exit(0);
}

// ─── Log path resolution ───
static string ResolveDbPath(string dbPath)
{
    if (Path.IsPathRooted(dbPath)) return dbPath;
    var baseDir = OperatingSystem.IsWindows()
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphaAITracker")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "alpha-ai-tracker");
    return Path.Combine(baseDir, dbPath);
}

// ─── Log path resolution ───
// Prefer the app dir (bin/... in dev, so the log sits next to the build
// output). When that dir is not writable (installed .deb → root-owned
// /usr/share/alpha-ai-tracker), use the user-writable data dir instead so
// the FileLoggerProvider never fails to open its file. The FileLoggerProvider
// itself is also defensive — this just picks a good path up front.
static string ResolveLogPath()
{
    var appDir = AppDomain.CurrentDomain.BaseDirectory;
    if (IsDirWritable(appDir))
        return Path.Combine(appDir, "dotnetrunlog.txt");

    var userDataDir = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlphaAITracker")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "alpha-ai-tracker");
    return Path.Combine(userDataDir, "dotnetrunlog.txt");
}

static bool IsDirWritable(string dir)
{
    try
    {
        if (string.IsNullOrEmpty(dir)) return false;
        // Create + delete a throwaway marker file. This is the only reliable
        // cross-platform writability test (ACLs / root ownership / read-only
        // mounts all surface here without actually touching the real log file).
        var marker = Path.Combine(dir, ".aat_write_probe_" + Guid.NewGuid().ToString("N"));
        using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { }
        try { File.Delete(marker); } catch { }
        return true;
    }
    catch
    {
        return false;
    }
}

/// <summary>
/// After <c>--remove-account</c> wipes local state, start a fresh tracker once this
/// process exits (mutex released). Linux: tiny shell wait-loop. Windows: powershell.
/// </summary>
static void ScheduleRemoveAccountRelaunch()
{
    try
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = OperatingSystem.IsLinux() ? "/usr/bin/alpha-ai-tracker" : exe;
        if (string.IsNullOrWhiteSpace(exe)) return;

        var pid = Environment.ProcessId;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"aat_relaunch_{Guid.NewGuid():N}.sh");
            var script = $@"#!/bin/sh
PID={pid}
EXE=""{exe}""
COUNT=0
while kill -0 ""$PID"" 2>/dev/null; do
  sleep 0.25
  COUNT=$((COUNT+1))
  if [ ""$COUNT"" -ge 80 ]; then break; fi
done
sleep 0.5
if [ -x ""$EXE"" ]; then
  ""$EXE"" --background --restart &
elif [ -x /usr/bin/alpha-ai-tracker ]; then
  /usr/bin/alpha-ai-tracker --background --restart &
fi
rm -f ""$0"" 2>/dev/null
";
            File.WriteAllText(scriptPath, script.Replace("\r\n", "\n"));
            try
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort chmod */ }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Console.WriteLine("Tracker will relaunch signed-out after this window closes.");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var ps = $"while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 250 }}; Start-Sleep -Milliseconds 500; Start-Process -FilePath '{exe.Replace("'", "''")}' -ArgumentList '--background','--restart'";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -Command \"{ps.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Console.WriteLine("Tracker will relaunch signed-out after this window closes.");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[remove-account] Relaunch schedule failed: {ex.Message}");
    }
}
