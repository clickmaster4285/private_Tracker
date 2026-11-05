namespace client.Services.Watchers;

/// <summary>
/// Provides the currently-open Explorer / file-manager windows so raw file-system events
/// (which carry no window identity) can be attributed to the window that is browsing the
/// containing folder — joining the file op to the same journey as the navigation.
/// Windows-only; the coordinator treats a null/empty result as "unattributed".
/// </summary>
public interface IExplorerWindowProvider
{
    /// <summary>
    /// Best-matching Explorer window for a filesystem path: the window whose current folder
    /// is the longest ancestor (or exact match) of <paramref name="path"/>.
    /// </summary>
    /// <returns>False when no window is browsing an ancestor of the path.</returns>
    bool TryGetWindowForPath(string path, out int windowId, out string windowTitle, out string processName);
}
