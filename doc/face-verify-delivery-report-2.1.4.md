# Face verify locker delivery report — client 2.1.4

Statuses: `IMPLEMENTED AND TESTED` | `IMPLEMENTED BUT NOT FULLY TESTED` | `DESIGNED BUT NOT IMPLEMENTED` | `NOT IMPLEMENTED` | `BLOCKED`

## 1. Architecture before

Avalonia `--face-verify` soft UI; UI-thread ONNX/camera; single-frame match; first-mode events could reach server; plaintext templates; no supervisor; no PAD.

## 2. Architecture after

WinForms soft-locker (`alpha-ai-tracker-face-verification`) on `net10.0-windows`; zero-network locker session; first-local client-only label; 3-frame + motion PAD; DPAPI templates; supervisor relaunch; tracker warmup syncs simple events only. Soft-lock ≠ Windows auth.

## 3. Files created

- `doc/face-verify-locker-architecture.md`
- `doc/face-verify-far-protocol.md`
- `doc/face-verify-delivery-report-2.1.4.md`
- `client/Services/FaceVerify/FaceVerifyController.cs`
- `client/Services/FaceVerify/FaceVerifySupervisor.cs`
- `client/Services/FaceVerify/FaceDpapi.cs`
- `client/Services/FaceVerify/WinForms/FaceVerifyLockerHost.cs`
- `client/client.Tests/FaceFirstLocalIsolationTests.cs`

## 4. Files modified (high level)

- `Program.cs` — WinForms entry, no Avalonia for `--face-verify`
- `FaceRecognitionEngine.cs` — streak, spacing, motion PAD, multi-face reject
- `FaceBiometricStore.cs` — label/integrity, DPAPI blobs, first-mode enqueue blocked
- `FaceVerifyLauncher.cs` / `FaceWarmupService.cs` / `FacePaths.cs` / `FaceVerifySession.cs`
- `AppConfig` / `.env` / `.env.example` — threshold 0.85
- `client.csproj` — multi-TFM `net10.0` + `net10.0-windows`
- `publish/build-installer.sh` — per-RID TFM
- `AGENTS.md`, `doc/face-attendance-plan.md`
- `VERSION` → **2.1.4**

## 5. Database migrations

Local SQLite additive columns only: `first_local_test_label`, `first_local_integrity`. No Postgres migration.

## 6. Network isolation

Locker session writes empty `DeviceToken`/`ApiBase`. Controller never constructs `FaceApiClient`.  
**Status:** IMPLEMENTED BUT NOT FULLY TESTED (runtime socket audit not executed in CI).

## 7. First-local event isolation

`EnqueueVerifyEventAsync` no-ops for `mode=first`; drain query excludes `first`.  
**Status:** IMPLEMENTED AND TESTED (`FaceFirstLocalIsolationTests`).

## 8. Biometric matching

Threshold default 0.85; first +0.05; 3 consecutive frames; min spacing; motion PAD `pad_static`.  
**Status:** IMPLEMENTED BUT NOT FULLY TESTED (no FAR/FRR lab run).

## 9. Liveness / PAD

Active RGB motion accumulation across streak — **not** government-grade; print/replay not fully defeated.  
**Status:** IMPLEMENTED BUT NOT FULLY TESTED.

## 10. Process supervision

`FaceVerifySupervisor` relaunches until `last-success.json` within 2 minutes (max 8). Soft-lock only.  
**Status:** IMPLEMENTED BUT NOT FULLY TESTED.

## 11. Windows Credential Provider

**Status:** DESIGNED BUT NOT IMPLEMENTED (see architecture doc §6).

## 12. Security limitations

- Soft-lock bypass via admin / CAD / kill before supervisor cycle.
- DPAPI CurrentUser only; same-user malware can still access.
- Thresholds unvalidated for production FAR policy.
- Avalonia `FaceVerifyWindow` still in tree but unused by `--face-verify` entry.

## 13. Build results

- `dotnet build -f net10.0-windows` — succeeded  
- `dotnet build -f net10.0` — succeeded  

## 14. Test results

- `FaceFirstLocalIsolationTests` — **Passed**  
- `FaceAlignParityTests` — **Passed** (python↔dotnet ≥ 0.95)  
- Live webcam PAD / impostor FAR — **NOT** executed this delivery.

## 15. Remaining production blockers

1. Credential Provider for true Windows auth boundary.  
2. FAR/FRR validation on representative dataset.  
3. Stronger PAD (IR/depth or validated passive model).  
4. Runtime proof locker opens zero sockets.  
5. Rebuild Windows installer + re-bake `config.enc` with 0.85.  
