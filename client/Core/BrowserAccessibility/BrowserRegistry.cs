using client.Core.Abstractions;
using client.Core.Models;
using Microsoft.Extensions.Logging;

namespace client.Core.BrowserAccessibility;

/// <summary>
/// Dynamic browser registry sourced from <see cref="IInstalledAppDetector"/>.
/// Caches the set of browsers for 5 minutes and answers IsBrowser / GetDisplayName
/// queries without any hardcoded product-name list.
/// </summary>
public sealed class BrowserRegistry : IBrowserRegistry
{
    private readonly IInstalledAppDetector _appDetector;
    private readonly ILogger<BrowserRegistry> _logger;
    private readonly object _gate = new();
    private volatile Dictionary<string, InstalledApplication>? _browserApps;
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public BrowserRegistry(IInstalledAppDetector appDetector, ILogger<BrowserRegistry> logger)
    {
        _appDetector = appDetector;
        _logger = logger;
    }

    private Dictionary<string, InstalledApplication> GetBrowserApps()
    {
        var apps = _browserApps;
        if (apps is not null && DateTime.UtcNow - _lastRefresh < RefreshInterval)
            return apps;

        lock (_gate)
        {
            if (_browserApps is not null && DateTime.UtcNow - _lastRefresh < RefreshInterval)
                return _browserApps;

            try
            {
                var allApps = _appDetector.GetAllInstalledApplications();
                var browsers = new Dictionary<string, InstalledApplication>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in allApps)
                {
                    if (!app.IsBrowser) continue;
                    if (!string.IsNullOrEmpty(app.BinaryName))
                        browsers[app.BinaryName] = app;
                    if (!string.IsNullOrEmpty(app.AppName) &&
                        !string.Equals(app.AppName, app.BinaryName, StringComparison.OrdinalIgnoreCase))
                        browsers[app.AppName] = app;
                }

                _browserApps = browsers;
                _lastRefresh = DateTime.UtcNow;
                _logger.LogDebug("Browser registry refreshed: {Count} browsers", browsers.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to refresh browser registry");
            }

            return _browserApps!;
        }
    }

    /// <summary>
    /// Exact-match first, then fuzzy contains-match (≥4 chars both sides).
    /// Mirrors the fuzzy rule already used by InstalledAppDetector.ResolveDisplayName.
    /// </summary>
    public bool IsBrowser(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var apps = GetBrowserApps();
        if (apps.ContainsKey(processName)) return true;

        if (processName.Length >= 4)
        {
            foreach (var kvp in apps)
            {
                if (kvp.Key.Length < 4) continue;
                if (kvp.Key.Contains(processName, StringComparison.OrdinalIgnoreCase) ||
                    processName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    public bool IsBrowser(string processName, string? commandLine)
    {
        if (IsBrowser(processName)) return true;
        if (!string.IsNullOrWhiteSpace(commandLine))
            return IsBrowser(commandLine);
        return false;
    }

    public string? GetDisplayName(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;
        var apps = GetBrowserApps();
        if (apps.TryGetValue(processName, out var app))
            return app.AppName;
        return null;
    }

    public IReadOnlyList<string> GetAllBrowserProcessNames()
    {
        var apps = GetBrowserApps();
        return apps.Keys.ToList();
    }

    public IReadOnlyList<string> GetAllBrowserDisplayNames()
    {
        var apps = GetBrowserApps();
        return apps.Values.Select(a => a.AppName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
