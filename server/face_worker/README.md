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

## Run

Loads `server/.env` automatically.

| Key | Purpose |
|-----|---------|
| `FACE_WORKER_API_KEY` | Shared secret (API ↔ worker) |
| `FACE_WORKER_LISTEN` | Bind address (default `:8010`) |
| `FACE_WORKER_URL` | Set on **Go API** side — where to push (default `http://127.0.0.1:8010`) |
| `SERVER_HOST` / `SERVER_PORT` | Used by worker to call back into the API |
| `FACE_ADAFACE_ONNX` | Path to AdaFace IR-50 ONNX (or place `models/adaface_ir50_webface4m.onnx`) |
| `FACE_ADAFACE_MODEL_VERSION` | Stored with embeddings (default `ir50-webface4m-lab`) |
| `FACE_EMBEDDING_KEY` | Set on **Go API** — AES-GCM for `employee_face_embeddings` |

```powershell
cd server\face_worker
.\.venv\Scripts\Activate.ps1
python worker.py
```

Health: `GET http://127.0.0.1:8010/healthz` → `adaface: true|false`

## Flow

1. Admin uploads → Go stores files + `pending` job → `POST {FACE_WORKER_URL}/v1/process`
2. Worker leases job → YuNet (exactly one face) → optional AdaFace align+embed
3. With ONNX: `POST …/jobs/:id/embeddings` → API encrypts → `employee_face_embeddings` → profile `active`
4. Without ONNX: job `succeeded` + `embeddings_deferred` (face-check only)

## AdaFace ONNX (required to fill embeddings)

Place a counsel-cleared AdaFace IR-50 ONNX at:

`server/face_worker/models/adaface_ir50_webface4m.onnx`

or set `FACE_ADAFACE_ONNX` to its path, then `pip install onnxruntime` and restart `python worker.py`.

DeviceAuth download (client sync): `GET /api/v1/face-profiles/me/embeddings`
