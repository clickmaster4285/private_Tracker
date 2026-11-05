using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using client.Core;
using client.Services;
using client.ViewModels;
using client.Views;
using Microsoft.Extensions.DependencyInjection;

namespace client;

public partial class App : Application
{
    public static IServiceProvider? ServiceProvider { get; internal set; }
    public static bool AllowShutdown { get; set; }

    /// <summary>
    /// Roaming Control Shell / Start–Stop popup only (<c>--control-shell</c> /
    /// <c>--tracking-controls</c>). Separate process so the headless tracker never
    /// initializes Avalonia.
    /// </summary>
    public static bool TrackingControlsMode { get; set; }

    /// <summary>
    /// Separate GUI process (<c>--gui</c>): Avalonia shell alongside the headless
    /// tracker. Collection/sync stay in the <c>--background</c> process — this flag
    /// parks duplicate hosted services so two collectors never fight over SQLite.
    /// </summary>
    public static bool GuiAgentMode { get; set; }

    /// <summary>
    /// True when the UI is being created HIDDEN (auto-start / systemd boot instance
    /// launched with --background/--minimized — no window at boot). A manual user
    /// launch sends a SHOW signal and the lazy GUI path clears this before starting
    /// Avalonia, so the window appears only when the user actually opens the app.
    /// </summary>
    public static bool LaunchedHidden { get; set; }

    /// <summary>
    /// True while an account removal is waiting for a GUI to exist.
    ///
    /// <c>client --remove-account</c> on a machine whose tracker runs headless
    /// (--background under systemd / auto-start) asks the running instance to remove
    /// the account. Program.cs sets this flag and starts the lazy GUI; the GUI's boot
    /// consumes it before the window is created, so the tool opens directly on the
    /// sign-in page instead of briefly rendering the removed employee's dashboard.
    /// </summary>
    public static bool PendingRemoveAccount { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (ServiceProvider == null)
            {
                throw new InvalidOperationException("ServiceProvider must be set before app starts");
            }

            // ─── Roaming Start/Stop only — never MainWindow / shell ───
            // Close → hide to tray (keep process + controls mutex). Linux
            // TrackingControlsSupervisor relaunches --tracking-controls only when
            // the process is killed (mutex gone) — not when hidden to tray.
            if (TrackingControlsMode)
            {
                var ctrlVm = new TrackingControlViewModel();
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
                var ctrlWindow = new TrackingControlWindow { DataContext = ctrlVm };
                desktop.MainWindow = ctrlWindow;

                var allowControlsExit = false;
                ctrlWindow.Closing += (_, e) =>
                {
                    if (allowControlsExit) return;
                    e.Cancel = true;
                    // Close (X) → tray; keep timers running (do not StopTimers).
                    ctrlWindow.HideToTray();
                };

                // Mode flipped Fixed → exit the agent for real (watchdog will not relaunch).
                ctrlVm.RequestExitFixed += () => Dispatcher.UIThread.Post(() =>
                {
                    allowControlsExit = true;
                    ctrlVm.StopTimers();
                    try { ctrlWindow.Close(); } catch { /* ignore */ }
                    Environment.Exit(0);
                });

                var ctrlTray = new Avalonia.Controls.TrayIcon
                {
                    Icon = new Avalonia.Controls.WindowIcon(
                        Avalonia.Platform.AssetLoader.Open(new Uri("avares://client/Assets/avalonia-logo.ico"))),
                    // Distinct from the main shell tray tooltip ("Alpha AI Tracker").
                    ToolTipText = "Tracking Start / Stop",
                    IsVisible = true,
                };
                void ShowControls()
                {
                    try
                    {
                        if (ctrlWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                            ctrlWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                        ctrlWindow.ShowInTaskbar = false;
                        ctrlWindow.ApplyResponsiveLayout();
                        ctrlWindow.Show();
                        ctrlWindow.Activate();
                        ctrlWindow.Topmost = false;
                        ctrlWindow.Topmost = true; // force Z-order on Windows
                        // Re-arm timers if somehow stopped (tray hide must never freeze the clock).
                        ctrlVm.Start();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ShowControls: {ex}");
                    }
                }

                // Pipe from GUI "Tracker control" / second --tracking-controls launch.
                TrackingControlsActivation.OnShowRequested = () =>
                    Dispatcher.UIThread.Post(ShowControls);

                var showCtrl = new Avalonia.Controls.NativeMenuItem("Show Start / Stop");
                showCtrl.Click += (_, _) => Dispatcher.UIThread.Post(ShowControls);
                ctrlTray.Menu = new Avalonia.Controls.NativeMenu { showCtrl };
                // Left-click (Windows) — post to UI thread; Menu alone is easy to miss.
                ctrlTray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowControls);
                Avalonia.Controls.TrayIcon.SetIcons(this, new Avalonia.Controls.TrayIcons { ctrlTray });

                try
                {
                    ctrlWindow.ApplyResponsiveLayout();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex);
                }

                desktop.MainWindow = ctrlWindow;
                ctrlWindow.Show();
                ctrlWindow.Activate();
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var viewModel = ServiceProvider.GetRequiredService<MainViewModel>();

            // Initialize async (check existing login state from SQLite)
            Task.Run(async () =>
            {
                await viewModel.InitializeAsync(CancellationToken.None);
            }).GetAwaiter().GetResult();

            // ─── Pending account removal (client --remove-account) ───
            // Wipe this machine's local account and forget the employee (see
            // MainViewModel.RemoveAccountAsync). Called from here — before the window
            // exists, so a removal request that STARTED this GUI (headless tracker →
            // lazy GUI) opens straight on the sign-in page with no flash of the removed
            // employee's dashboard — and from the single-instance handler below, for a
            // GUI that is already live. InitializeAsync has restored the session by this
            // point; the removal undoes it.
            async Task RemoveLocalAccountAsync()
            {
                try
                {
                    await viewModel.RemoveAccountAsync(CancellationToken.None);
                    RemovalProgress.Write("done", "remove-account successfully");
                }
                catch (Exception ex)
                {
                    RemovalProgress.Write("failed", ex.Message);
                    Console.Error.WriteLine($"[client] Account removal failed: {ex.Message}");
                }
            }

            if (PendingRemoveAccount)
            {
                PendingRemoveAccount = false;
                Task.Run(RemoveLocalAccountAsync).GetAwaiter().GetResult();
            }

            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

            // Intercept close to hide instead (only block normal window-close, not explicit shutdown).
            // Separate --gui agent: closing the shell exits THIS process only — the
            // --background tracker keeps running (Chrome-like: UI ≠ daemon).
            mainWindow.Closing += (s, e) =>
            {
                if (GuiAgentMode)
                {
                    AllowShutdown = true;
                    return;
                }

                if (!AllowShutdown)
                {
                    e.Cancel = true;
                    // Time and Attendance (Phase 1, finalplan section 2.6 / BUG-3 fix):
                    // emit a ui_hidden event BEFORE the window goes to the tray. The
                    // dashboard can now distinguish "in tray" from "actively using
                    // the app" without waiting for a heartbeat gap. The IEventRecorder
                    // is DI-registered as a singleton, so we resolve it from the
                    // ServiceProvider that Program.cs set in App.ServiceProvider.
                    try
                    {
                        var recorder = ServiceProvider?.GetService<client.Core.Abstractions.IEventRecorder>();
                        if (recorder != null)
                        {
                            _ = recorder.RecordAsync(client.Core.Models.SessionEventTypes.UiHidden);
                        }
                    }
                    catch
                    {
                        // Best-effort: telemetry must never break a window close.
                    }
                    mainWindow.Hide();
                }
            };

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            var trayIcon = new Avalonia.Controls.TrayIcon
            {
                Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://client/Assets/avalonia-logo.ico"))),
                ToolTipText = AppInfo.DisplayName
            };

            var showItem = new Avalonia.Controls.NativeMenuItem($"Show {AppInfo.DisplayName}");
            showItem.Click += (s, e) =>
            {
                mainWindow.Show();
                mainWindow.Activate();
            };

            var hideItem = new Avalonia.Controls.NativeMenuItem("Hide");
            hideItem.Click += (s, e) =>
            {
                mainWindow.Hide();
            };

            var menu = new Avalonia.Controls.NativeMenu();
            menu.Items.Add(showItem);
            menu.Items.Add(hideItem);

            trayIcon.Menu = menu;
            trayIcon.IsVisible = true;
            
            var trayIcons = new Avalonia.Controls.TrayIcons { trayIcon };
            Avalonia.Controls.TrayIcon.SetIcons(this, trayIcons);

            // ─── Single-instance activation ───
            // This process is the primary (mutex-owning) instance — it may have
            // been launched hidden (--background / --minimized by auto-start or
            // systemd) and is the one that keeps tracking alive. When a second
            // user launch (e.g. the user clicks the desktop entry) sends a SHOW
            // signal via the named pipe, bring this window to the front. This is
            // what makes "open the GUI any number of times" work without ever
            // stopping the background tracker.
            //
            // Brings the window to the front. Shared by the SHOW signal and the
            // account-removal signal below.
            void ShowWindow()
            {
                try
                {
                    if (mainWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
                        mainWindow.WindowState = Avalonia.Controls.WindowState.Normal;
                    mainWindow.Show();
                    mainWindow.Activate();
                    mainWindow.Topmost = true;
                    mainWindow.Topmost = false; // bring to front without pinning
                }
                catch
                {
                    // Window may be in an odd state during shutdown — ignore.
                }
            }

            SingleInstanceService.OnShowRequested = () => Dispatcher.UIThread.Post(ShowWindow);

            // ─── Account removal (client --remove-account) ───
            // This process owns both the SQLite connection and the window, so it
            // performs the removal in place: wipe every local row, stop collecting, and
            // land the UI on the sign-in page. The requesting CLI process then exits —
            // it must NOT start a second tracker. Removing the account here (rather than
            // in the CLI, after stopping the tracker) is also what keeps the Linux
            // systemd unit's Restart=always from racing the mutex with a restarted,
            // still-logged-in tracker.
            // Background thread — same as protected-uninstall. Running wipe/sync on the
            // UI thread used to freeze the shell for the whole drain and delay phase
            // writes the CLI polls in RemovalProgress.
            SingleInstanceService.OnRemoveAccountRequested = () =>
            {
                _ = Task.Run(async () =>
                {
                    await RemoveLocalAccountAsync();
                    Dispatcher.UIThread.Post(ShowWindow);
                });
            };

            // stop→sync→status→teardown on a worker thread; CLI polls RemovalProgress.
            SingleInstanceService.OnProtectedUninstallRequested = code =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var svc = ServiceProvider?.GetService<ProtectedUninstallService>();
                        if (svc is null)
                        {
                            RemovalProgress.Write("failed", "ProtectedUninstallService not registered");
                            Console.Error.WriteLine("[client] ProtectedUninstallService not registered");
                            return;
                        }
                        var already = string.IsNullOrEmpty(code) ||
                                      ProtectedUninstallService.HasApprovedMarker();
                        var ok = await svc.RunPipelineAsync(
                            code ?? "", "both", CancellationToken.None,
                            codeAlreadyVerified: already);
                        Console.WriteLine(ok
                            ? "protected-uninstall successfully"
                            : "protected-uninstall failed — check the removal code.");
                    }
                    catch (Exception ex)
                    {
                        RemovalProgress.Write("failed", ex.Message);
                        Console.Error.WriteLine($"[client] Protected uninstall failed: {ex.Message}");
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        PendingRemoveAccount = true;
                        ShowWindow();
                    });
                });
            };

            // A removal request that arrived between the boot-time check above and the
            // handler installation just now would otherwise be dropped (the flag had not
            // been consumed yet and no handler existed) — re-check and run it.
            if (PendingRemoveAccount)
            {
                PendingRemoveAccount = false;
                Dispatcher.UIThread.Post(async () =>
                {
                    await RemoveLocalAccountAsync();
                    ShowWindow();
                });
            }

            // Show the window only for a real user launch. Auto-start / systemd boot
            // instances (--background / --minimized) start hidden (or fully headless),
            // and the lazy-GUI path in Program.cs clears LaunchedHidden before creating
            // the window so a manual launch always brings the GUI up.
            if (!LaunchedHidden)
            {
                mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}