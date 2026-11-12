# face_worker deploy notes

Implementation: **[`server/face_worker`](../../face_worker/)** — Python HTTP on **`:8010`** (push from Go API).

```bash
cd server/face_worker
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
python worker.py   # listens FACE_WORKER_LISTEN; reads server/.env
```

Go API needs `FACE_WORKER_URL=http://127.0.0.1:8010` and matching `FACE_WORKER_API_KEY`.
