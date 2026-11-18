# Optimization Plan — Memory, CPU & Process Footprint

> **Audited:** 2026-11-18 (branch `sohaib`, client **2.9.x**)
> **Scope:** `client/` (tracker), `client/DlpService/` (Windows SYSTEM service), `server/` (Go API + SFU),
> `server/face_worker/` (Python), `web/` (Next.js dashboard).
> **Method:** static code review of every hot path. Line numbers are from the audited tree and will drift —
> search for the symbol if a line no longer matches. **All MB / rate figures are estimates from the code,
> not measurements.** Items marked ✅ were re-checked by hand during the audit; the rest were found by
> reading but not individually re-verified.
>
> **Target hardware:** employee PCs = i5 3rd-gen / 8 GB RAM, often HDD. Server target = 1000 agents,
> 100 live streams, 100 admins, **one Postgres** (see AGENTS.md *Single-Postgres Rule*).

---

## 0. How to read this file

| Prefix | Component | Process |
|---|---|---|
| `C-MEM-xx` | Desktop tracker | `client.exe --background` (always on, user session) |
| `S-MEM-xx` | DLP service | `AlphaAITracker.Service.exe` (always on, LocalSystem) |
| `G-MEM-xx` | Go API / SFU | `server` |
| `P-MEM-xx` | Face worker | `python worker.py` |
| `W-MEM-xx` | Web dashboard | admin browser tab |

Severity: **Critical** = can crash/OOM or leaks continuously · **High** = large steady cost on every PC/request ·
**Medium** = grows over time or costs under specific load · **Low** = hygiene / small.

Every fix must still follow AGENTS.md: **Installer-Parity Rule** (verify from an installed build), and new
env knobs go through `.env` → `config.enc` / `service.env`.

---

## 1. Top 15 — fix these first

| # | ID | Sev | One-line problem | Where |
|---|---|---|---|---|
| 1 | C-MEM-01 ✅ | High | 1 `powershell.exe` + 1 `wmic.exe` spawned **per user process every 30 s** | `client/Platform/Windows/ProcessCollector.cs:65-70,196-217`, `client/Core/ParentProcessResolver.cs:247-280` |
| 2 | S-MEM-01 ✅ | Critical | Native DNS-cache list never freed (`DnsFree` missing) → ~150–500 MB/day | `client/DlpService/DnsCacheReader.cs:20-37` |
| 3 | G-MEM-01 ✅ | Critical | `BodyLimit` registered **before** `Decompress` → unauthenticated gzip bomb | `server/internal/router/router.go:54-55` |
| 4 | S-MEM-04 | High | 6 spawn helpers read stdout synchronously → timeout/kill is dead code, child + lock held forever | see S-MEM-04 |
| 5 | S-MEM-02 | High | Defender guard starts PowerShell every 30 s | `client/DlpService/AntivirusGuardWorker.cs:118-141` |
| 6 | S-MEM-03 | High | Lockdown verify: ~9 netsh + 3 PowerShell per 60 s tick | `client/DlpService/LockdownNetworkEnforcer.cs:62,203-231,469-505,739-747` |
| 7 | C-MEM-02/03 | High | ~5 full `Process.GetProcesses()` per cycle, never disposed; O(n²) Toolhelp snapshots | `ProcessCollector.cs:31,38`, `ParentProcessResolver.cs:13,38,197-220`, `LogCollectorService.cs:487-488` |
| 8 | C-MEM-04 | High | UIA reader rebuilds full descendant trees of every window every 3 s, COM elements unreleased | `client/Core/BrowserAccessibility/WindowsUiaBrowserReader.cs:57,167,199-215` |
| 9 | G-MEM-03 | High | pgx statement cache fills with ~512 giant one-off `VALUES` statements per connection | `server/internal/repository/new_schema_repo.go:47-560`, `server/internal/database/postgres.go:23` ✅ |
| 10 | S-MEM-05 | High | `PinnedIps` grows for days; each new IP deletes + re-adds every PIN firewall rule | `client/DlpService/DomainBlockEnforcer.cs:70,514-565,620-643` |
| 11 | G-MEM-04 | High | Rate-limiter map keyed by spoofable `X-Forwarded-For` (no `IPExtractor`), no size cap | `server/internal/admit/limiter.go:49-57`, `router.go:105` |
| 12 | C-MEM-05 | High | Live stream allocates ~5 MB of new LOH buffers per frame (~75 MB/s) | `client/Services/ScreenCaptureService.cs:531,656,662`, `Streaming/ScreenVp8Encoder.cs:137,240` |
| 13 | G-MEM-17 | High | No `GOMEMLIMIT`, no pprof — server can't be measured or soft-capped | `server/cmd/server/main.go` |
| 14 | C-MEM-06/07 ✅ | High/Med | Two client caches with **no eviction** (`_correlationCache`, `_contextCooldown`) | `EventCoordinator.cs:13,242`, `LogCollectorService.cs:85,1035` |
| 15 | G-MEM-05 | High | Screenshot upload parses full multipart **before** idempotency check, no concurrency gate | `server/internal/handlers/screenshot_handler.go:34-40` |

**Expected outcome on an office PC after items 1, 2, 4–8, 10, 12, 14:** most of the idle CPU goes away
(roughly 200–400 PowerShell/wmic spawns per minute from the tracker, plus about 6 PowerShell and 12 netsh
per minute from the service, all stop), and private-bytes growth over a day becomes flat. Measure before
and after (section 8).

---

## 2. Desktop tracker — `client/` (`client.exe --background`)

> The always-on process is `--background`. The GUI (`--gui`), face lock (`--face-verify`) and
> `--timedatewarning` are separate short-lived processes. Steady-state numbers that matter are those of
> the background process.

### High

**C-MEM-01 ✅ — Process spawn per user process per cycle (High)**
- *Where:* `Platform/Windows/ProcessCollector.cs:65-70` calls `GetProcessCommandLine(pid)` →
  `powershell -Command Get-CimInstance Win32_Process` (`:196-217`), then
  `ParentProcessResolver.GetBrowserProfile` → `wmic process where processid=…` (`Core/ParentProcessResolver.cs:247-280`)
  for **every** process passing `ProcessFilter.IsUserProcess` (`Core/ProcessFilter.cs:110-156`) — usually 100–200.
- *Cost:* 200–400 child processes per 30 s cycle; each PowerShell ≈ 40–70 MB commit while alive. A cycle can run
  past its interval, so the PC never idles. `wmic` is also removed on Windows 11 24H2.
- *Fix:* read the command line in-process with `NtQueryInformationProcess(ProcessCommandLineInformation = 60)`
  on a `PROCESS_QUERY_LIMITED_INFORMATION` handle (or one WMI query for all PIDs per cycle). Cache by
  `(pid, startTime)` — a command line never changes. Call `GetBrowserProfile` only when
  `IBrowserRegistry.IsBrowser(name)` is true.
- *Verify:* Process Monitor "Process Create" with parent = `client.exe`, 10 min, before/after.

**C-MEM-02 — Undisposed `Process` arrays, ~5 enumerations per cycle (High)**
- *Where:* `ProcessCollector.cs:31,38`; `ParentProcessResolver.cs:13` (`BuildProcessTree`), `:38`;
  `LogCollectorService.cs:487-488`.
- *Cost:* 5×N `Process` objects + `SafeProcessHandle`s per cycle left to the finalizer; 1–3 MB garbage per
  cycle, handle-count spikes of +1–2k. `MainWindowHandle` enumerates all windows per process.
- *Fix:* one Toolhelp snapshot per cycle (see C-MEM-03) shared by collector + `LogCollectorService`;
  `try/finally { foreach (var p in procs) p.Dispose(); }`; use the existing `EnumerateAllWindowTitles` map
  instead of `MainWindowHandle`.
- *Verify:* Task Manager Handles column over 10 min; `dotnet-counters` gen0/gen1 rate.

**C-MEM-03 — O(n²) Toolhelp snapshots (High)**
- *Where:* `ParentProcessResolver.GetParentPidWindows` (`:197-220`) creates a new `CreateToolhelp32Snapshot`
  per process, inside `BuildProcessTree` (called twice per cycle).
- *Fix:* one snapshot → fill `pid→ppid` and `pid→exeName` in one pass (also replaces `BuildProcessNameMap`).

**C-MEM-04 — Windows UIA browser reader (High)**
- *Where:* `Core/BrowserAccessibility/WindowsUiaBrowserReader.cs` — `new CUIAutomationClass()` every poll (`:57`);
  two `FindAll(TreeScope_Descendants)` per **non-browser** window (`:199-215`); `DetectIncognito` collects every
  descendant button including page content (`:167`); `Process.GetProcessById` per window (`:81`).
- *Cost:* hundreds–thousands of `IUIAutomationElement` RCWs per 3 s poll; only the root is released
  (`:132-139`). Estimate 20–80 MB transient native memory with Office/VS Code/Chrome open, plus high CPU.
- *Fix:* one long-lived `CUIAutomation` on a dedicated MTA thread; `IUIAutomationCacheRequest` for
  Name/ControlType/Value; `TreeWalker` with a node + time budget (Linux uses 400 nodes); scan webviews only for
  `Chrome_WidgetWin_1`-class windows and only every 5th poll (Linux parity); incognito check limited to the
  toolbar subtree and cached per HWND; skip windows whose title is unchanged; `Marshal.ReleaseComObject` on
  each element/array.
- *Verify:* `dotnet-gcdump collect -n client` → `__ComObject` count and finalizer queue.

**C-MEM-05 — Live-stream per-frame LOH allocations (High, while streaming)**
- *Where:* `Services/ScreenCaptureService.cs:656,662` (`new byte[w*h*4]` + double copy from `_bgraScratch`),
  `:531` (`DownscaleBgraNearest`), `Services/Streaming/ScreenVp8Encoder.cs:137` (`PixelConverter.ToI420` new
  array), `:240` (encoded packet up to ~270 KB).
- *Cost:* 1280×720 @ 15 fps ≈ 75 MB/s LOH allocation → frequent gen2 GCs, fragmentation, +100–200 MB private bytes.
- *Fix:* ring of 2–3 reusable frame buffers (channel is capacity 1 / DropOldest, so a ring is safe) or
  `ArrayPool<byte>.Shared` with explicit return after encode; copy straight from `LockBits` into the rented buffer;
  own BGRA→I420 into a reusable field; reusable encode output buffer.
- *Verify:* `dotnet-counters monitor -n client --counters System.Runtime[loh-size,gen-2-gc-count,alloc-rate]`.

**C-MEM-06 ✅ — `EventCoordinator._correlationCache` never pruned (High over days)**
- *Where:* `Core/DesktopEventBus/EventCoordinator.cs:13,242`. `LimitCacheSize` (`:279`) only prunes
  `_dedupCache` and `_activeJourneys`; only `Dispose` clears it (`:305`).
- *Cost:* one `"{path}:{action}"` entry per file event under watched folders → 10k–100k entries over days
  (2–30 MB), never shrinks.
- *Fix:* prune entries older than `CorrelationWindow` when count > 1000 (same code as `_dedupCache`).

### Medium

**C-MEM-07 ✅ — `_contextCooldown` has no eviction** — `Services/LogCollectorService.cs:85,1035`. Key is
`{sessionId}|{itemType}|{identifier}` (titles/URLs/paths). Fix: drop entries older than `ContextCooldown`
every N cycles and when the session closes (`_sessionRootItems` cleanup near `:619`).

**C-MEM-08 — Screenshot path keeps full-monitor bitmaps forever** — `ScreenCaptureService.CaptureStill` (`:96`)
→ `CaptureFrame` caches `_srcBitmap`, `_scaledBitmap`, `_bgraScratch` (`:63,597-651`); released only on error
or Dispose. ~15–25 MB retained after the first 30 s screenshot even with no stream. Fix: call
`DisposeCaptureBitmaps()` and null `_bgraScratch` after `CaptureStill` when `!_streamActive`, and when a stream stops.

**C-MEM-09 — Screenshot encode double-copies 8 MB every 30 s** — `ScreenCaptureService.cs:656/662` +
`Screenshots/ScreenshotWebpEncoder.cs:15-17` (`new SKBitmap` + `Marshal.Copy`). Fix: encode from
`new SKPixmap(info, data.Scan0, stride)` over the `LockBits` buffer.

**C-MEM-10 — SyncService payload copies on the LOH** — `Services/SyncService.cs:827-836` re-serializes on every
halving step; `:833,974` `SerializeToUtf8Bytes`; `:1181-1184` gzip → `MemoryStream` → `ToArray()`. Three ~1 MB
LOH arrays per chunk. Fix: serialize once into a pooled `ArrayBufferWriter<byte>`; stream gzip through a custom
`HttpContent`, or `new ByteArrayContent(ms.GetBuffer(), 0, (int)ms.Length)`; estimate slice size from per-row
bytes; source-generated `JsonSerializerContext` to drop reflection.

**C-MEM-11 — `HttpResponseMessage` not disposed** — `SyncService.cs:227,1210`. Fix: `using var response = …`.

**C-MEM-12 — ~118 `SqliteCommand`s without `using`** — throughout `Storage/SqliteLogStore.cs` (e.g. `:427,654`);
prepared `sqlite3_stmt` natives live until finalization. Fix: mechanical `using var cmd`. Cache prepared
commands for the hottest inserts.

**C-MEM-13 — GUI re-reads the whole inventory every 5 s** — `Views/Pages/InstalledAppsPage.axaml.cs:22-44` →
`ViewModels/InstalledAppsViewModel.cs:174-214` (`SELECT *` incl. closed history, new row objects, `Clear()` +
per-row `Add`). `DashboardViewModel.cs:158` loads all rows just to count. Fix: query `is_installed=1` with needed
columns; skip when a `MAX(updated_at)`/count signature is unchanged; replace the collection in one go; stop the
timer when the window is hidden/minimized; use `SELECT COUNT(*)` for badges.

**C-MEM-14 — Browser History DB copied to `%TEMP%` on every change** — `BrowserHistoryReader.cs:186-221`
(Chrome `History` is often 20–150 MB, copied every ~10 s when it changes); `TryResolveUrl` copies 3000 visits per
call (`:129`). Fix: open read-only with `immutable=1`/`mode=ro` URI, copy only when a window actually lacks a URL
and at most once per 60 s per profile; search under the lock instead of `ToList()`.

**C-MEM-15 — Face lock preview: 3 Mat clones + BMP array + Bitmap per frame @15 fps** (short-lived
`--face-verify` process) — `Services/FaceVerify/FaceRecognitionEngine.cs:229-237`,
`WinForms/FaceVerifyLockerHost.cs:266-271` (`ImEncode(".bmp")` on LOH; disposing the backing `MemoryStream`
breaks the GDI+ contract), same in `ViewModels/FaceVerifyViewModel.cs:384-389`. Fix: reuse one `Bitmap` via
`BitmapConverter.ToBitmap(mat, existing)`; drop a frame while a UI update is pending (`Interlocked` flag); one
reusable preview `Mat`.

**C-MEM-16 — Face detect allocates a ~4.9 MB tensor per match** — `FaceRecognitionEngine.cs:602-615` (new
`DenseTensor<float>(1,3,640,640)` ~2/s, `ToArray()` copies, sub-Mat not disposed); `SessionOptions` at `:125`
not disposed. Fix: reusable tensor field filled from `Mat.Data` spans; `using` the sub-Mat; consider
`EnableCpuMemArena=false`.

**C-MEM-17 — PowerShell `Get-PnpDevice` every 30 s and 60 s** — `Services/HardwareDeviceWatcherService.cs:81,241-258`,
`Services/PortDeviceCollector.cs:26,184`; stderr redirected but never drained → can hang. Fix: native
`CM_Get_Device_ID_List` (reuse DlpService `CfgMgrDevices`) + `RegisterDeviceNotification` / `WM_DEVICECHANGE`;
at minimum `ProcessFilter.RunProbe` and 120 s cadence.

**C-MEM-18 — `GetExecutablePath` enumerates processes + every loaded module** — `LogCollectorService.cs:1207-1220`.
Fix: `QueryFullProcessImageNameW` on the known pid; dispose the array.

**C-MEM-19 — No GC tuning for an always-on agent** — `client.csproj` has no GC settings and no
`runtimeconfig.template.json` (defaults: workstation **concurrent** GC, TieredPGO, full ICU). Recommended:
```xml
<ServerGarbageCollection>false</ServerGarbageCollection>      <!-- never Server GC on 4-core / 8 GB -->
<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection> <!-- no background GC thread -->
<TieredPGO>false</TieredPGO>
<PublishReadyToRun>true</PublishReadyToRun>                    <!-- less JIT memory, faster boot -->
<UseSystemResourceKeys>true</UseSystemResourceKeys>
```
`runtimeconfig.template.json`:
```json
{ "configProperties": {
    "System.GC.ConserveMemory": 5,
    "System.GC.RetainVM": false
} }
```
Optional safety net for `--background` only: `DOTNET_GCHeapHardLimit=0x10000000` (256 MB). Consider
`InvariantGlobalization=true` only after confirming no culture-specific formatting is needed. Verify with
`dotnet-counters` `working-set` / `gc-committed` after 1 h, before vs after.

### Low

| ID | Problem | Where | Fix |
|---|---|---|---|
| C-MEM-20 | Undisposed `Process` from `GetProcessById`/`GetProcesses` | `SessionHierarchyResolver.cs:121`, `ParentProcessResolver.cs:72`, `MainViewModel.cs:723`, `ATSPIEventWatcher.cs:146,210`, `Platform/Linux/ProcessCollector.cs:26,42,564,591` | `using var` / dispose arrays |
| C-MEM-21 | USB-drive `FileSystemWatcher`s never removed on unplug; 24-journey cap is soft; recursion into `node_modules` | `FileSystemEventWatcher.cs:202-212,350-371` | remove watchers whose root vanished; hard LRU cap 24; structurally skip `node_modules`/`.git`/`bin`/`obj` |
| C-MEM-22 | `_pidNameCache` never evicted; python3 probe spawned every 3 s (Linux) | `LinuxAtSpiBrowserReader.cs:50,94,348-381` | rebuild per poll / clear every 10 min |
| C-MEM-23 | No WAL size limit / truncate; read connection uses `Cache=Shared` | `SqliteLogStore.cs:89-104,131` | `PRAGMA journal_size_limit=8388608`; `wal_checkpoint(TRUNCATE)` after retention; drop `Cache=Shared` |
| C-MEM-24 | File logger grows without cap within a run | `Services/FileLoggerProvider.cs:45-46` | roll at ~10 MB |
| C-MEM-25 | WS message buffer unbounded | `Services/LiveStreamClient.cs:161,193` | reject > 256 KB, recreate buffer |
| C-MEM-26 | Probe output (`winget`/`npm`/`dpkg`) read as one string | `Core/ProcessFilter.cs:28-48`, `PackageDetector` | parse with `ReadLineAsync` |
| C-MEM-27 | Capture thread polls every 200 ms while idle | `ScreenCaptureService.cs:301-307` | wait on `ManualResetEventSlim` set when stream starts |
| C-MEM-28 | Shell COM via `dynamic` pulls DLR (one-time 5–10 MB) | `Watchers/WindowsExplorerWatcher.cs:125-176` | typed `[ComImport]` interop |

**Already good (client):** one shared `HttpClient`; downloads stream with `ResponseHeadersRead`; frame channel
bounded (1, DropOldest); GDI HDCs released in `finally`; VP8 uses `VpxImgWrap` (no per-frame native alloc);
history reader capped at 3000 visits; journey/dedup caches capped; `SystemEvents` unsubscribed; GUI is a separate
process; InstalledApps list is virtualized; ORT only lives in the short-lived face process.

---

## 3. DLP service — `client/DlpService/` (LocalSystem, 24/7)

### Child-process budget (defaults from `DlpConfig.cs`)

Idle with lockdown on and AppLocker enforcing: **~6 `powershell.exe` and ~12 `netsh.exe` per minute**
(≈ 10–20 % of one core on an i5-3rd-gen just for spawning; 0.3–0.6 PowerShell alive on average, peak ~2 =
~200–250 MB). A PC with AppLocker stuck in "not enforcing" roughly **triples** this.

| Source | Exe | Idle cadence | Per call | Output read | Timeout/kill |
|---|---|---|---|---|---|
| `DefenderGuard.Enforce` `:332` | powershell (EncodedCommand) | **30 s** | 1 | async ✔ | ✔ |
| `DefenderGuard.ReadStatus` `:192` | powershell | 150 s | 1 | async ✔ | ✔ |
| Lockdown `SetDefaultOutboundAction` `:476-503` | netsh ×9 + powershell ×1 | **60 s** | 10 | sync ✘ | dead ✘ |
| Lockdown `ListLockRules` `:1005` | netsh show rule name=all | 60 s | 1 | sync ✘ | ✘ |
| Lockdown `EnsureQuicBlock` `:1019` | powershell -File | 60 s | 1 | sync ✘ | ✘ |
| Lockdown `QueryActiveNetworkProof` `:515` (+ `:745`) | powershell -File | 60 s | **2** | sync ✘ | ✘ |
| DomainBlock `OsStateMatchesDesired` `:262,335` | netsh show all | 60 s | 1 | sync ✘ | ✘ |
| DomainBlock PIN rebuild `:620-643` | netsh show all + 2·N/40 delete/add | per new IP batch, up to 1.5 s | 1+2N/40 | sync ✘ | ✘ |
| `AppControlCapability.Probe` `:393` | powershell | 10 min | 1 | sync ✘ | ✘ |
| `AppAllowEnforcer.Apply` `:122-179` | powershell ×6–10, sc ×6 | **60 s when stuck** | 12–16 | sync ✘ | ✘ |
| `WdacEnforcer.DeployCip` `:266` | CiTool.exe | 60 s if CIP present but not enforcing | 1 | sync ✘ | no kill |
| `UsbStorEnforcer.Run` `:186` | sc / reg | on change | 1–2 | never read | no kill |
| `ServiceAclHardener` `:90` | sc sdset | once | 1 | after wait (deadlock risk) | 10 s |

### Findings

**S-MEM-01 ✅ — DNS cache native list leak (Critical)**
- *Where:* `DnsCacheReader.cs:20-37`. `DnsGetCacheDataTable` returns a dnsapi-allocated linked list; neither
  entries nor `pszName` are freed (no `DnsFree` P/Invoke anywhere in the project). An exception mid-walk also leaks.
- *Cost:* ~15–60 KB per call; called every 10 s, and every 1.5 s for 30 s after each new pin
  (`DnsBlockPinWorker.cs:17-18,129,177`) → ~150–500 MB/day of private bytes on a PC that hits blocked sites.
- *Fix:*
  ```csharp
  [DllImport("dnsapi.dll")] static extern void DnsFree(IntPtr p, int freeType); // 0 = DnsFreeFlat
  // walk: read entry, save next = entry.pNext, DnsFree(entry.pszName, 0); DnsFree(ptr, 0); ptr = next;
  ```
  in `try/finally` so the remainder is freed on exceptions. Longer term: ETW `Microsoft-Windows-DNS-Client`.
- *Verify:* Process Explorer private bytes rising while `dotnet-counters` GC heap stays flat = native leak;
  run 1 h with a blocked domain active, before/after.

**S-MEM-02 — Defender guard PowerShell every 30 s (High)** — `AntivirusGuardWorker.cs:118-141`,
`DefenderGuard.cs:192,286-333`. No "nothing drifted → skip" path; also drives WmiPrvSE (MSFT_MpPreference)
growth. Fix: in-process drift check from `HKLM\SOFTWARE\Microsoft\Windows Defender\*` + Policies key (watch with
`RegNotifyChangeKeyValue`), run the PS enforce only on drift, on Defender events 5001/5007/5010/5012/5013, or
every 10–15 min; raise `ALPHA_AV_GUARD_SEC` default to 300.

**S-MEM-03 — Lockdown verify path runs on almost every tick (High)** — `LockdownNetworkEnforcer.cs:62`
(`VerifyEvery` = 1 min = `EnforceTickSec`), `:203-231,469-505,739-747`; `Finish` runs
`QueryActiveNetworkProof` a second time (`:745`). Fix: reuse the first proof in `Finish`; `VerifyEvery` 5–10 min;
call `SetDefaultOutboundAction`/`EnsureQuic` only when the proof shows drift; read profiles in-process via
`HNetCfg.FwPolicy2` COM (`DefaultOutboundAction`, `FirewallEnabled`) — no PowerShell.

**S-MEM-04 — Spawn helpers whose timeout never fires (High)**
- *Where:* `AppAllowEnforcer.cs:1044-1049`, `LockdownNetworkEnforcer.cs:1065-1070`, `PortDeviceEnforcer.cs:687-692`,
  `AppControlCapability.cs:403-408`, `WdacEnforcer.cs:277-280` (no kill), `DomainBlockEnforcer.cs:1035-1038`
  (`p.ExitCode` after timed-out wait throws, child never killed).
- *Why:* `StandardOutput.ReadToEnd()` blocks until the child closes stdout; a hung cmdlet or a full stderr pipe
  means `WaitForExit(timeout)` is never reached → ~100 MB child alive forever, the enforcer `Gate`/`PinGate` lock
  stays held, and EnforceWorker / pin worker wedge.
- *Fix:* one shared `ProcRunner` copying the correct pattern in `DefenderGuard.RunPowerShell` (`:902-918`):
  `ReadToEndAsync` on both streams, `WaitForExit(timeout)`, `Kill(entireProcessTree: true)`, bounded drain.
  Route **all** spawns (incl. S-MEM-13) through it.
- *Verify:* `dotnet-dump collect` → `clrstack -all`, look for threads parked in `StreamReader.ReadToEnd`.

**S-MEM-05 — `PinnedIps` unbounded + full PIN rebuild per new IP (High)** — `DomainBlockEnforcer.cs:70,514-565,620-643`.
Cleared only on generation bump; a stable policy keeps CDN IPs for days (500–5,000/week). Each new batch:
`show rule name=all`, delete **all** PIN rules, re-add in chunks of 40 → ~100 netsh at 2,000 IPs, possibly every
1.5 s, with short windows where no PIN rule exists; grows mpssvc/BFE memory. Fix: last-seen timestamp per IP,
expire after 6–24 h, cap ~2,000; append-only (fill last chunk / add `PIN-k`); better, one rule updated through
`INetFwRule.RemoteAddresses` (COM). Verify: `netsh advfirewall firewall show rule name=all | find /c "AlphaAI-DLP-PIN"` hourly.

**S-MEM-06 — Full firewall dumps buffered as strings ≥2×/min (Medium)** — `DomainBlockEnforcer.cs:996-1012`,
`LockdownNetworkEnforcer.cs:1002-1015`; 300–800 KB text → 0.6–1.6 MB strings, copied again (`:1040`), `Regex.Matches`
per rule → 3–6 MB LOH churn per minute. Fix: query by name (`show rule name="AlphaAI-DLP-…"`) or enumerate
`INetFwPolicy2.Rules` and filter by prefix; or stream lines via `OutputDataReceived`.

**S-MEM-07 — `AppLaunchWatcher` sweeps every 400 ms when armed (Medium)** — `AppLaunchWatcher.cs:31-54`,
`AppProcessGuard.cs:28-113`; ~250 `Process` objects + parent walk + SHA256 per sweep ≈ 3–4 GB/h allocated,
3–8 % of a core (disposal is correct). Fix: 1.5–2 s cadence; verdict cache keyed by `(pid, startTime)`; ETW
`Microsoft-Windows-Kernel-Process` start events; cache the allow-list fingerprint.

**S-MEM-08 — Stuck AppLocker re-applies every 60 s (Medium)** — `AppAllowEnforcer.cs:122-179,985-999`; 8–12
PowerShell + 6 sc.exe per minute + AppIDSvc restart + `Thread.Sleep(1500)` under the lock; compounds with
S-MEM-07. Fix: exponential back-off for the same fingerprint (1 → 2 → 5 → 15 min); immediate retry only on
fingerprint change or user Retry.

**S-MEM-09 — `IpCache` never removes expired entries (Medium)** — `DomainBlockEnforcer.cs:56,977`; keys are rotating
CDN hostnames. Fix: sweep `ExpiresUtc < now - 1h` each pin tick, or `MemoryCache` with `SizeLimit`.

**S-MEM-10 — `_seenHosts` write-only (Low)** — `DnsBlockPinWorker.cs:24,163`. Delete it.

**S-MEM-11 — Pooled SQLite handles on users' tracker DBs (Medium, handles)** — `TrackerTokenProbe.cs:27` opens
`client.db` read-only on every `RefreshCredentials()` (16 call sites, every tick); default pooling keeps the native
handle + up to ~2 MB page cache open forever, and can make the tracker's wipe `File.Delete` fail (sharing
violation). Fix: `Pooling=False` (or `SqliteConnection.ClearPool`), and cache the token by DB `LastWriteTimeUtc`.
Verify: `handle.exe -p AlphaAITracker.Service.exe | findstr /i client.db`.

**S-MEM-12 — New `HttpClient` per policy ack (Low/Med)** — `PolicyAckClient.cs:19`, `EnforceWorker.cs:183-184`;
new socket + TIME_WAIT every tick per PC (~1,000/min fleet-wide). Fix: use the `"dlp"` factory client; ack only
when `(version, hash, stage)` changes.

**S-MEM-13 — Other spawn hygiene (Low)** — `UsbStorEnforcer.cs:184-196` (streams redirected, never read, no kill),
`ServiceAclHardener.cs:90-104` (reads after `WaitForExit` → deadlock if output > ~4 KB), `WdacEnforcer.cs:279`
(no kill). Route through the S-MEM-04 runner.

**S-MEM-14 — `-EncodedCommand` near the 32,767-char limit (Low, correctness)** — `DefenderGuard.cs:886-894`;
~10.5 KB script → UTF-16 + base64 ≈ 32–35 K chars. If over, `Process.Start` throws and enforce silently returns
`enforce_probe_failed`. Fix: temp `.ps1` + `-File` (as `LockdownNetworkEnforcer.cs:1050-1058` already does).

**S-MEM-15 — WDAC re-hashes CIP and re-runs CiTool every tick (Low)** — `WdacEnforcer.cs:27-34,59-61,91-104,162-165`,
`EnforceWorker.cs:527-557`. Fix: cache fingerprint by (length, mtime); back off CiTool like S-MEM-08.

**S-MEM-16 — Thread-pool starvation from blocking calls (Low)** — `DomainBlockEnforcer.ResolvePublicIps:930` uses
synchronous `HttpClient.Send` (8 s × A+AAAA, sequential); 400 ms sweep; `Task.Run` around long PowerShell →
25–40 threads instead of ~15 (≈1 MB stack reserve each). Fix: `await SendAsync`, capped parallel resolve, one
dedicated thread for long blocking loops.

**S-MEM-17 — Make GC config explicit (Low)** — `AlphaAITracker.Service.csproj` sets nothing (default is workstation
concurrent — fine). Pin it so it cannot regress: same csproj block as C-MEM-19 plus
`"System.GC.ConserveMemory": 5` (helps S-MEM-06 LOH churn); optional `GCHeapHardLimit` ~256 MB.

**S-MEM-18 — Indirect growth (Info)** — S-MEM-02/03 continuously drive **WmiPrvSE.exe**, **mpssvc** and **BFE**.
Measure those processes too, not only the service.

**Already good (service):** `TcpConnectionKiller` frees `AllocHGlobal` in `finally`; every `OpenProcess` has
`CloseHandle`; `ServiceAclHardener` `LocalFree`/`CloseServiceHandle`; `UserSessionProcessLauncher` closes tokens and
destroys env blocks; `EventLogReader`/records in `using` with a 200-record cap; all `RegistryKey` and `JsonDocument`
in `using`; `PolicyStore` trims audit to 200 and snapshots to 3; `NamedPipeHost` 4-instance cap, 64 KB line cap;
no Server GC; `DefenderGuard.RunPowerShell` is the correct spawn pattern to copy.

---

## 4. Go server — `server/`

**G-MEM-01 ✅ — Gzip decompression bomb (Critical)**
- *Where:* `internal/router/router.go:54-55` — `e.Use(middleware.BodyLimit("20M"))` then `e.Use(middleware.Decompress())`.
  The cap counts **compressed** bytes; the decompressed stream is unlimited. Global middleware → also covers public
  `/auth/login`, `/auth/refresh`, `/auth/employee-login`.
- *Cost:* 20 MB gzip at ~1000:1 → ~20 GB JSON; one request can OOM the process.
- *Fix:* after `Decompress`, wrap the body in `http.MaxBytesReader` (e.g. 64 MB for sync, 64 KB for auth); apply
  `Decompress` only to the `syncAdmit` group and reject `Content-Encoding` elsewhere.

**G-MEM-02 — No per-request row cap; each batch held ~3× (High)** — `dto/new_schema_dto.go:12`,
`services/new_schema_service.go:41,372,433` (DTO → model → args). 300k-row backlog ≈ 500 MB–1 GB per agent. Fix:
413 when `len(Entries) > 2000`; or stream with `json.Decoder.Token()` and flush every 500 rows.

**G-MEM-03 — pgx statement cache full of giant one-off statements (High, Go + Postgres)**
- *Where:* every `BulkInsert*` in `repository/new_schema_repo.go:47-560` builds `VALUES ($1..$N)` whose text varies
  with row count; `database/postgres.go:23` ✅ keeps pgx v5 default `QueryExecModeCacheStatement` (512/conn).
- *Cost:* up to 512 entries/conn × (~55 KB SQL + ~100 KB param OIDs) × 40 conns ≈ up to ~2 GB Go heap, plus a cached
  plan per entry in each Postgres backend.
- *Fix:* `pgx.QueryExecModeExec` for bulk statements (per call or pool `DefaultQueryExecMode`); better, a single fixed
  `INSERT … SELECT * FROM unnest($1::text[], …) ON CONFLICT …` or `CopyFrom` into a temp table + upsert. Also set
  `MaxConnIdleTime` so idle backends release memory.
- *Verify:* `SELECT count(*) FROM pg_prepared_statements` on a pooled backend.

**G-MEM-04 — Spoofable, uncapped rate-limiter map (High)** — `admit/limiter.go:49-57`, `router.go:105`
(`c.RealIP()`, no `e.IPExtractor`). Random `X-Forwarded-For` → unlimited visitors (≈250 B each, 10 min TTL) and
login-limit bypass. Fix: `e.IPExtractor = echo.ExtractIPFromXFFHeader(echo.TrustLoopback(true), <nginx range>)`;
hard cap ~100k entries; key sync limits by `device_id`.

**G-MEM-05 — Screenshot upload buffers before idempotency check (High)** — `handlers/screenshot_handler.go:34-40`
(`c.FormValue` triggers `ParseMultipartForm(32MB)`); only per-IP RL, no gate (`router.go:195`). Post-outage drain ≈
1–2 GB transient. Fix: `screenshotId` in a header/query checked first; `MultipartReader` streaming to disk; put
behind `admit.NewSyncGate`.

**G-MEM-06 — Live-stream employee list: full list + ~1000 Redis GETs every 2 s per admin (Medium)** —
`handlers/stream_handler.go:146-190`, `ws/hub.go:162`. 100 admins ≈ 75 MB/s allocation, 50k Redis RTT/s. Fix: one
in-process online snapshot refreshed each second (Redis `MGET`/`SMEMBERS`), employee list cached 30 s, web poll 5 s.

**G-MEM-07 — WebSockets have no read limit (Medium)** — `handlers/ws_handler.go:176`, `stream_handler.go:425,666`.
Fix: `conn.SetReadLimit(64<<10)` presence, `256<<10` signaling.

**G-MEM-08 — Pion SFU: NACK buffers per subscriber, sender RTCP never read (Medium)** — `stream/sfu.go:63,479`.
~1.2–1.5 MB NACK ring per subscriber track; 100 streams × 10 watchers ≈ 1.5–2 GB. Fix: per-sender
`go func(){ for { if _,_,err := sender.Read(buf); err != nil { return } } }()`; register only needed interceptors
(NACK responder size 256, RTCP reports); watchers-per-employee 3–4.

**G-MEM-09 — DeviceAuth goroutine + 2 UPDATEs per request (Medium)** — `middleware/device_auth.go:72-79`, also for
requests later rejected by RL/gate. Fix: in-memory debounce `sync.Map[deviceID]lastTouch` (skip < 60 s) or one
batched UPDATE every 30 s; move DeviceAuth touch after admission.

**G-MEM-10 — HTTP server without timeouts (Medium)** — `cmd/server/main.go:346`. Fix: `ReadHeaderTimeout=10s`,
`IdleTimeout=120s`, `ReadTimeout=60s` on non-WS routes (or nginx `client_header_timeout`).

**G-MEM-11 — Face enrollment multipart fully in RAM (Medium)** — `handlers/face_handler.go:66`. Fix: `MultipartReader`
streaming each part to `FACE_MEDIA_DIR`.

**G-MEM-12 — `NotifyWorker` goroutines up to 10 min each (Medium)** — `services/face_service.go:64,596`. Fix: bounded
channel with 1–2 notifier workers deduped by `jobId`, or worker answers 202 immediately.

**G-MEM-13 — Dashboard attendance is N+1 over all employees (Medium)** — `services/time_attendance_service.go:119-133`,
`dashboard_service.go:152`, `time_attendance_repo.go:153`. Fix: one set-based SQL for today's counts.

**G-MEM-14 — `GetEmployeeDetail` reads `hardware_devices` without LIMIT (Low/Med)** — `new_schema_repo.go:2266-2275`.
Fix: `LIMIT 200` or current-only + paged history.

**G-MEM-15 — Per-agent mailboxes / duplicate presence sockets (Low)** — `stream/hub.go:279`, `ws/hub.go:86`; ≈80 MB at
1000 agents × 2 sockets. Count it in capacity planning; close the superseded socket on reconnect.

**G-MEM-16 — Presence `Register` holds hub write lock across Redis call (Low)** — `ws/hub.go:89-95`. Call Redis after unlock.

**G-MEM-17 — No `GOMEMLIMIT`, `GOGC` or pprof (High, operational)** — Fix: `GOMEMLIMIT` ≈ 80 % of container RAM;
`net/http/pprof` on a localhost-only admin port; log `runtime/metrics` + `NumGoroutine` each minute.

**G-MEM-18 — Postgres per-connection memory (Low)** — ~5–15 MB per backend + `work_mem` × sort nodes in usage/insights
aggregates. Keep `work_mem` ~16 MB, set `MaxConnIdleTime` (`postgres.go:23-25` sets only Max/Min/Lifetime ✅).

**Related CPU/DB items from the earlier audit (still open):** website list runs `SELECT DISTINCT domain FROM app_items`
on every page (`services/monitoring_service.go:253`); policy pull builds the full bundle before the "unchanged"
check (`services/dlp_service.go:929-944`); retention deletes only 5000/1000 rows per hour with no `opened_at` index
(`jobs/retention_sweep.go:70-93`); catalog upsert regex scan per entry (`new_schema_repo.go:1741`); per-row browser-name
query (`new_schema_service.go:676`); `COUNT(*)` + OFFSET on every infinite-scroll page; no `pg_trgm` index for
`LIKE '%x%'` search.

**Already good (server):** `rows.Close()` everywhere; typed sync DTOs; installers/screenshots/face images served with
`http.ServeContent`; uploads saved via `io.Copy` + `LimitReader`; list `PerPage` capped at 100; retention deletes
DB-side; Asynq concurrency fixed; watch tickets capped at 2000 and reaped; SFU rooms/mailboxes reaped; `forwardRTP`
reuses its buffer.

---

## 5. Face worker — `server/face_worker/`

| ID | Sev | Problem | Where | Fix |
|---|---|---|---|---|
| P-MEM-01 | Med | Images decoded at full resolution, no pixel cap (crafted JPEG → up to ~3 GB) | `embed.py:484` | check dimensions first, reject > 4096²; `IMREAD_REDUCED_COLOR_2` above 1920; env `OPENCV_IO_MAX_IMAGE_PIXELS=16777216` |
| P-MEM-02 | Med | `ThreadingHTTPServer` unbounded threads all waiting on `PROCESS_LOCK` | `worker.py:296,663` | bounded `queue.Queue(maxsize=50)`, reply 202, one worker thread, 503 when full |
| P-MEM-03 | Low/Med | ORT arena keeps peak forever; intra = inter = cpu_count | `embed.py:55-63` | `enable_cpu_mem_arena=False` (or `kSameAsRequested`), `inter_op_num_threads=1`, intra ≈ cores/2 |
| P-MEM-04 | Low | ~280 MB model zip read fully into RAM | `worker.py:121` | `shutil.copyfileobj(resp, f, 1<<20)` |
| P-MEM-05 | Low | Request body read with no size cap | `worker.py:509-511` | cap at 64 KB |
| P-MEM-06 | Low | Per-job image buffers released late | job loop | `del bgr, img_resp` after embedding |
| P-MEM-07 | Info | Shared `requests.Session` safe only because of the global lock | — | one Session per thread if parallelised |

---

## 6. Web dashboard — `web/`

| ID | Sev | Problem | Where | Fix |
|---|---|---|---|---|
| W-MEM-01 | Med/High | Screenshot grid keeps every full-res image decoded; no virtualization | `components/screenshots/ScreenshotGrid.tsx:144-153,171` | server thumbnail (`?size=thumb`, ~320 px); `@tanstack/react-virtual`; `maxPages: 10` |
| W-MEM-02 | Med | Infinite queries never drop pages; each filter key keeps all pages 5 min | `components/providers.tsx:14-21`, 18 `useInfiniteQuery` users | `maxPages` 10–20 on journey/screenshot/alert lists; `gcTime: 60_000` for lists |
| W-MEM-03 | Med | Live-stream / theater poll every 2 s (server cost, see G-MEM-06) | `live-stream/page.tsx:58`, `theater/page.tsx:77` | 5 s or push presence over WS; memoize sidebar rows |
| W-MEM-04 | Low | Untracked recovery `setTimeout` keeps ws/pc alive 5 s | `lib/useLiveStreamSocket.ts:187` | store handle, clear in `cleanupPc` + unmount |
| W-MEM-05 | Low | Old `MediaStream` detached without stop on renegotiation | `useLiveStreamSocket.ts:170-180` | acceptable (old PC closed); confirm in `chrome://webrtc-internals` |
| W-MEM-06 | Low | `pages.flatMap` re-run every render | `ScreenshotGrid.tsx:51` | `useMemo` |
| W-MEM-07 | Low | `xlsx` (~400 KB) imported statically | `app/(app)/employees/page.tsx:9` | `await import('xlsx')` in handlers (also unmaintained, has CVEs) |
| W-MEM-08 | Low | Several polling queries | `dashboard/page.tsx:72`, `ports/page.tsx:172`, `antivirus/page.tsx:281` | keep `refetchIntervalInBackground: false` |
| W-MEM-09 | High | `react-scan` (dev tool) shipped in production bundle | `src/app/layout.tsx:4,32`, `components/ReactScan.tsx` | load only when `NODE_ENV==='development'` via `dynamic(…, {ssr:false})` |
| W-MEM-10 | Med | `useUrlQueryState` setter identity changes every render (inline `schema`) → effects/debounce restart | `hooks/use-url-query-state.ts` | keep `schema` in a ref; drop from `useCallback` deps |
| W-MEM-11 | Med | Employees row fade delay `i * 0.03` across all pages (row 400 waits 12 s), framer per row | `employees/page.tsx:636-640` | `Math.min(i % pageSize, 10) * 0.03` or CSS fade |
| W-MEM-12 | Med | All 66 pages `"use client"`, framer-motion on ~40, Google Fonts via CSS `@import` | `(app)/layout.tsx`, `globals.css:1` | server components for static pages; CSS fades; `next/font` |

**Already good (web):** live-stream hook closes PCs/tracks/timers/WS on unmount with 15 s back-off;
`use-video-fps` cancels its callbacks; `EnrollFaceDialog` revokes every object URL and stops camera tracks;
IntersectionObservers disconnected; background polling off by default; token refresh is single-flight.

---

## 7. Implementation roadmap

### Phase 1 — stop the bleeding (small, code-only, ~1 week)
1. **S-MEM-01** add `DnsFree` (one file).
2. **G-MEM-01** `MaxBytesReader` after `Decompress`; scope `Decompress` to sync routes.
3. **S-MEM-04 + S-MEM-13** shared `ProcRunner`; migrate every spawn.
4. **C-MEM-01/02/03** single native process snapshot per cycle; in-process command line; browser-only profile lookup.
5. **C-MEM-06/07, S-MEM-09/10** add eviction / delete write-only set.
6. **G-MEM-17** `GOMEMLIMIT` + localhost pprof (needed to measure everything else).
7. **W-MEM-09/11, W-MEM-07** react-scan dev-only, row delay, lazy xlsx.

### Phase 2 — reduce steady spawn load (~1–2 weeks)
8. **S-MEM-02/03/08/15** drift-driven enforcement + back-off; COM firewall reads; verify every 5–10 min.
9. **S-MEM-05/06** append-only PIN rules with TTL + cap; name-filtered rule queries.
10. **C-MEM-04** UIA rewrite (cache request, tree walker budget, release COM).
11. **C-MEM-17, S-MEM-07** native device enumeration / ETW process-start events.
12. **G-MEM-03/04/05/09** pgx exec mode + `unnest` upserts, `IPExtractor` + cap, streaming screenshot upload, touch debounce.

### Phase 3 — buffers, GC and scale (~2 weeks)
13. **C-MEM-05/08/09/10** pooled frame & payload buffers; release still-capture bitmaps.
14. **C-MEM-19, S-MEM-17** explicit GC config + `runtimeconfig.template.json`.
15. **G-MEM-02/06/07/08/10–13** row caps, presence snapshot, WS read limits, SFU sender RTCP + interceptor trim, server timeouts, face notify queue, set-based attendance.
16. **P-MEM-01..05**, **W-MEM-01/02/10/12**.
17. Split god files while touching them: `SqliteLogStore.cs` (3.8k lines), `LogCollectorService.cs` (2.5k),
    `new_schema_repo.go` (3.3k), `dlp_repo.go` (2.5k), `web/src/lib/api.ts` (2.5k).

Each phase ends with an installed-build test on a real i5-3rd-gen / 8 GB PC (Installer-Parity Rule).

---

## 8. Measurement kit (baseline before every phase, compare after)

**Employee PC (tracker + service), 1 h idle and 1 h normal use:**
```powershell
# managed heap vs working set (native leak = WS rises, GC heap flat)
dotnet-counters monitor -n client --refresh-interval 5 System.Runtime
dotnet-counters monitor -n AlphaAITracker.Service --refresh-interval 5 System.Runtime
# type-level diff
dotnet-gcdump collect -n client          # at 10 min and 6 h, diff type counts
# hung spawn threads
dotnet-dump collect -n AlphaAITracker.Service ; dotnet-dump analyze <file> -> clrstack -all
# firewall growth
netsh advfirewall firewall show rule name=all | find /c "AlphaAI"
netsh advfirewall firewall show rule name=all | find /c "AlphaAI-DLP-PIN"
# open handles on tracker DB held by service
handle.exe -p AlphaAITracker.Service.exe | findstr /i client.db
```
- Process Monitor / Sysmon Event 1: count child processes per minute with parent `client.exe` and
  `AlphaAITracker.Service.exe` (target after Phase 2: < 1/min each at idle).
- Process Explorer: Private Bytes + Handles graphs for `client.exe`, the service, **WmiPrvSE.exe**, **svchost (mpssvc/BFE)**.

**Server:**
- `go tool pprof http://127.0.0.1:<admin>/debug/pprof/heap` (`inuse_space`, `alloc_space`), `/goroutine`.
- Postgres: `SELECT count(*) FROM pg_prepared_statements;`, `pg_stat_activity`, backend RSS.
- Load test: 1000 simulated agents syncing + 100 watch sessions; record RSS, GC CPU, p99 latency.

**Face worker:** `tracemalloc` snapshot per job; RSS while processing a 4K and a crafted large-dimension JPEG.

**Web:** Chrome heap snapshot after scrolling ~1000 screenshots and 20 journey filter changes;
`chrome://webrtc-internals` after 50 tile switches (no lingering PeerConnections).

### Targets
| Metric | Target |
|---|---|
| `client.exe --background` private bytes after 24 h | flat (±20 MB of 1 h value) |
| Service private bytes after 24 h with blocked domains | flat (±20 MB) |
| Child processes / min at idle (tracker + service) | < 2 |
| Idle CPU on i5-3rd-gen (tracker + service + WmiPrvSE) | < 3 % |
| `AlphaAI-DLP-PIN` rule count | bounded (cap) |
| Server RSS under 1000-agent load | below `GOMEMLIMIT`, no OOM |
| Postgres `pg_prepared_statements` per backend | < 50 |
