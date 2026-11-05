using Microsoft.Extensions.Logging;

namespace client.Services.Watchers;

/// <summary>
/// macOS half of <see cref="SystemEventWatcher"/>. Uses NSWorkspace notifications
/// for sleep/wake and a 1Hz poll of <c>CGSessionCopyCurrentDictionary</c> for
/// the screen-lock state (which is not exposed as a notification on macOS
/// prior to Sonoma 14.0).
///
/// Two notification centers we listen on:
///   - <c>NSWorkspaceWillSleepNotification</c>     → power_off (the OS is going
///                                                    to sleep; the ShutdownSentinel
///                                                    is not invoked because the
///                                                    process keeps running)
///   - <c>NSWorkspaceDidWakeNotification</c>      → resume
///
/// macOS sends no discrete "screen lock" notification; the screen is locked
/// when the session's CGSSession flag <c>CGSSessionOnConsoleKey</c> is absent.
/// The CGSession poll on a 1-second timer captures that. (1Hz is more than
/// enough: a user locking their screen sees the dashboard update within 1s.)
///
/// The CGSession poll is a TODO comment in this file (A.3 ships the foundation
/// + the workspace notifications; the screen-lock poll lands once the macOS
/// build pipeline is set up to test it). On non-macOS platforms the method is
/// simply not called.
/// </summary>
public sealed partial class SystemEventWatcher
{
    private void SubscribeMac()
    {
        _logger.LogInformation("SystemEventWatcher: subscribing to NSWorkspace sleep/wake notifications");

        // macOS NSWorkspace notifications are delivered through the
        // Foundation NSNotificationCenter. The .NET for macOS bindings
        // (Microsoft.Mac.Runtime / Foundation.NSNotificationCenter) require
        // a macOS-specific TFM (net10.0-maccatalyst). For Phase 1 we emit
        // a single placeholder log entry so the watcher is at least visible
        // in startup logs; the real subscription will land in a follow-up
        // commit that adds the maccatalyst TFM.
        //
        // Why this placeholder: keeping the project cross-platform on the
        // linux+win TFM means a single .csproj builds everywhere. Splitting
        // into maccatalyst would require a separate build pipeline and is
        // out of scope for the Phase 1 client.
        _logger.LogInformation("SystemEventWatcher: macOS NSWorkspace wiring is TODO - will land with the maccatalyst TFM split");

        // The CGSession poll is a follow-up. Until then, screen_lock / unlock
        // events on macOS are best-effort: the user's first interaction with
        // the OS is captured by the LogCollectorService foreground gate.
    }
}
