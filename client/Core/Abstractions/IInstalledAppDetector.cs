using client.Core.Models;

namespace client.Core.Abstractions;

/// <summary>
/// Detects whether a process is a legitimately installed application
/// vs a random script/binary. Each platform implements its own heuristics.
/// </summary>
public interface IInstalledAppDetector
{
    /// <summary>
    /// Returns true if the given process is an installed application
    /// (e.g. has a desktop file, is in Program Files, is an AppX package, etc.)
    /// </summary>
    bool IsInstalledApplication(string processName, string? executablePath);

    /// <summary>
    /// Returns a list of known installed application names for fast pre-filtering.
    /// </summary>
    IReadOnlySet<string> KnownInstalledAppNames { get; }

    /// <summary>
    /// Returns full InstalledApplication objects with metadata (version, publisher, path, etc.)
    /// from the actual installed application database/registry, NOT from running processes.
    /// </summary>
    IReadOnlyList<InstalledApplication> GetAllInstalledApplications();

    /// <summary>
    /// Returns a description of what permissions are currently missing
    /// to properly detect installed applications.
    /// </summary>
    IReadOnlyList<string> MissingPermissions { get; }

    /// <summary>
    /// Get human-readable instructions for granting missing permissions.
    /// </summary>
    IReadOnlyList<string> PermissionGrantInstructions { get; }

    /// <summary>
    /// Force re-detection of installed apps and missing permissions.
    /// Call this after the user grants permissions via PolKit/UAC.
    /// </summary>
    void ForceRecheck();

    /// <summary>
    /// Resolve the display name for a process by its executable binary name.
    /// e.g., "code" → "Visual Studio Code", "firefox" → "Firefox"
    /// Returns null if the binary name is not known.
    /// </summary>
    string? ResolveDisplayName(string processName);

    /// <summary>
    /// Returns true only if the process corresponds to a GUI application
    /// (has a .desktop file on Linux, .app bundle on macOS, Start Menu /
    /// Program Files entry on Windows). Returns false for CLI-only tools,
    /// shell interpreters, build tools, runtimes, and daemons.
    /// </summary>
    bool IsGuiApplication(string processName, string? executablePath);
}
