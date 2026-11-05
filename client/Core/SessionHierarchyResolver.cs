using Microsoft.Extensions.Logging;

namespace client.Core;

public sealed class OpenSessionRecord
{
    public int ProcessId { get; init; }
    public string AppSessionId { get; init; } = string.Empty;
    public string RootItemId { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ItemType { get; init; } = string.Empty;
    /// <summary>Root item title — used by the accessibility browser tracker to hydrate its
    /// in-memory window registry across a fast relaunch (title-match re-keying).</summary>
    public string RootItemTitle { get; init; } = string.Empty;
    /// <summary>Root item URL (browser_tab roots carry it) — same hydration purpose.</summary>
    public string RootItemUrl { get; init; } = string.Empty;
    /// <summary>FK → installed_applications.id, needed to rebuild scope-aware session keys on boot hydration.</summary>
    public string? InstalledAppId { get; init; }
}

public sealed class ParentLink
{
    public string ParentSessionId { get; init; } = string.Empty;
    public string ParentItemId { get; init; } = string.Empty;
    public int ParentProcessId { get; init; }
}

/// <summary>
/// Resolves parent-child relationships using the OS process tree and open session registry.
/// Supports: node → terminal → IDE, bash → gnome-terminal, standalone terminals (no parent).
/// </summary>
public sealed class SessionHierarchyResolver
{
    private readonly Dictionary<int, int> _processTree;
    private readonly Dictionary<int, OpenSessionRecord> _openByPid;
    private readonly ILogger? _logger;

    public SessionHierarchyResolver(
        Dictionary<int, int> processTree,
        IEnumerable<OpenSessionRecord> existingOpen,
        ILogger? logger = null)
    {
        _processTree = processTree;
        _logger = logger;

        // More than one open session can legitimately share a process_id: the
        // accessibility browser tracker writes one app_sessions per browser window
        // (ProcessId = the browser's main pid) while the process collector writes its
        // own session for that same browser process. A plain ToDictionary would throw
        // "An item with the same key has already been added" on every collection cycle
        // (which killed the entire tracking loop) — dedupe by keeping the earliest
        // record per PID. These records are only consulted for parent-session lookups,
        // so either copy is equivalent.
        var duplicates = existingOpen
            .GroupBy(r => r.ProcessId)
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicates.Count > 0)
        {
            // Debug, not Warning: this is expected whenever both systems track the same
            // browser process, and it no longer indicates a problem — a warning here
            // would spam the log every 30s collection cycle.
            logger?.LogDebug(
                "SessionHierarchyResolver: {DuplicateCount} process_id(s) have multiple open sessions " +
                "(browser accessibility tracker + process collector overlap) — keeping the earliest per PID: {Pids}",
                duplicates.Count, string.Join(", ", duplicates.Select(g => g.Key)));
        }
        _openByPid = existingOpen
            .GroupBy(r => r.ProcessId)
            .ToDictionary(g => g.Key, g => g.First());
    }

    public void Register(OpenSessionRecord record) => _openByPid[record.ProcessId] = record;

    public int? GetParentProcessId(int pid) =>
        _processTree.TryGetValue(pid, out var ppid) && ppid > 0 ? ppid : null;

    /// <summary>
    /// Walk the PPID chain to find the nearest tracked ancestor session/item.
    /// Returns null for standalone processes (e.g. gnome-terminal with no IDE parent).
    /// Handles VSCode sub-processes (renderers, utilities) as intermediate steps.
    /// </summary>
    public ParentLink? ResolveParent(int pid, string processName)
    {
        _logger?.LogDebug("ResolveParent: walking PPID chain for pid={Pid} name={Name}", pid, processName);
        var visited = new HashSet<int>();
        var current = pid;

        while (current > 0 && visited.Add(current))
        {
            if (!_processTree.TryGetValue(current, out var ppid) || ppid <= 0)
            {
                _logger?.LogDebug("ResolveParent: pid={Pid} has no tracked parent, stopping", current);
                break;
            }

            if (visited.Contains(ppid))
                break;

            if (_openByPid.TryGetValue(ppid, out var parentRecord))
            {
                if (ShouldLinkTo(parentRecord, processName))
                {
                    _logger?.LogDebug("ResolveParent: pid={Pid} linked to parent pid={Ppid} session={Session}",
                        pid, ppid, parentRecord.AppSessionId);
                    return new ParentLink
                    {
                        ParentSessionId = parentRecord.AppSessionId,
                        ParentItemId = parentRecord.RootItemId,
                        ParentProcessId = ppid,
                    };
                }
                _logger?.LogDebug("ResolveParent: pid={Pid} found open session at pid={Ppid} but ShouldLinkTo rejected (parent={ParentName} child={ChildName})",
                    pid, ppid, parentRecord.ProcessName, processName);
            }

            // Walk through intermediate processes to find IDE / terminal emulator
            string? parentName = null;
            try
            {
                parentName = System.Diagnostics.Process.GetProcessById(ppid).ProcessName;
            }
            catch
            {
                break;
            }

            // Continue walking up through ALL intermediate processes:
            // - Shells (bash, zsh), terminal emulators (gnome-terminal)
            // - Build tools (make, go, npm), runtimes (node, dotnet)
            // - IDE sub-processes with the same base name as the IDE (e.g., "code" renderers/utilities)
            // This is critical for VSCode integrated terminal: bash → code --utility → code main
            if (AppProcessClassifier.IsShellInterpreter(parentName) ||
                AppProcessClassifier.IsTerminalEmulator(parentName) ||
                AppProcessClassifier.IsBuildTool(parentName) ||
                AppProcessClassifier.IsRuntimePackage(parentName) ||
                AppProcessClassifier.IsIdeProcess(parentName))
            {
                current = ppid;
                continue;
            }

            break;
        }

        return null;
    }

    private static bool ShouldLinkTo(OpenSessionRecord parent, string childProcessName)
    {
        if (AppProcessClassifier.IsIdeProcess(parent.ProcessName))
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsTerminalEmulator(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName) ||
                   parent.ItemType == "terminal";

        if (AppProcessClassifier.IsTerminalEmulator(parent.ProcessName))
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName);

        if (parent.ItemType == "terminal")
            return AppProcessClassifier.IsShellInterpreter(childProcessName) ||
                   AppProcessClassifier.IsRuntimePackage(childProcessName) ||
                   AppProcessClassifier.IsBuildTool(childProcessName);

        if (parent.ItemType == "tab" && AppProcessClassifier.IsShellInterpreter(childProcessName))
            return true;

        return false;
    }
}
