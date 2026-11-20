using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace AlphaAITracker.Service;

/// <summary>
/// "Check the shift at the click": wakes <see cref="LogonHoursWorker"/> (fresh shift download
/// for every account) the moment
/// <list type="bullet">
/// <item>Windows refuses a sign-in because of logon hours (Security 4625, SubStatus
/// 0xC000006F) — if the admin just extended the shift, the hours are opened and the
/// employee's next click works;</item>
/// <item>a user session starts — if the admin just shortened the shift, the hours are closed
/// and the tracker's end-of-shift check signs the session out.</item>
/// </list>
/// Polls every 3 s (Security log from the last seen record only; WTS session list) and wakes
/// the worker at most once per 10 s.
/// </summary>
internal sealed class LogonEventWatcher : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinWakeGap = TimeSpan.FromSeconds(10);
    private const string LogonHoursSubStatus = "0xc000006f";

    private readonly DlpConfig _cfg;
    private readonly ILogger<LogonEventWatcher> _log;
    private long _lastRecordId;
    private HashSet<int> _knownSessions = [];
    private DateTimeOffset _lastWake = DateTimeOffset.MinValue;

    public LogonEventWatcher(DlpConfig cfg, ILogger<LogonEventWatcher> log)
    {
        _cfg = cfg;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return;
        _lastRecordId = LatestSecurityRecordId();
        _knownSessions = SessionGuardEnforcer.ActiveUserSessionIds();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_cfg.LogonHours)
                {
                    string? why = null;
                    var refused = RefusedForLogonHoursSinceLast();
                    if (refused != null) why = $"sign-in refused by logon hours ({refused})";

                    var sessions = SessionGuardEnforcer.ActiveUserSessionIds();
                    if (sessions.Except(_knownSessions).Any()) why ??= "new sign-in";
                    _knownSessions = sessions;

                    if (why != null && DateTimeOffset.UtcNow - _lastWake >= MinWakeGap)
                    {
                        _lastWake = DateTimeOffset.UtcNow;
                        _log.LogInformation("Logon hours: re-checking shifts on the server — {Why}", why);
                        DlpSignals.RequestLogonHours();
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Logon event watch failed");
            }

            try { await Task.Delay(Poll, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static long LatestSecurityRecordId()
    {
        try
        {
            var q = new EventLogQuery("Security", PathType.LogName, "*[System[EventID=4625]]") { ReverseDirection = true };
            using var reader = new EventLogReader(q);
            using var rec = reader.ReadEvent();
            return rec?.RecordId ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Account name of the newest logon-hours refusal since the last poll, else null.</summary>
    private string? RefusedForLogonHoursSinceLast()
    {
        string? user = null;
        var q = new EventLogQuery("Security", PathType.LogName,
            $"*[System[EventID=4625 and EventRecordID > {_lastRecordId}]]");
        using var reader = new EventLogReader(q);
        for (var rec = reader.ReadEvent(); rec != null; rec = reader.ReadEvent())
        {
            using (rec)
            {
                if (rec.RecordId is { } id && id > _lastRecordId) _lastRecordId = id;
                try
                {
                    var data = XDocument.Parse(rec.ToXml()).Descendants()
                        .Where(e => e.Name.LocalName == "Data")
                        .ToDictionary(e => e.Attribute("Name")?.Value ?? "", e => e.Value, StringComparer.OrdinalIgnoreCase);
                    if (data.TryGetValue("SubStatus", out var sub)
                        && sub.Equals(LogonHoursSubStatus, StringComparison.OrdinalIgnoreCase))
                        user = data.TryGetValue("TargetUserName", out var u) ? u : "?";
                }
                catch { /* unreadable record */ }
            }
        }
        return user;
    }
}
