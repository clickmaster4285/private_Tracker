# Hardware & Port Control — Build Plan

> Scan every port/interface on every Windows machine, show state per machine in the web
> dashboard, and let an admin **block / allow / alert** a port, device, or device-class —
> scoped to one employee or the whole fleet — enforced by the SYSTEM DlpService.
>
> Reuses the existing DLP spine: **publish → pull → enforce → alert** (same as USB block,
> web blocker, network/app lockdown). Nothing reaches the fleet until **Publish**.

---

## 0. The three hardware categories (scanner must cover all)

| Category | Examples | Scan | Block |
|---|---|---|---|
| **1. External physical ports** | USB-A/C, Thunderbolt, HDMI/DP/VGA/DVI, Ethernet RJ45, audio jack/mic, SD/MicroSD, serial/COM, parallel/LPT, PS/2, smart-card | ✅ | ✅ except video-out (detect/alert only) |
| **2. Internal interfaces** | NVMe M.2, SATA, PCIe, storage controllers | ✅ | ⚠️ removable/external only — **never** the boot/system disk |
| **3. Wireless & virtual** | Wi-Fi, Bluetooth, WWAN/4G/5G, virtual NICs (Hyper-V, WAN miniport) | ✅ | ✅ radios; virtual adapters = alert only |

Scanner auto-adapts per machine — no per-model config. Computer A (8 USB/HDMI/NVMe/Ethernet),
Computer B (3 USB/Wi-Fi/BT), Computer C (Thunderbolt/2×NVMe/dock) all fall out of one enumeration.

### Honest limits (designed-in, not hidden)
- **Video outputs (VGA/DVI/HDMI/DP)** — Windows has no supported API to disable a single
  display connector. → **detect + alert on connect**, never a dead "block" button. (Disabling the
  whole display adapter bricks the display, so it is not offered.)
- **Internal system/boot disk** — disabling it makes the PC unbootable. → inventory + alert only.
- **"Empty vs occupied"** is reliable for USB ports, Ethernet (link state), and display outputs;
  for audio jacks / legacy serial we always report enabled/disabled, not guaranteed occupancy.
- Enforcement is **Windows-first**. Linux = udev (USB/NIC) best-effort; macOS = detection only.

---

## 1. Data model (what each port row carries)

Two **independent** state dimensions per device/port:
- **Occupancy** — `empty` | `occupied` (+ attached device name/speed/link)
- **Health** — `ok` | `disabled` | `error` | `blocked_by_policy` | `removed` (last-seen history)

Fields: `instance_id` (stable PnP id), `category` (1/2/3), `pnp_class`, `class_guid`,
`bus_type` (NVMe/SATA/USB/PCIe…), `friendly_name`, `manufacturer`, `present`, `status`,
`port_ref` (controller/hub+port where known), `machine_id`, `last_seen_at`.

---

## 2. Client — scan (`client/Services/PortDeviceCollector.cs`, new)

Windows enumeration (proven live — see `portctl.ps1`):
- `Get-PnpDevice -PresentOnly` → full device tree (class, instance id, status, present)
- `MSFT_PhysicalDisk.BusType` → NVMe/SATA/USB per disk; `Get-Disk` IsBoot/IsSystem → protect flag
- `Win32_USBController` + `Win32_USBControllerDevice` → USB topology / per-port occupancy
- `Win32_VideoController` + `root\wmi WmiMonitorConnectionParams.VideoOutputTechnology`
  (0=VGA,4=DVI,5=HDMI,10=DP) → which video connector has a monitor
- `Get-NetAdapter` / `Win32_NetworkAdapter.PhysicalAdapter` → NIC phys/virt + link state
- `Win32_SoundDevice`, `Win32_SerialPort`, `Win32_ParallelPort`

Writes `machine_ports` SQLite rows (same `is_synced` drain pattern as `hardware_devices`),
stamped with `machine_id`. Full re-scan on a timer + on `WM_DEVICECHANGE` / plug events.
Gated by `ALPHA_PORT_SCAN_ENABLED` (default true). Reuses `HardwareDeviceWatcherService`
patterns (PowerShell probe with bounded wait, present/absent diffing).

## 3. Server

- Migration **065_machine_ports.sql** — `machine_ports` inventory (machine-scoped),
  `device_control_rules` (draft: target_kind `class|instance`, target_value, action
  `block|allow|alert`, enabled, note, audit cols), `device_control_assignments`
  (scope `global|department|employee|machine` + scope_id).
- DeviceAuth: `POST /api/v1/hardware-ports/sync` (stamp `machine_id` server-side —
  *Machine-Scoped Data Rule*).
- JWT: `GET /hardware-ports` (inventory, infinite scroll), `GET /hardware-port-events`
  (events), CRUD `/device-control/rules` + `/device-control/assignments`,
  `POST /dlp/rules/publish` extended to compile a `deviceControl` section into the bundle,
  **resolving assignments for each device at pull time** so a PC only receives rules aimed at it.
- RBAC: submodule `hardware-port-events` already seeded; `device-specs` covers `/device-specs/ports`.

## 4. Policy bundle (`PolicyBundle.cs` + parser)

Add `deviceControl`:
```json
"deviceControl": {
  "blockClasses":   ["<ClassGuid>", "USBSTOR", "Net", "Ports", "Bluetooth"],
  "blockInstances": ["USB\\VID_xxxx&PID_yyyy\\..."],
  "alertClasses":   ["Display"],
  "allowInstances": ["<exempted device>"]
}
```
Mirrors `AllowApps` shape. Clear path when empty / `lockdown:false` / switch off.

## 5. DlpService — enforce (`client/DlpService/PortDeviceEnforcer.cs`, new)

Wired into `EnforceWorker.ApplyAsync` (15 s tick), gated by local switch
`ALPHA_DLP_PORT_BLOCK` (DlpConfig, default true; seeded via EnvBootstrap → service.env →
installer; reported in Health) — mirrors the four existing switches.

- **Class block** → `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions`
  `DenyDeviceClasses` + `DenyDeviceClassesRetroactive=1`.
- **Instance block** → `DenyDeviceIDs` + immediate `Disable-PnpDevice` / SetupAPI
  `DICS_DISABLE`.
- **NIC** → disable net adapter device.
- **Clear** → remove Deny* values + re-enable (like `LockdownNetworkEnforcer.Clear`).
- **Guards** → never disable boot/system disk, never the only keyboard/mouse; video-out = alert.
- Emits `blocked` / `blocked_failed` alerts + `AuditAsync("port_control_apply", …)`.

## 6. Web

- `/device-specs/ports` — per-machine inventory grouped by category, occupancy + health +
  policy state, with Block/Allow/Alert + scope picker (Fleet/Dept/Employee/Machine),
  auto-publish (like `/allowlists/apps`). Infinite scroll, URL-synced filters.
- `/hardware-port-events` — event/alert log (like DLP Alerts).

## 7. Phases (testable increments)

- **P0 (now)** — `portctl.ps1` standalone harness: scan + block + unblock + status on a real PC
  (proves the mechanism before product wiring).
- **P1** — Client collector + migration 065 + `hardware-ports/sync` + `/device-specs/ports`
  read-only inventory. (See real ports in the dashboard.)
- **P2** — `deviceControl` bundle + `PortDeviceEnforcer` + local switch + rules/assignments UI +
  alerts. (Block end-to-end, fleet + per-employee.)
- **P3** — audio/BT/COM/LPT classes; video-out + virtual-NIC + new-internal-disk detect/alert.

## 8. Installer parity

New env keys (`ALPHA_PORT_SCAN_ENABLED`, `ALPHA_DLP_PORT_BLOCK`) must be in `.env` before
`encrypt-config.sh`; seeded by `EnvBootstrap`; rebuild installer. Not "done" until verified
from an installed build.
