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

```powershell
cd server\face_worker
.\.venv\Scripts\Activate.ps1
python worker.py
```

Health: `GET http://127.0.0.1:8010/healthz`

## Flow

1. Admin uploads → Go stores files + `pending` job → `POST {FACE_WORKER_URL}/v1/process`
2. Worker `POST /api/v1/internal/face-worker/jobs/:id/lease` → YuNet per sample → result/complete
