#!/usr/bin/env python3
"""
Server-side face_worker (NOT the Avalonia employee agent).

Runs an HTTP server (default :8010). The Go API pushes new enrollment jobs:
  POST /v1/process  {"jobId":"..."}  header X-Face-Worker-Key

Worker leases the job, validates each sample (YuNet), and when FACE_ADAFACE_ONNX
is configured runs AdaFace IR-50 → POST embeddings (AES-GCM stored by API).
Without the ONNX file, jobs still complete as succeeded/embeddings_deferred.
"""

from __future__ import annotations

import json
import os
import socket
import sys
import threading
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

import cv2
import requests

from embed import (
    AdaFaceEmbedder,
    detect_faces_detail,
    embed_single_face,
    try_load_embedder,
)

YUNET_URL = (
    "https://github.com/opencv/opencv_zoo/raw/main/models/"
    "face_detection_yunet/face_detection_yunet_2023mar.onnx"
)
YUNET_FILENAME = "face_detection_yunet_2023mar.onnx"
ADAFACE_FILENAME = "adaface_ir50_webface4m.onnx"
YUNET_SCORE_THRESHOLD = float(os.environ.get("FACE_WORKER_YUNET_SCORE", "0.5") or "0.5")
YUNET_NMS_THRESHOLD = 0.3
YUNET_TOP_K = 5000
YUNET_MIN_FACE_PX = int(os.environ.get("FACE_WORKER_MIN_FACE_PX", "60") or "60")

WORKER_DIR = Path(__file__).resolve().parent
MODELS_DIR = WORKER_DIR / "models"
SERVER_ROOT = WORKER_DIR.parent
SERVER_DOTENV = SERVER_ROOT / ".env"

# Filled in main()
API_BASE = ""
API_KEY = ""
WORKER_ID = ""
DETECTOR: cv2.FaceDetectorYN | None = None
EMBEDDER: AdaFaceEmbedder | None = None
HTTP: requests.Session | None = None
PROCESS_LOCK = threading.Lock()


def load_server_dotenv(path: Path = SERVER_DOTENV) -> Path | None:
    if not path.is_file():
        return None
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("export "):
            line = line[7:].strip()
        if "=" not in line:
            continue
        key, _, value = line.partition("=")
        key = key.strip()
        if not key or key in os.environ:
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        os.environ[key] = value
    return path


def env(name: str, default: str = "") -> str:
    return (os.environ.get(name) or default).strip()


def resolve_path(raw: str) -> Path | None:
    """Resolve a path from server/.env relative to server/ when not absolute."""
    value = (raw or "").strip()
    if not value:
        return None
    p = Path(value)
    if not p.is_absolute():
        p = (SERVER_ROOT / p).resolve()
    return p if p.is_file() else None


def resolve_server_url() -> str:
    explicit = env("ALPHA_SERVER_URL")
    if explicit:
        return explicit.rstrip("/")
    host = env("SERVER_HOST", "localhost") or "localhost"
    if host in ("0.0.0.0", "::", "[::]"):
        host = "localhost"
    port = env("SERVER_PORT", "8080") or "8080"
    scheme = "https" if env("TLS_CERT_FILE") and env("TLS_KEY_FILE") else "http"
    return f"{scheme}://{host}:{port}"


def resolve_tls_verify() -> bool | str:
    """Trust mkcert/local CA when TLS_CA_FILE or REQUESTS_CA_BUNDLE points at a PEM."""
    for key in ("TLS_CA_FILE", "REQUESTS_CA_BUNDLE"):
        found = resolve_path(env(key))
        if found is not None:
            return str(found)
    return True


def resolve_listen() -> tuple[str, int]:
    """FACE_WORKER_LISTEN=:8010 or 0.0.0.0:8010 → (host, port)."""
    raw = env("FACE_WORKER_LISTEN", ":8010") or ":8010"
    if raw.startswith(":"):
        return "0.0.0.0", int(raw[1:] or "8010")
    parsed = urlparse("http://" + raw if "://" not in raw else raw)
    host = parsed.hostname or "0.0.0.0"
    port = parsed.port or 8010
    return host, port


def _download_to(path: Path, url: str, label: str) -> None:
    print(f"[FaceWorker] downloading {label}…", flush=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    try:
        with urllib.request.urlopen(url, timeout=600) as resp:
            data = resp.read()
        if not data:
            raise RuntimeError(f"empty download for {label}")
        tmp.write_bytes(data)
        tmp.replace(path)
        print(f"[FaceWorker] {label} → {path} ({path.stat().st_size} bytes)", flush=True)
    finally:
        if tmp.exists():
            try:
                tmp.unlink()
            except OSError:
                pass


def ensure_yunet_model() -> Path:
    """Ensure YuNet ONNX under models/. Auto-download from OpenCV zoo if missing."""
    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    path = MODELS_DIR / YUNET_FILENAME
    if path.exists() and path.stat().st_size > 100_000:
        return path
    url = env("FACE_YUNET_DOWNLOAD_URL") or YUNET_URL
    _download_to(path, url, "YuNet")
    if not path.exists() or path.stat().st_size <= 100_000:
        raise RuntimeError(f"YuNet download failed or file too small: {path}")
    return path


def ensure_adaface_model() -> Path | None:
    """Ensure AdaFace ONNX under models/ when possible.

    Prefer existing FACE_ADAFACE_ONNX / models/adaface_*.onnx.
    If missing and FACE_ADAFACE_DOWNLOAD_URL is set, download into models/.
    Returns path when present; None when still missing (embeddings_deferred).
    """
    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    from embed import resolve_onnx_path  # local import after dotenv load

    existing = resolve_onnx_path()
    if existing is not None:
        return existing

    url = env("FACE_ADAFACE_DOWNLOAD_URL")
    if not url:
        return None

    dest = MODELS_DIR / ADAFACE_FILENAME
    _download_to(dest, url, "AdaFace")
    if dest.exists() and dest.stat().st_size > 1_000_000:
        return dest
    return None


def ensure_models() -> Path:
    """Startup: check models/ for YuNet (+ AdaFace); download missing when URLs allow."""
    MODELS_DIR.mkdir(parents=True, exist_ok=True)
    yunet = ensure_yunet_model()
    ada = ensure_adaface_model()
    print(f"[FaceWorker] models dir={MODELS_DIR}", flush=True)
    print(f"[FaceWorker]   YuNet   = {yunet.name} ({yunet.stat().st_size} bytes)", flush=True)
    if ada is not None:
        print(f"[FaceWorker]   AdaFace = {ada.name} ({ada.stat().st_size} bytes)", flush=True)
    else:
        print(
            "[FaceWorker]   AdaFace = MISSING — place "
            f"{ADAFACE_FILENAME} under models/ or set FACE_ADAFACE_ONNX / "
            "FACE_ADAFACE_DOWNLOAD_URL",
            flush=True,
        )
    return yunet


def create_yunet_detector(model_path: Path) -> cv2.FaceDetectorYN:
    if not hasattr(cv2, "FaceDetectorYN_create"):
        raise RuntimeError("OpenCV FaceDetectorYN missing — upgrade opencv-python-headless")
    return cv2.FaceDetectorYN_create(
        str(model_path),
        "",
        (320, 320),
        YUNET_SCORE_THRESHOLD,
        YUNET_NMS_THRESHOLD,
        YUNET_TOP_K,
    )


def report(job_id: str, sample_id: str, status: str, reject_reason: str, face_count: int) -> None:
    assert HTTP is not None
    HTTP.post(
        f"{API_BASE}/api/v1/internal/face-worker/jobs/{job_id}/samples/{sample_id}/result",
        json={"status": status, "rejectReason": reject_reason, "faceCount": face_count},
        timeout=60,
    )


def complete(job_id: str, status: str, code: str, message: str) -> None:
    assert HTTP is not None
    HTTP.post(
        f"{API_BASE}/api/v1/internal/face-worker/jobs/{job_id}/complete",
        json={"status": status, "code": code, "message": message},
        timeout=60,
    )


def submit_embeddings(job_id: str, payload: dict[str, Any]) -> None:
    assert HTTP is not None
    resp = HTTP.post(
        f"{API_BASE}/api/v1/internal/face-worker/jobs/{job_id}/embeddings",
        json=payload,
        timeout=120,
    )
    if resp.status_code >= 300:
        raise RuntimeError(f"embeddings HTTP {resp.status_code}: {resp.text[:400]}")


def process_job(job_id: str) -> dict[str, Any]:
    assert HTTP is not None and DETECTOR is not None
    with PROCESS_LOCK:
        lease = HTTP.post(
            f"{API_BASE}/api/v1/internal/face-worker/jobs/{job_id}/lease",
            timeout=60,
        )
        if lease.status_code == 409:
            return {"ok": False, "error": "job_not_claimable", "jobId": job_id}
        lease.raise_for_status()
        payload = lease.json()
        samples = payload.get("samples")
        if not isinstance(samples, list):
            complete(job_id, "failed", "no_samples", "Job has no samples")
            return {"ok": False, "error": "no_samples", "jobId": job_id}

        print(
            f"[FaceWorker] processing job {job_id} samples={len(samples)} "
            f"adaface={'on' if EMBEDDER else 'off'}",
            flush=True,
        )

        rejected = 0
        validated = 0
        reject_notes: list[str] = []
        embedding_items: list[dict[str, Any]] = []

        for sample in samples:
            sample_id = sample.get("id") or ""
            pose_id = sample.get("poseId") or ""
            client_id = sample.get("clientSampleId") or ""
            process_status = sample.get("processStatus") or ""

            if process_status in ("validated", "embedded") and EMBEDDER is None:
                validated += 1
                print(
                    f"[FaceWorker] sample {sample_id} pose={pose_id} already {process_status} — skip",
                    flush=True,
                )
                continue

            report(job_id, sample_id, "processing", "", 0)
            img_resp = HTTP.get(
                f"{API_BASE}/api/v1/internal/face-worker/samples/{sample_id}/image",
                timeout=120,
            )
            img_resp.raise_for_status()
            bgr, faces = detect_faces_detail(DETECTOR, img_resp.content, YUNET_MIN_FACE_PX)
            face_count = len(faces)
            print(
                f"[FaceWorker] sample {sample_id} pose={pose_id} faces={face_count}",
                flush=True,
            )

            label = pose_id or client_id or sample_id
            if bgr is None or face_count == 0:
                rejected += 1
                reject_notes.append(f"{label}: no face")
                report(job_id, sample_id, "rejected", "no_face", face_count)
                continue
            if face_count > 1:
                rejected += 1
                reject_notes.append(f"{label}: multiple faces ({face_count})")
                report(job_id, sample_id, "rejected", "multiple_faces", face_count)
                continue

            if EMBEDDER is not None:
                result = embed_single_face(EMBEDDER, bgr, faces[0])
                if result is None:
                    rejected += 1
                    reject_notes.append(f"{label}: align_failed")
                    report(job_id, sample_id, "rejected", "align_failed", face_count)
                    continue
                vector, quality = result
                embedding_items.append(
                    {
                        "sampleId": sample_id,
                        "poseId": pose_id,
                        "qualityScore": quality,
                        "vector": vector,
                    }
                )
                validated += 1
                report(job_id, sample_id, "validated", "", face_count)
            else:
                validated += 1
                report(job_id, sample_id, "validated", "", face_count)

        if rejected > 0:
            msg = (
                f"Rejected {rejected} image(s): {'; '.join(reject_notes)}. "
                "Retake those angles (one face only, no background people)."
            )
            complete(job_id, "needs_more_samples", "sample_rejected", msg)
            print(
                f"[FaceWorker] job {job_id} needs_more_samples ({rejected} rejected, {validated} ok)",
                flush=True,
            )
            return {
                "ok": True,
                "jobId": job_id,
                "status": "needs_more_samples",
                "validated": validated,
                "rejected": rejected,
            }

        if EMBEDDER is None:
            complete(
                job_id,
                "succeeded",
                "embeddings_deferred",
                "All images passed single-face checks. "
                "Place AdaFace ONNX at FACE_ADAFACE_ONNX (or models/adaface_ir50_webface4m.onnx) "
                "and restart face_worker to persist embeddings.",
            )
            print(f"[FaceWorker] job {job_id} face-check OK; embeddings_deferred", flush=True)
            return {
                "ok": True,
                "jobId": job_id,
                "status": "succeeded",
                "validated": validated,
                "rejected": 0,
                "embeddings": "deferred",
            }

        if not embedding_items:
            complete(job_id, "failed", "no_embeddings", "No embeddings produced")
            return {"ok": False, "error": "no_embeddings", "jobId": job_id}

        meta = EMBEDDER.meta
        submit_embeddings(
            job_id,
            {
                "modelId": meta["modelId"],
                "modelVersion": meta["modelVersion"],
                "preprocessVersion": meta["preprocessVersion"],
                "embeddingDims": EMBEDDER.dims,
                "packageSha256": meta["packageSha256"],
                "packageRelPath": meta["packageRelPath"],
                "embeddings": embedding_items,
            },
        )
        print(
            f"[FaceWorker] job {job_id} stored {len(embedding_items)} embeddings; profile activated",
            flush=True,
        )
        return {
            "ok": True,
            "jobId": job_id,
            "status": "succeeded",
            "validated": validated,
            "rejected": 0,
            "embeddings": len(embedding_items),
        }


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"[FaceWorker] {self.address_string()} {fmt % args}", flush=True)

    def _read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length") or "0")
        raw = self.rfile.read(length) if length > 0 else b"{}"
        if not raw:
            return {}
        data = json.loads(raw.decode("utf-8"))
        return data if isinstance(data, dict) else {}

    def _send(self, code: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _authorized(self) -> bool:
        got = (self.headers.get("X-Face-Worker-Key") or "").strip()
        return bool(API_KEY) and got == API_KEY

    def do_GET(self) -> None:  # noqa: N802
        path = urlparse(self.path).path.rstrip("/") or "/"
        if path in ("/healthz", "/v1/healthz", "/"):
            self._send(
                200,
                {
                    "ok": True,
                    "service": "face_worker",
                    "adaface": EMBEDDER is not None,
                    "modelVersion": EMBEDDER.meta["modelVersion"] if EMBEDDER else "",
                },
            )
            return
        self._send(404, {"ok": False, "error": "not_found"})

    def do_POST(self) -> None:  # noqa: N802
        path = urlparse(self.path).path.rstrip("/") or "/"
        if path != "/v1/process":
            self._send(404, {"ok": False, "error": "not_found"})
            return
        if not self._authorized():
            self._send(401, {"ok": False, "error": "unauthorized"})
            return
        try:
            body = self._read_json()
        except json.JSONDecodeError:
            self._send(400, {"ok": False, "error": "invalid_json"})
            return
        job_id = str(body.get("jobId") or body.get("job_id") or "").strip()
        if not job_id:
            self._send(400, {"ok": False, "error": "jobId_required"})
            return
        try:
            result = process_job(job_id)
            code = 200 if result.get("ok") else 409
            self._send(code, result)
        except Exception as ex:  # noqa: BLE001
            print(f"[FaceWorker] process error job={job_id}: {ex}", flush=True)
            try:
                complete(job_id, "failed", "worker_error", str(ex)[:500])
            except Exception:  # noqa: BLE001
                pass
            self._send(500, {"ok": False, "error": str(ex), "jobId": job_id})


def main() -> int:
    global API_BASE, API_KEY, WORKER_ID, DETECTOR, EMBEDDER, HTTP

    loaded = load_server_dotenv()
    if loaded:
        print(f"[FaceWorker] loaded env from {loaded}", flush=True)
    else:
        print(f"[FaceWorker] warning: {SERVER_DOTENV} not found — using process env only", flush=True)

    API_BASE = resolve_server_url()
    API_KEY = env("FACE_WORKER_API_KEY")
    WORKER_ID = env("FACE_WORKER_ID") or f"faceworker-{socket.gethostname()}"
    listen_host, listen_port = resolve_listen()

    if not API_KEY:
        print("FACE_WORKER_API_KEY is required (set it in server/.env)", file=sys.stderr)
        return 1

    try:
        model_path = ensure_models()
        DETECTOR = create_yunet_detector(model_path)
    except Exception as ex:  # noqa: BLE001
        print(f"Failed to init YuNet / models: {ex}", file=sys.stderr)
        return 1

    try:
        EMBEDDER = try_load_embedder()
    except Exception as ex:  # noqa: BLE001
        print(f"[FaceWorker] AdaFace init failed (continuing face-check only): {ex}", flush=True)
        EMBEDDER = None

    HTTP = requests.Session()
    HTTP.verify = resolve_tls_verify()
    if isinstance(HTTP.verify, str):
        print(f"[FaceWorker] TLS verify CA={HTTP.verify}", flush=True)
    HTTP.headers.update(
        {
            "X-Face-Worker-Key": API_KEY,
            "X-Face-Worker-Id": WORKER_ID,
            "Accept": "application/json",
        }
    )

    print(
        f"[FaceWorker] YuNet ready score>={YUNET_SCORE_THRESHOLD} minFacePx={YUNET_MIN_FACE_PX} model={model_path.name}",
        flush=True,
    )
    if EMBEDDER:
        print(
            f"[FaceWorker] AdaFace ready dims={EMBEDDER.dims} "
            f"version={EMBEDDER.meta['modelVersion']} path={EMBEDDER.path.name}",
            flush=True,
        )
    else:
        print(
            "[FaceWorker] AdaFace OFF — set FACE_ADAFACE_ONNX or place "
            "models/adaface_ir50_webface4m.onnx (embeddings_deferred until then)",
            flush=True,
        )
    print(
        f"[FaceWorker] listening http://{listen_host}:{listen_port}/v1/process "
        f"(API={API_BASE}, id={WORKER_ID}) — push mode, no poll",
        flush=True,
    )

    server = ThreadingHTTPServer((listen_host, listen_port), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[FaceWorker] shutting down", flush=True)
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
