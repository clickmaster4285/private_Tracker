# Face verify locker architecture (Windows)

> **Status:** Target design for government-critical soft-lock + local biometrics.  
> **Last updated:** 2026-11-14  
> **Classification:** Application soft-lock is **not** Windows Lock Screen, Secure Desktop, or a Credential Provider.

## 1. As-is (pre-redesign)

| Concern | Implementation |
|---------|----------------|
| Process | `client.exe --face-verify`, mutex `Global\{APP_MUTEX}-face-verify` |
| UI | Avalonia `FaceVerifyWindow` (TopMost); ONNX + camera often on UI thread |
| Match | YuNet → LMEDS → AdaFace → L2 cosine; **single frame** |
| First vs simple | `local_biometric_sync_state.first_verify_completed_at` |
| Network | Modal may POST verify-event; `FaceWarmupService` downloads models/templates and drains queue |
| Crypto | Plaintext `vector_blob`, JPEGs, `verify-session.json` (device token) |
| PAD | None |
| Supervisor | Mutex only; kill locker = soft-lock gone until next trigger |

## 2. Target architecture

```text
Tracker (network OK)                    Locker (zero network)
─────────────────────                   ─────────────────────
FaceWarmupService ──models/templates──► LocalAppData/face/
FaceVerifySupervisor ──spawn/monitor──► alpha-ai-tracker-face-verification
     ▲                                  WinForms soft-lock UI
     │                                  FaceRecognitionEngine (local)
Session unlock / boot / login           biometric.db (first_local label)
```

### Soft-lock limitations (explicit)

- Not Winlogon / Secure Desktop / Credential Provider.
- Not resistant to admin, kernel, or Ctrl+Alt+Delete.
- Process kill can remove the overlay until supervisor relaunches.
- **Windows authentication remains the real workstation boundary.**

### Fail-closed

Camera failure, ONNX failure, mismatch, PAD fail, crash → **do not** authenticate. Supervisor may relaunch soft-lock; never treat absence of UI as success.

## 3. State machines

### First-local (client-only)

1. Read `first_verify_completed_at` + `first_local_test_label`.
2. If incomplete → multi-angle wizard (front mandatory).
3. On success → atomic write label `first_local_test_done` + timestamp.
4. **Never** enqueue/POST first-mode events.

### Simple (after first-local done)

1. Front (or front_return) templates only when present.
2. Quality + temporal (3 consecutive frames) + PAD challenge.
3. On success → write local success marker for supervisor; optional **simple-mode** event enqueue for tracker drain.
4. Release soft-lock.

## 4. Failure matrix

| Failure | Behavior |
|---------|----------|
| Camera disconnected | Remain locked; retry / recover |
| ONNX init fail | Remain locked; show error + Retry |
| Face mismatch | Remain locked |
| Liveness/PAD fail | Remain locked |
| Locker crash | Supervisor relaunch (fail-closed) |
| DB corrupt / missing templates | Controlled recovery; not authorized |
| Network down | Local verify continues; warmup deferred |

## 5. Local biometric protection

- DPAPI `CurrentUser` protect template blobs (migrate plaintext → envelope).
- Strip device token from locker session handoff (locker needs no network).
- Restrict ACLs on `%LocalAppData%\AlphaAITracker\face` where practical.
- Integrity: completion label + timestamp; tamper → treat as incomplete / recovery.
- SqlCipher / TPM: future; documented only unless separately scoped.

## 6. Windows Credential Provider (feasibility)

**Status: DESIGNED BUT NOT IMPLEMENTED**

| Topic | Notes |
|-------|-------|
| Surface | COM `ICredentialProvider` / `ICredentialProviderCredential` under Winlogon Secure Desktop |
| Unlock | Serialize credentials via CredPack / auth package — **must not** store user passwords in Alpha |
| Biometrics | CP typically unlocks via Windows Hello or password; custom face would need careful design with Microsoft auth stack |
| Signing | Authenticode required for production CP DLLs |
| Install | Registry under `Credential Providers`; rollback must unregister cleanly |
| Risk | Fake CP / credential injection **forbidden** |

Until a real CP ships, Alpha soft-lock is **supplemental** only.

## 7. PAD / liveness

**RGB face match ≠ liveness.**

Ship: lightweight **active challenge** (pose/motion consistency across spaced frames) — status must remain honest under test. Passive MiniFASNet-class models are optional follow-on with validation.

## 8. FAR/FRR

Thresholds (`ALPHA_FACE_MATCH_THRESHOLD=0.85`, first +0.05) are **initial proposals**. Lab harness stubs record protocol; do not invent production FAR numbers.
