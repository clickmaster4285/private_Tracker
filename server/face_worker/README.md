# face_worker (server-side, Python)

HTTP service next to the Go API (**not** the Avalonia employee agent). Default listen **`:8010`**.

When an admin uploads / retries enrollment images, the **Go API pushes**:

`POST http://127.0.0.1:8010/v1/process` `{"jobId":"..."}` + `X-Face-Worker-Key`

There is **no 2-second poll loop**.

## Setup

```bash
cd server/face_worker
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

Place InsightFace **buffalo_l** ONNX under `models/` (commercial license required for production):

- `det_10g.onnx` — SCRFD detector
- `w600k_r50.onnx` — recognition (512-D)

## Run

Loads `server/.env` automatically.

| Key | Purpose |
|-----|---------|
| `FACE_WORKER_API_KEY` | Shared secret (API ↔ worker) |
| `FACE_WORKER_LISTEN` | Bind address (default `:8010`) |
| `FACE_WORKER_URL` | Set on **Go API** side — where to push (default `http://127.0.0.1:8010`) |
| `SERVER_HOST` / `SERVER_PORT` | Used by worker to call back into the API |
| `FACE_BUFFALO_DET_ONNX` | Path to SCRFD `det_10g.onnx` |
| `FACE_BUFFALO_REC_ONNX` | Path to `w600k_r50.onnx` |
| `FACE_BUFFALO_ZIP_URL` | Auto-download pack when models missing (default InsightFace `buffalo_l.zip`) |
| `FACE_BUFFALO_DET_DOWNLOAD_URL` | Optional single-file mirror for det |
| `FACE_BUFFALO_REC_DOWNLOAD_URL` | Optional single-file mirror for rec |
| `FACE_BUFFALO_MODEL_ID` | Default `buffalo-l-w600k-r50` |
| `FACE_WORKER_ORT_THREADS` | ORT intra/inter threads (default = CPU count) |
| `FACE_DISTANCE_MIN_FRAC` | Min face-height/frame-height (~70 cm / too far); default `0.25` |
| `FACE_DISTANCE_MAX_FRAC` | Max face-height/frame-height (~50 cm / too close); default `0.48` |
| `FACE_POSE_TO_FRONT_MIN_COSINE` | Reject non-front enroll poses below this cosine vs front (default `0.30`) |
| `FACE_EMBEDDING_KEY` | Set on **Go API** — AES-GCM for `employee_face_embeddings` |

Lab: `python parity_check.py` (Python embed); `dotnet test --filter FaceAlignParity` for Python↔.NET ≥ 0.95.

```powershell
cd server\face_worker
.\.venv\Scripts\Activate.ps1
python worker.py
```

Health: `GET http://127.0.0.1:8010/healthz` → `buffalo: true|false`

## Flow

1. Admin uploads → Go stores files + `pending` job → `POST {FACE_WORKER_URL}/v1/process`
2. Worker leases job → SCRFD (exactly one face) → optional buffalo_l align+embed
3. With ONNX: `POST …/jobs/:id/embeddings` → API encrypts → `employee_face_embeddings` → profile `active`
4. Without recognition ONNX: job `succeeded` + `embeddings_deferred` (face-check only)

**Cutover:** AdaFace embeddings are incompatible. After deploying buffalo_l, reprocess every enrollment job (or HR re-enroll) so Postgres holds `model_id=buffalo-l-w600k-r50` vectors.

## Models directory (`models/`)

On startup the worker runs `ensure_models()`:

1. If `det_10g.onnx` / `w600k_r50.onnx` are missing → download InsightFace **buffalo_l.zip**
   (override with `FACE_BUFFALO_ZIP_URL`) and extract both ONNX files into `models/`
2. Optional single-file URLs (`FACE_BUFFALO_*_DOWNLOAD_URL`) are tried first
3. **det_10g** is required to start; **w600k_r50** required for embeddings

The Go API serves the same files to Windows clients (no GitHub on the PC):

- `GET /api/v1/face-models/packages/buffalo-l-det`
- `GET /api/v1/face-models/packages/buffalo-l-rec`
