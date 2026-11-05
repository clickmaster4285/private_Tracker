# Client UI Architecture

**Scope:** the Avalonia desktop GUI only — design tokens, style classes, the six pages, the router, and the binding patterns that hold them together. For services, collectors, storage and the sync pipeline see [ARCHITECTURE.md](./ARCHITECTURE.md). For the repo-wide file tree see [../FILE_HIERARCHY.md](../FILE_HIERARCHY.md).

*Last audited: 2026-10-30 — MainWindow rail + roaming Tracker control; separate `--tracking-controls` modal agent.*

---

## 1. The shape in one screen

```
App.axaml ─────────────► FluentTheme + ~55 style selectors (all Classes.* based)
   └─ merges Styles/AppTheme.xaml ─► design tokens: Colors, Brushes, Shadows,
                                     Radii, StreamGeometry icons, FontFamily

MainWindow.axaml — ROUTER (not a screen)
   │  Title="{Binding AppTitleWithVersion}"      ← from APP_IDENTIFIERS + VERSION
   │
   ├─ IsSplashVisible          → pages:SplashPage            (page 1)
   └─ !IsSplashVisible
        ├─ !IsLoggedIn              → pages:LoginPage            (page 2)
        ├─ RequiresPermissionAction → pages:PermissionSetupPage  (page 3)
        └─ IsProfile → ColumnDefinitions="246,*"
              ├─ col 0  Nav rail (dark navy) — brand tile, Overview / Specs /
              │         Applications, **Tracker control** (roaming-only),
              │         employee card, version
              └─ col 1  RowDefinitions="Auto,*"
                    ├─ Top bar: ActivePageTitle / ActivePageSubtitle / env badge /
                    │         update controls / refresh
                    └─ Page host — three UserControls stacked, one visible:
                         DashboardPage      (page 4)  DataContext = Dashboard
                         SystemSpecsPage    (page 5)  DataContext = SystemSpecs
                         InstalledAppsPage  (page 6)  DataContext = InstalledApps

Separate Avalonia agent (not MainWindow):
  `--tracking-controls` → TrackingControlWindow (Start/Stop + day timer; ─/× → tray)
  Reopen: tray "Tracking Start / Stop", second launch, or MainWindow **Tracker control**
  via TrackingControlsActivation named pipe.
```

Four top-level states are **mutually exclusive booleans**, not a navigation stack — there is no back button and no history, because the wizard is a one-way gate: splash → login → permissions → shell. Once `IsProfile` is true the user never returns to pages 1–3 — the Disconnect button/`LogoutCommand` was removed 2026-08-10, so the shell has no in-app logout (tracking runs until the process stops).

---

## 2. Design tokens — `Styles/AppTheme.xaml` (153 lines)

A single `ResourceDictionary` merged into `Application.Resources`. **Light theme** (`RequestedThemeVariant="Light"` on `App.axaml`). Nothing in it names the product — every brand string arrives at runtime from `Core/AppInfo`.

| Group | Keys | Notes |
| ----- | ---- | ----- |
| **Canvas / text** | `BgColor` `#F4F6FA`, `CanvasColor` `#F8F9FA`, `FgColor` `#0F172A`, `MutedFgColor` `#64748B`, `FaintFgColor` `#94A3B8` | three-level text ramp: full / muted / faint |
| **Surfaces** | `CardColor` `#FFFFFF`, `CardFgColor`, `MutedBrushColor` `#F1F5F9`, `InputColor`, `BorderColor` `#E2E8F0` | cards are white on a grey canvas — the depth cue is the canvas, not a shadow |
| **Brand** | `PrimaryColor` `#2563EB` + Hover `#1D4ED8` + Pressed `#1E40AF`, `SecondaryColor` `#0EA5E9` (cyan), `NavyColor` `#0F172A` | enterprise blue; cyan is accent-only, never a primary action |
| **Semantic** | `SuccessColor` `#10B981`, `WarningColor` `#F59E0B`, `DestructiveColor` `#DC2626`, `InfoColor` `#3B82F6` | each with a matching `*FgColor` |
| **Nav rail** | `RailColor` `#0B1524`, `RailAltColor`, `RailFgColor`, `RailFgActiveColor`, `RailHoverColor`, `RailActiveColor`, `RailBorderColor` | the rail is a **dark island in a light app** — it needs its own ramp, which is why these are separate tokens rather than reused semantics |
| **Soft tints** | `PrimarySoft`, `SuccessSoft`, `WarningSoft`, `DangerSoft`, `CyanSoft`, `VioletSoft` (+ `*FgBrush`, some `*BorderBrush`) | badge and stat-tile fills; the `Fg` variant is the darkened text pair, never the base colour |
| **Data rows** | `RowAltBrush` `#FAFBFD`, `RowHoverBrush` `#F1F6FE` | zebra + hover for the two table pages |
| **Gradients** | `HeroScrimBrush` (3-stop navy scrim), `LoginPanelBrush` (diagonal deep-navy field) | the scrim is what keeps white text legible over photography |
| **Shadows** | `ShadowSm`, `CardShadow`, `ShadowLg`, `ShadowXl` | Avalonia `BoxShadows` order is `offsetX offsetY blur spread color` |
| **Radii** | `RadiusSm` 8, `RadiusMd` 10, `RadiusLg` 14, `RadiusXl` 20, `RadiusPill` 999 | |
| **Icons** | `ShieldGeometry`, `CheckGeometry`, `SpinnerArcGeometry`, `GaugeGeometry`, `ChipGeometry`, `GridGeometry`, `UserGeometry`, `SearchGeometry`, `RefreshGeometry`, `DownloadGeometry`, `TimerGeometry` | `StreamGeometry` path data, stroked not filled — one definition each; `TimerGeometry` = roaming **Tracker control** rail |
| **Fonts** | `DisplayFont`, `BodyFont` → `avares://Avalonia.Fonts.Inter/Assets#Inter` | Inter ships as a NuGet package; no font file to bundle |

**Colour + brush pairing convention.** Every colour is declared as a `<Color>` then wrapped in a `<SolidColorBrush>` of the same name + `Brush`. Bind to the **brush**; the raw `Color` exists so gradients and animations can reference it. `TransparentBrush` is declared explicitly because `Value="Transparent"` on a `Setter` and a brush resource are not interchangeable in every Avalonia selector context.

---

## 3. Style classes — `App.axaml` (419 lines, ~55 selectors)

Zero inline styling in the pages: every visual is a class. Adding a screen means composing existing classes, not writing new setters.

| Class | Applies to | Purpose |
| ----- | ---------- | ------- |
| `.primary` `.secondary` `.success` `.danger` `.ghost` | `Button` | the action ramp. `.primary` has `:pointerover` / `:pressed` / `:disabled` states; `.ghost` is chromeless until hover (icon buttons) |
| `.nav` + `.active` | `Button` | rail item. **`Button.nav Path` / `.nav:pointerover Path` / `.nav.active Path`** set `Stroke` via descendant selectors — `Stroke` is not an inherited property, so `Foreground` alone cannot colour the icon |
| `.tab` + `.active` | `Button` | segmented Applications/Packages switcher. `Button` has no `BoxShadow` property (only `Border` does), so the selected tab is marked by the raised card fill + primary text |
| `.card` | `Border` | white surface, 1px border, `RadiusLg`, `ShadowSm`, 20px padding |
| `.badge` + `.ok` `.warn` `.info` | `Border` | pill. `Border.badge > TextBlock` styles the child text, and each variant re-styles that child — the direct-child selector is what lets a badge be `<Border Classes="badge ok"><TextBlock .../></Border>` with no per-use styling |
| `.h1` `.h2` `.eyebrow` `.label` `.value` `.metric` `.mono` | `TextBlock` | the type scale. `.eyebrow` is the letterspaced uppercase micro-label; `.metric` is the 26px tile number; `.value` trims with ellipsis |
| `.row` + `.alt` | `Border` | table rows: `.alt` is the zebra stripe, both share a hover |
| `.table` | `ListBox` | strips Fluent chrome (`ListBox.table ListBoxItem` zeroes padding/margin/min-height) so a `ListBox` reads as a data table while keeping **virtualisation** |
| `.search` `.readonly` | `TextBox` | pill search field; flat grey read-only detail field |
| `.fade-in` | `Border` | 0.35s opacity — wizard step transitions |
| `.page-in` | `Border` | 0.32s opacity + 10px rise, `CubicEaseOut` — applied to each page root so nav switches read as a transition, not a flicker |
| `.hero-drift` | `Image` | 24s alternating 1.0→1.08 `ScaleTransform` — slow Ken Burns on hero photography |
| `.pulse` | `Ellipse` | 1.8s opacity breathe for the live-tracking dot |
| `.spinner` | `Path` | 0.9s infinite `RotateTransform.Angle` 0→360. Avalonia 12 has no `ProgressRing`; animate **`RotateTransform.Angle`** (a registered animator), not `RenderTransform` itself, which has no default animator |

---

## 4. The six pages

Each page is a `UserControl` in `Views/Pages/` with a compiled-binding `x:DataType`. Pages 1–3 bind to `MainViewModel` (they are shell states); pages 4–6 bind to their own VM.

| # | File | Lines | `x:DataType` | Hero asset | What it shows |
| - | ---- | ----- | ------------ | ---------- | ------------- |
| 1 | `SplashPage.axaml` | 172 | `MainViewModel` | — (navy field + white card) | 3-step boot sequence with a progress bar |
| 2 | `LoginPage.axaml` | 136 | `MainViewModel` | `login-hero.png` | split layout: artwork left, credential card right |
| 3 | `PermissionSetupPage.axaml` | 203 | `MainViewModel` | — | one card per wizard step, shared stepper header |
| 4 | `DashboardPage.axaml` | 275 | `DashboardViewModel` | `dashboard-hero.png` | identity hero band + status/inventory tiles |
| 5 | `SystemSpecsPage.axaml` | 231 | `SystemSpecsViewModel` | `specs-hero.png` | machine, compute, network, storage, attached devices |
| 6 | `InstalledAppsPage.axaml` | 191 | `InstalledAppsViewModel` | `apps-hero.png` | virtualised Applications / Packages inventory + search |

Hero images live in `Assets/backgrounds/` and are referenced as `avares://client/Assets/backgrounds/<name>.png`. They are compiled in by the existing `<AvaloniaResource Include="Assets\**" />` glob in `client.csproj` — **adding a hero image needs no installer-script change**.

### Page 1 — Splash

`MainViewModel.RunSplashSequenceAsync()` drives `SplashStep` 0→3 while `AnimateSplashProgressAsync` tweens `SplashProgress` 0→25→55→90→100, then sets `IsSplashVisible = false`. Each step exposes three booleans (`StepNActive` / `StepNDone` / `StepNPending`) recomputed by `[NotifyPropertyChangedFor]` off both `IsSplashVisible` and `SplashStep`, so the checklist renders pending / spinning / ticked without a converter.

It is kicked off from `MainWindow.axaml.cs` `OnOpened`, inside a try/catch that falls back to `IsSplashVisible = false` — **a splash animation must never be able to strand the user on a loading screen**.

### Page 2 — Login

Artwork column collapses on narrow windows so the form is never squeezed. Uses all three of the boolean/string converters: `BoolInvert` disables the button while loading, `LoadingToText` swaps the button caption, `StringNotEmpty` shows the status line only when there is one.

### Page 3 — Permission setup

Step panels are mutually exclusive, driven by `MainViewModel.CurrentPermissionStep` (`enum PermissionStep { None, AutoStart, BackgroundRunning, Dependencies, OtherPermissions }`) through `IsAutoStartStep` / `IsBackgroundStep` / `IsDependencyStep` / `IsPermissionStep`. Titles and body copy come from the `StepTitle` / `StepDescription` / `StepButtonText` switch expressions, with the platform-specific text behind `GetPlatformPermission*()` — **adding a step touches the VM only**, and the step count itself is platform-aware (`CurrentPermissionStepNumber` returns 4 on Linux, 3 elsewhere, for `OtherPermissions`).

### Page 4 — Dashboard (Operational Overview)

`DashboardViewModel` (221 lines) aggregates what the collectors already persisted — it is **read-only, nothing here writes to the DB**. Sources: `GetEmployeeInfoAsync`, `GetStatusAsync("last_heartbeat_at")`, `GetLastDeviceHardwareInfoAsync`, `GetLastNetworkInfoAsync`, `GetOpenHardwareDevicesAsync`, `GetLatestStorageDevicesAsync`, plus a live `IInstalledAppDetector` count.

Two decisions worth keeping:
- **Sync health is tolerance-based, not binary.** `IsSyncHealthy` allows three missed collect cycles (`max(120s, CollectIntervalSec * 3)`) before it reports "Pipeline stalled" — a single slow cycle must not flash a red state at the user.
- **The app-count tile reads the stored `installed_applications` count from SQLite** — the exact table page 6 renders — so the tile, the page, and the DB can never disagree.

The `Or` / `Join` / `FormatMb` / `FormatDuration` / `FormatAgo` helpers are `internal static` here and reused by `SystemSpecsViewModel` and `InstalledAppsViewModel` — one em-dash placeholder convention (`"—"`) across all three pages.

**Software update banner (2026-08-12).** The dashboard also hosts a self-update banner at the top of the page when `Update.UpdateAvailable` — version + release notes, a download progress bar while streaming, and **Download & Install** / **Later** buttons (Later persists `update_dismissed_version`). `DashboardViewModel.Update` is the SAME singleton `AppUpdateService` the top bar binds, so the two surfaces never disagree (see `client/ARCHITECTURE.md` §18).

### Page 5 — System Specs

`SystemSpecsViewModel` (114 lines) renders the newest `device_hardware_info` row plus its storage children, current network identity, and everything still physically plugged in (`unplugged_at IS NULL`). `HasSnapshot` is false until the collector has written its first row — the page shows an empty state rather than a grid of dashes. Attached devices are sorted by `DeviceClass` then `Product` (stable ordering between refreshes); the dashboard sorts the same data by `PluggedAt` descending and takes 8, because there it is a recency feed rather than an inventory.

### Page 6 — Installed Applications

`InstalledAppsViewModel` (204 lines) reads the **stored inventory straight from SQLite** via `ILogStore.GetAllInstalledAppsAsync` / `GetAllInstalledPackagesAsync` — the exact tables the collector writes and the rows that get synced upstream. Display never scans the OS: the collector owns the OS scan, which is **100% event-driven** — `InstalledSoftwareWatcher` (a background service watching app evidence — `.desktop` dirs, Start Menu, Program Files, `/Applications` — PLUS package-manager state: `/var/lib/dpkg`, npm global root resolved at runtime, pip `~/.local/lib`, snap db, flatpak runtimes, `%APPDATA%\npm\node_modules`, Homebrew Cellars) calls `LogCollectorService.RescanInventoryAsync` the moment an app OR package install/uninstall happens; there is deliberately **no minute-based periodic scan** (the collector's `%30/%60` scans were removed; the one-time startup scan is the only other scan). The page's **Rescan** button also calls `RescanInventoryAsync`. This always shows exactly what the DB holds (no raw detector drift: Windows would otherwise rediscover each app up to ~4× across Start Menu + registry sources).

While the page is on screen a lightweight `DispatcherTimer` (5s, started/stopped by the page's `IsVisible` change) calls `PollAsync` — a pure SQLite re-read with an `IsBusy` reentrancy guard — so installs/uninstalls the background watcher just recorded appear live **without any click or restart** (round 3, 2026-08-10).

- **`InventoryRow` projection.** Applications and packages have different shapes on disk but the same shape on screen, so both are projected onto one record and a single virtualised template renders either tab.
- **`SearchKey` is precomputed and lowercased at projection time**, so filtering thousands of rows stays a plain `Contains` with no per-keystroke allocation.
- **Page loads are pure SQLite reads** — `GetAllInstalledAppsAsync` / `GetAllInstalledPackagesAsync` are fast local queries, no `Task.Run` offload needed. Results are cached in `_allApps` / `_allPackages` until the next refresh; tab switching and searching re-filter the cache and never touch the DB again.
- **`AccentFor` hashes the name** into a fixed 8-colour palette so a given app keeps its colour between refreshes instead of flickering.
- **Install / uninstall lifecycle — ONE ROW PER INSTALL CYCLE.** The inventory is a full install→uninstall→reinstall history: a row is opened when a cycle starts (`install_date` = the OS-reported date, or **NULL = “Unknown”** for pre-existing software the tracker never saw installed — Linux sources report none) and closed when the cycle ends (`uninstall_date`, `is_installed=0`). A reinstall opens a **NEW row** with the fresh time — two installs of the same app = two records, exactly `record 1 (install → uninstall)` + `record 2 (install → …)`. `LogCollectorService` diffs every completed scan against the stored inventory: open rows missing from the scan close (`is_installed=0` + `uninstall_date`); the next scan's store pass opens a brand-new row when an app reappears. Nothing is ever deleted — closed cycles stay in SQLite and sync upstream — but since 2026-08-11 the **table lists only currently-installed rows**: `ApplyFilter` skips `IsInstalled == false` and there is no Uninstalled column (the old dimmed-history treatment via `RowOpacity` 0.5 was removed with the property). `app_name` / the package fingerprint are deliberately **not unique** (a UNIQUE constraint would block reinstall history). The GUI shows `Installed` (`Unknown` when NULL) per row; page + dashboard badges count **currently-installed** cycles only. A 50% confidence guard skips the uninstall pass when a scan returns <50% of the open rows, so a failed/partial scan can never falsely uninstall everything. The v1 design (single row per app with `install_count` + `×N` badge) was replaced by this rows-per-cycle model on 2026-08-10.

---

## 5. Router and binding patterns

**Guard properties (`MainViewModel`).** `IsProfile => IsLoggedIn && CurrentPermissionStep == PermissionStep.None` and `RequiresPermissionAction => IsLoggedIn && CurrentPermissionStep != PermissionStep.None`. Both carry `[NotifyPropertyChangedFor]` from `IsLoggedIn` and `CurrentPermissionStep`, so the router reacts to either change.

**Navigation.** `NavigateCommand` takes a **string parameter** — `"dashboard"` / `"specs"` / `"apps"` — maps it to `AppPage`, then awaits `RefreshActivePageAsync()`. `ActivePage` drives `IsDashboardPage` / `IsSystemSpecsPage` / `IsInstalledAppsPage` (page visibility and `Classes.active` on the rail) plus `ActivePageTitle` / `ActivePageSubtitle` (top bar), all via `[NotifyPropertyChangedFor]`. Data loads **lazily per page**; each page VM guards re-entry with its own `IsBusy`, so rapid nav clicks cannot stack overlapping scans.

`EnterShellAsync()` loads the landing page from every point where the user can arrive at the shell (startup, login, last permission granted) rather than from a property-changed hook — so the load never fires mid-wizard.

**The `$parent[Window]` cast pattern.** Each hosted page's `DataContext` is re-pointed to its own page VM:

```xml
<pages:DashboardPage DataContext="{Binding Dashboard}"
                     IsVisible="{Binding $parent[Window].((vm:MainViewModel)DataContext).IsDashboardPage}"/>
```

Because of that re-pointing, a page (or an attribute set on it from the shell) that needs **shell** state has to walk back up to the window and cast. This is the one binding idiom in the UI that looks unusual, and it is load-bearing — a plain `{Binding IsDashboardPage}` there would resolve against `DashboardViewModel` and silently never be true.

**Class toggling.** State-driven styling uses `Classes.active="{Binding IsDashboardPage}"` rather than binding `Background`, keeping colour decisions in `App.axaml`. Boolean negation is inline (`{Binding !IsSplashVisible}`); only where a converter earns it does one exist.

**Converters** (`Converters/`, four of them — declared per-file in `UserControl.Resources`, and in `Window.Resources` for the router):

| Converter | Used by | Why it exists |
| --------- | ------- | ------------- |
| `BoolInvertConverter` | Login, PermissionSetup | `IsEnabled` inversion where `!` syntax is not available on the target |
| `StringNotEmptyConverter` | Login, PermissionSetup, Dashboard, InstalledApps | show a row only when its string has content — avoids an `IsXVisible` bool per field |
| `LoadingToTextConverter` | Login | button caption ↔ loading state |
| `PercentToGridLengthConverter` | Splash, PermissionSetup | progress bars are built from a two-column `Grid` (fill + `ConverterParameter=remainder`) because a star-sized fill is the cheapest way to animate width without a template |

---

## 6. Branding — nothing in the UI names the product

The UI reads all brand strings from `Core/AppInfo`, re-exposed by `MainViewModel` so XAML can bind them:

| Binding | `AppInfo` source | Where it renders |
| ------- | ---------------- | ---------------- |
| `AppTitleWithVersion` | `DisplayName` + `Version` | window title, log banners |
| `AppDisplayName` | `DISPLAY_NAME` | rail wordmark, splash, tray tooltip (via `App.axaml.cs`) |
| `AppTagline` | `TAGLINE` (optional key — defaults to `ENTERPRISE SECURITY SUITE`) | under the wordmark |
| `AppInitials` | computed first+last initial of `DisplayName` | logo tile |
| `AppVersionDisplay` | `VERSION` → `"Version 0.2.0"` | rail footer, splash footer |
| `AppCopyright` | `PUBLISHER` + current year | footers |

`APP_IDENTIFIERS` is embedded as `client.APP_IDENTIFIERS` and parsed with a strict regex (read as data, never executed); `VERSION` arrives through `InformationalVersion` with any `+sha` suffix stripped. See [APP_IDENTIFIERS_README.md](./APP_IDENTIFIERS_README.md), [VERSION_README.md](./VERSION_README.md), and the *Branding-Single-Source Rule* in [../AGENTS.md](../AGENTS.md) §6 — including the warning that `EncryptedConfigService`'s `TransportKeySeed` / `MachineKeyPrefix` are **KDF seeds, not branding**, and must never be templatized.

---

## 7. Adding a seventh page

1. **ViewModel** — `ViewModels/YourPageViewModel.cs`, extend `ViewModelBase`, `[ObservableProperty]` for state, one `[RelayCommand] RefreshAsync(CancellationToken)` guarded by `IsBusy`. Reuse `DashboardViewModel.Or/Join/FormatMb/FormatAgo` for placeholders and formatting.
2. **Register** — `builder.Services.AddTransient<YourPageViewModel>()` in `Program.cs`, and take it as a constructor parameter on `MainViewModel`, exposed as a `{ get; }` property.
3. **Enum + guards** — add to `MainViewModel.AppPage`, add `IsYourPage => ActivePage == AppPage.YourPage`, and list it in the `[NotifyPropertyChangedFor]` block on `_activePage`. Add its arm to `NavigateAsync`'s switch (pick a short string token), `RefreshActivePageAsync`, `ActivePageTitle`, and `ActivePageSubtitle`.
4. **View** — `Views/Pages/YourPage.axaml` with `x:DataType="vm:YourPageViewModel"`, root `Border Classes="page-in"`, composed from existing classes. Add a `StreamGeometry` to `AppTheme.xaml` if it needs a new icon.
5. **Rail + host** — one `Button Classes="nav" Classes.active="{Binding IsYourPage}"` in `MainWindow.axaml`, and one entry in the page host using the `$parent[Window].((vm:MainViewModel)DataContext).IsYourPage` visibility pattern.
6. **Verify** — `dotnet build`, then per the Installer-Parity Rule build and install the artifact and click through it. New `Assets/` files and `APP_IDENTIFIERS` need no script change; any **other** new runtime file does.
