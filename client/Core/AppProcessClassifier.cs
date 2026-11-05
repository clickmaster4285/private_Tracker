namespace client.Core;

/// <summary>
/// Classifies running processes into app categories for item_type assignment and hierarchy rules.
/// </summary>
public static class AppProcessClassifier
{
    // ⚠️ BROWSER DETECTION IS NOW DYNAMIC — see InstalledAppDetector + is_browser column in DB.
    // No hardcoded BrowserProcesses HashSet.
    // Use the `isBrowser` flag passed from ResolveAppInfo() at runtime.

    private static readonly HashSet<string> FileManagerFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        "nautilus", "dolphin", "thunar", "nemo", "pcmanfm", "caja", "nautilus-desktop",
        "explorer", "finder", "files",
    };

    private static readonly HashSet<string> IdeFallbacks = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "cursor", "codium", "idea", "pycharm", "webstorm", "goland", "rider",
        "devenv", "sublime_text", "atom", "zed",
    };

    private static readonly HashSet<string> ShellInterpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "zsh", "sh", "dash", "fish", "cmd", "powershell", "pwsh", "wsl",
    };

    private static readonly HashSet<string> TerminalEmulators = new(StringComparer.OrdinalIgnoreCase)
    {
        "gnome-terminal", "gnome-terminal.real", "gnome-terminal-server", "konsole", "alacritty", "kitty",
        "iterm2", "terminal", "xterm", "rxvt", "urxvt", "st", "tmux", "screen", "foot",
        "xfce4-terminal", "lxterminal", "tilix", "hyper", "wezterm",
    };

    private static readonly HashSet<string> RuntimePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "nodejs", "python", "python3", "java", "ruby", "perl", "php", "dotnet",
    };

    /// <summary>
    /// Build tools / package managers / compilers that run inside terminals.
    /// These have no window but should still be tracked as child process entries.
    /// </summary>
    private static readonly HashSet<string> BuildToolProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "make", "go", "cargo", "mvn", "gradle", "gradlew", "mvnw",
        "npm", "npx", "pnpm", "yarn", "bun",
        "pip", "pip3", "uv",
        "tsc", "webpack", "vite", "esbuild", "rollup",
        "cmake", "ninja", "bazel", "ant",
        "dotnet", "rustc", "gcc", "g++", "clang", "clang++",
    };

    // ── Dynamic category detection (preferred) ──────────────────────────────────

    /// <summary>
    /// Resolve the primary app category using the new metadata-first approach:
    ///   1. If <paramref name="isBrowser"/> is true → browser
    ///   2. If <paramref name="categories"/> can be resolved → use that category
    ///   3. Fallback to process-name sets
    /// Returns null when the category cannot be determined.
    /// </summary>
    public static string? ResolveCategory(string processName, bool isBrowser, string? categories)
    {
        if (isBrowser) return SoftwareCategoryResolver.Browser;
        if (!string.IsNullOrWhiteSpace(categories))
        {
            var cat = SoftwareCategoryResolver.ResolveFromCategories(categories, isBrowser: false);
            if (cat != SoftwareCategoryResolver.Unknown) return cat;
        }
        // Fallback to process-name heuristics.
        return ResolveCategoryFallback(processName);
    }

    /// <summary>Process-name fallback when OS metadata is not available.</summary>
    private static string? ResolveCategoryFallback(string processName)
    {
        if (IsFileManagerProcess(processName)) return SoftwareCategoryResolver.FileManager;
        if (IsIdeProcess(processName)) return SoftwareCategoryResolver.Ide;
        if (IsShellProcess(processName)) return SoftwareCategoryResolver.Application;
        if (IsRuntimePackage(processName)) return SoftwareCategoryResolver.Runtime;
        return null;
    }

    // ── Legacy bool checks (backward compat) ────────────────────────────────────

    public static bool IsFileManagerProcess(string processName) =>
        FileManagerFallbacks.Contains(processName);

    public static bool IsIdeProcess(string processName) =>
        IdeFallbacks.Contains(processName);

    public static bool IsShellInterpreter(string processName) =>
        ShellInterpreters.Contains(processName);

    public static bool IsTerminalEmulator(string processName) =>
        TerminalEmulators.Contains(processName);

    public static bool IsShellProcess(string processName) =>
        IsShellInterpreter(processName) || IsTerminalEmulator(processName);

    public static bool IsRuntimePackage(string processName) =>
        RuntimePackages.Contains(processName);

    public static bool IsBuildTool(string processName) =>
        BuildToolProcesses.Contains(processName);


    /// <summary>Extract the base binary name from a process name that may include arguments.</summary>
    public static string ExtractBaseProcessName(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            return rawName;
        var idx = rawName.IndexOf(' ');
        return idx > 0 ? rawName[..idx].Trim() : rawName;
    }

    /// <summary>Check if a process name (possibly with args) matches a known build tool.</summary>
    public static bool IsBuildToolExtended(string rawName)
    {
        var baseName = ExtractBaseProcessName(rawName);
        return IsBuildTool(baseName);
    }

    /// <summary>Check if a process name (possibly with args) matches a known runtime package.</summary>
    public static bool IsRuntimePackageExtended(string rawName)
    {
        var baseName = ExtractBaseProcessName(rawName);
        return IsRuntimePackage(baseName);
    }

    /// <summary>Check if a process name (possibly with args) matches a known shell process.</summary>
    public static bool IsShellProcessExtended(string rawName)
    {
        var baseName = ExtractBaseProcessName(rawName);
        return IsShellProcess(baseName);
    }

    // ── Root item type resolution ───────────────────────────────────────────────

    /// <summary>
    /// Resolve item_type using metadata when available, falling back to the old bool + name-check model.
    /// </summary>
    public static string ResolveRootItemType(
        string processName, string? appId, string? pkgId, string? windowTitle,
        bool isBrowser, string? categories, string? desktopId)
    {
        // Priority 1: explicit browser flag (from .desktop / manifest / registry).
        if (isBrowser) return "browser_tab";

        // Priority 2: metadata lookups.
        if (!string.IsNullOrWhiteSpace(categories) || !string.IsNullOrWhiteSpace(desktopId))
        {
            var cat = ResolveCategory(processName, isBrowser: false, categories);
            switch (cat)
            {
                case SoftwareCategoryResolver.FileManager: return "folder";
                case SoftwareCategoryResolver.Ide:
                case SoftwareCategoryResolver.Application:
                    return "tab";
                case SoftwareCategoryResolver.Browser:
                    return "browser_tab";
                case SoftwareCategoryResolver.Runtime:
                    return pkgId != null ? "process" : "tab";
            }
        }

        // Priority 3: legacy process-name fallbacks.
        if (IsFileManagerProcess(processName))
            return "folder";

        if (IsShellProcess(processName))
            return "terminal";

        if (appId != null)
            return "tab";

        if (pkgId != null || IsRuntimePackage(processName) || IsBuildTool(processName))
            return "process";

        if (!string.IsNullOrWhiteSpace(windowTitle))
            return "tab";

        return "process";
    }

    /// <summary>Backward-compatible overload (no metadata).</summary>
    public static string ResolveRootItemType(
        string processName, string? appId, string? pkgId, string? windowTitle, bool isBrowser = false) =>
        ResolveRootItemType(processName, appId, pkgId, windowTitle, isBrowser, categories: null, desktopId: null);

    /// <summary>Whether a child process should nest under a parent session item instead of being standalone.</summary>
    public static bool ShouldNestUnderParent(string processName, string? pkgId) =>
        IsShellInterpreter(processName) ||
        pkgId != null ||
        IsRuntimePackage(processName) ||
        IsBuildTool(processName);

    // ── Chromium/Electron Headless Subprocess Detection (Fix 2, 2026-07-30) ──

    /// <summary>
    /// Flags that identify Chromium/Electron helper subprocesses.
    /// Any process whose cmdline contains one of these flags is a headless
    /// helper (renderer, GPU, utility, etc.) and should NOT be tracked as
    /// its own app_session — all activity belongs to the parent browser/Electron app.
    /// Shared across all three platform ProcessCollectors.
    /// </summary>
    private static readonly string[] ChromiumSubprocessFlags =
    {
        "--type=renderer",
        "--type=gpu-process",
        "--type=utility",
        "--type=zygote",
        "--type=broker",
        "--type=crashpad-handler",
        "--contentproc",
        "-contentproc",
    };

    /// <summary>
    /// Check if a command line indicates a headless Chromium/Electron subprocess.
    /// Reads the full cmdline and checks for --type= flags (Chrome, Edge, Brave,
    /// Vivaldi, Opera, VSCode, Slack, Discord, etc.) or --contentproc (Firefox).
    /// </summary>
    public static bool IsHeadlessSubprocess(string? cmdline)
    {
        if (string.IsNullOrEmpty(cmdline)) return false;
        foreach (var flag in ChromiumSubprocessFlags)
        {
            if (cmdline.Contains(flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
