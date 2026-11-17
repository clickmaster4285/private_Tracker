using System.Text.Json;

namespace AlphaAITracker.Service;

/// <summary>
/// Last enforce result per layer. The tracker policy screen polls this over the pipe.
/// A layer is ready only when its code is <c>ok</c> or <c>off</c> (not requested).
/// Network lockdown and AppLocker never use <c>off</c> for a switch that is supposed to be on.
/// </summary>
internal sealed class EnforceStatus
{
    public static EnforceStatus Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, Layer> _layers = new(StringComparer.OrdinalIgnoreCase);
    private string _phase = "waiting";
    private bool _hasCreds;
    private bool _rebootArmed;
    private bool _rebootQueued;
    private string? _rebootError;
    private string _solution = "";
    private string _failureCode = "";
    private AppBlockNotice? _lastBlock;
    private bool _watchArmed;
    private IReadOnlyList<AllowAppEntry> _watchApps = Array.Empty<AllowAppEntry>();

    private sealed record Layer(string Code, string Detail);

    public sealed record AppBlockNotice(string Id, string Name, string Path, DateTimeOffset At);

    public bool IsEnforcing
    {
        get
        {
            lock (_gate)
                return _phase is "enforcing" or "reboot_queued";
        }
    }

    public void BeginApply(bool hasCreds)
    {
        lock (_gate)
        {
            _hasCreds = hasCreds;
            if (_rebootQueued)
                return;
            _phase = hasCreds ? "applying" : "waiting";
        }
    }

    public void SetLayer(string id, string code, string detail)
    {
        lock (_gate)
            _layers[id] = new Layer(code, detail ?? "");
    }

    public void SetWatch(bool armed, IReadOnlyList<AllowAppEntry> apps)
    {
        lock (_gate)
        {
            _watchArmed = armed;
            _watchApps = apps ?? Array.Empty<AllowAppEntry>();
        }
    }

    public (bool Armed, IReadOnlyList<AllowAppEntry> Apps) WatchSnapshot()
    {
        lock (_gate)
            return (_watchArmed, _watchApps);
    }

    public void NoteAppBlock(string name, string path)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lastBlock is { } prev
                && string.Equals(prev.Path, path, StringComparison.OrdinalIgnoreCase)
                && now - prev.At < TimeSpan.FromSeconds(5))
                return;
            _lastBlock = new AppBlockNotice(Guid.NewGuid().ToString("N"), name, path, now);
        }
    }

    /// <summary>Called when credentials appear mid-run and a reboot should follow a good apply.</summary>
    public void SetRebootArmed(bool armed)
    {
        lock (_gate)
        {
            if (_rebootQueued)
            {
                _rebootArmed = true;
                return;
            }
            _rebootArmed = armed;
        }
    }

    public void MarkRebootQueued(string detail)
    {
        lock (_gate)
        {
            _rebootQueued = true;
            _rebootArmed = true;
            _rebootError = null;
            _phase = "reboot_queued";
            _solution = "";
            _failureCode = "";
            _layers["reboot"] = new Layer("ok", detail ?? "");
        }
    }

    public void MarkRebootFailed(string detail)
    {
        lock (_gate)
        {
            _rebootError = string.IsNullOrWhiteSpace(detail) ? "reboot_failed" : detail;
            _solution =
                "Windows did not accept the restart (" + _rebootError +
                "). Choose Retry. The block rules stay on.";
            _failureCode = _rebootError;
        }
    }

    public void NoteTickFailed(string detail)
    {
        lock (_gate)
        {
            if (_rebootQueued)
                return;
            _phase = "failed";
            _failureCode = "tick_failed";
            _solution = SolutionFor("enforce", "failed", detail);
        }
    }

    public void FinishApply()
    {
        lock (_gate)
        {
            if (_rebootQueued)
            {
                _phase = "reboot_queued";
                _solution = "";
                _failureCode = "";
                return;
            }
            if (!_hasCreds)
            {
                _phase = "waiting";
                _solution = "";
                _failureCode = "";
                return;
            }

            var fail = FirstFailure();
            if (fail is not null)
            {
                _phase = "failed";
                _failureCode = fail.Value.Code;
                _solution = fail.Value.Solution;
                return;
            }

            _phase = "enforcing";
            _failureCode = "";
            if (string.IsNullOrEmpty(_rebootError))
                _solution = "";
        }
    }

    public string ToJson()
    {
        lock (_gate)
        {
            var layers = _layers
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => new { id = kv.Key, code = kv.Value.Code, detail = kv.Value.Detail })
                .ToArray();
            return JsonSerializer.Serialize(new
            {
                ok = true,
                phase = _phase,
                rebootArmed = _rebootArmed,
                rebootError = _rebootError,
                solution = _solution,
                failureCode = _failureCode,
                layers,
                lastAppBlock = _lastBlock is null
                    ? null
                    : new { id = _lastBlock.Id, name = _lastBlock.Name, path = _lastBlock.Path },
            });
        }
    }

    private (string Code, string Solution)? FirstFailure()
    {
        string[] order = ["network", "dns", "applocker", "usb", "web", "filetransfer", "delete", "ports"];
        foreach (var id in order)
        {
            if (!_layers.TryGetValue(id, out var layer))
                return ("missing_" + id, SolutionFor(id, "missing", "not_applied"));
            if (layer.Code is "ok" or "off")
                continue;
            if (id == "ports" && layer.Code == "pending")
                continue;
            return (layer.Detail, SolutionFor(id, layer.Code, layer.Detail));
        }
        return null;
    }

    internal static string SolutionFor(string layer, string code, string detail)
    {
        var d = detail ?? "";
        if (d.Contains("bootstrap_unresolved", StringComparison.OrdinalIgnoreCase))
            return "The tracker server name did not resolve. Check the server address and DNS, then choose Retry.";
        if (d.Contains("applocker_unavailable", StringComparison.OrdinalIgnoreCase))
            return "This Windows edition cannot enforce AppLocker. Pro, Enterprise, or Education is required.";
        if (d.Contains("NOT_ENFORCING", StringComparison.OrdinalIgnoreCase)
            || d.Contains("verify_failed", StringComparison.OrdinalIgnoreCase)
            || d.Contains("taskmgr_allowed", StringComparison.OrdinalIgnoreCase))
            return "Application Identity (AppIDSvc) is not enforcing. Choose Retry to start it again. If it still fails, reboot this PC once.";
        if (d.Contains("access_denied", StringComparison.OrdinalIgnoreCase))
            return ServiceIdentity.ServiceName + " is not running as LocalSystem. Start the service, then choose Retry.";
        if (d.Contains("disabled_local", StringComparison.OrdinalIgnoreCase) && layer == "network")
            return "Network lockdown is turned off in service.env (ALPHA_DLP_NETWORK_LOCKDOWN). Set it to true and restart AlphaAITracker.Service.";
        if (d.Contains("disabled_local", StringComparison.OrdinalIgnoreCase) && layer == "applocker")
            return "Application blocking is turned off in service.env (ALPHA_DLP_APP_BLOCK). Set it to true and restart AlphaAITracker.Service.";
        if (d.Contains("paused", StringComparison.OrdinalIgnoreCase))
            return "Blocking is paused. It stays off until the pause ends. Choose Retry after the pause ends.";
        if (d.Contains("lockdown_off", StringComparison.OrdinalIgnoreCase))
            return "Network lockdown is turned off in the published policy. An administrator must turn it on under Allowlists, then choose Retry.";
        if (d.Contains("lock_error", StringComparison.OrdinalIgnoreCase)
            || d.Contains("netsh", StringComparison.OrdinalIgnoreCase)
            || d.Contains("firewall", StringComparison.OrdinalIgnoreCase))
            return "Windows Firewall did not accept the block rules. Start the Windows Firewall service (MpsSvc), then choose Retry.";
        if (layer == "pipe" || d.Contains("dlp_disabled", StringComparison.OrdinalIgnoreCase))
            return "The security service is not running. Start " + ServiceIdentity.ServiceName + ", then choose Retry.";
        if (code == "missing")
            return "Security policy has not finished applying (" + layer + "). Choose Retry.";
        var tail = string.IsNullOrWhiteSpace(d) ? code : d;
        if (tail.Length > 180)
            tail = tail[..180];
        return "Security policy did not apply (" + layer + ": " + tail + "). Choose Retry. If it keeps failing, restart " + ServiceIdentity.ServiceName + ".";
    }
}
