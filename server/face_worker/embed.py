"""InsightFace buffalo_l embedder (SCRFD det_10g + w600k_r50).

face-preprocess-v2-buffalo-l: SCRFD 5-pt → LMEDS align 112 → ArcFace (x-127.5)/127.5 → L2.
"""

from __future__ import annotations

import hashlib
import os
from pathlib import Path
from typing import Any

import cv2
import numpy as np

# ArcFace 5-point template for 112×112 (left_eye, right_eye, nose, left_mouth, right_mouth).
ARC_FACE_5PTS = np.array(
    [
        [38.2946, 51.6963],
        [73.5318, 51.5014],
        [56.0252, 71.7366],
        [41.5493, 92.3655],
        [70.7299, 92.2041],
    ],
    dtype=np.float32,
)

WORKER_DIR = Path(__file__).resolve().parent
MODELS_DIR = WORKER_DIR / "models"
DET_FILENAME = "det_10g.onnx"
REC_FILENAME = "w600k_r50.onnx"
MODEL_ID = "buffalo-l-w600k-r50"
PREPROCESS_VERSION = "face-preprocess-v2-buffalo-l"


def env(name: str, default: str = "") -> str:
    return (os.environ.get(name) or default).strip()


def ort_thread_count() -> int:
    raw = env("FACE_WORKER_ORT_THREADS")
    if raw:
        try:
            n = int(raw)
            if n > 0:
                return n
        except ValueError:
            pass
    return max(1, os.cpu_count() or 4)


def make_ort_session(onnx_path: Path):
    import onnxruntime as ort

    opts = ort.SessionOptions()
    opts.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    threads = ort_thread_count()
    opts.intra_op_num_threads = threads
    opts.inter_op_num_threads = threads
    return ort.InferenceSession(
        str(onnx_path),
        sess_options=opts,
        providers=["CPUExecutionProvider"],
    )


def _candidate_paths(raw: str, filename: str) -> list[Path]:
    """Resolve env-relative paths against face_worker/ and server/ (shared .env)."""
    out: list[Path] = []
    seen: set[str] = set()

    def add(p: Path) -> None:
        try:
            key = str(p.resolve()) if p.exists() else str(p)
        except OSError:
            key = str(p)
        if key in seen:
            return
        seen.add(key)
        out.append(p)

    if raw:
        p = Path(raw.strip())
        if p.is_absolute():
            add(p)
        else:
            add((WORKER_DIR / p))
            add((WORKER_DIR.parent / p))  # server/.env often uses face_worker/models/...
            add(MODELS_DIR / p.name)
    buffalo_dir = env("FACE_BUFFALO_DIR")
    if buffalo_dir:
        bd = Path(buffalo_dir)
        if not bd.is_absolute():
            add(WORKER_DIR / bd / filename)
            add(WORKER_DIR.parent / bd / filename)
        else:
            add(bd / filename)
    add(MODELS_DIR / filename)
    return out


def resolve_det_path() -> Path | None:
    for p in _candidate_paths(env("FACE_BUFFALO_DET_ONNX"), DET_FILENAME):
        if p.is_file() and p.stat().st_size > 100_000:
            return p.resolve()
    return None


def resolve_rec_path() -> Path | None:
    for p in _candidate_paths(env("FACE_BUFFALO_REC_ONNX"), REC_FILENAME):
        if p.is_file() and p.stat().st_size > 1_000_000:
            return p.resolve()
    return None


# Back-compat alias used by older scripts.
def resolve_onnx_path() -> Path | None:
    return resolve_rec_path()


def model_meta(rec_path: Path) -> dict[str, str]:
    sha = hashlib.sha256(rec_path.read_bytes()).hexdigest()
    version = env("FACE_BUFFALO_MODEL_VERSION") or "buffalo-l-w600k-r50"
    return {
        "modelId": env("FACE_BUFFALO_MODEL_ID") or MODEL_ID,
        "modelVersion": version,
        "preprocessVersion": PREPROCESS_VERSION,
        "packageSha256": sha,
        "packageRelPath": str(rec_path),
    }


def align_face_112_from_kps(bgr: np.ndarray, kps5: np.ndarray) -> np.ndarray | None:
    """kps5: (5,2) ArcFace order — left_eye, right_eye, nose, left_mouth, right_mouth."""
    pts = np.asarray(kps5, dtype=np.float32).reshape(5, 2)
    if not np.isfinite(pts).all():
        return None
    m, _ = cv2.estimateAffinePartial2D(pts, ARC_FACE_5PTS, method=cv2.LMEDS)
    if m is None:
        return None
    return cv2.warpAffine(bgr, m, (112, 112), flags=cv2.INTER_LINEAR, borderValue=0.0)


def preprocess_buffalo(face_bgr_112: np.ndarray) -> np.ndarray:
    """BGR 112×112 → NCHW float32 InsightFace ArcFace: (x - 127.5) / 127.5 on RGB."""
    rgb = cv2.cvtColor(face_bgr_112, cv2.COLOR_BGR2RGB).astype(np.float32)
    rgb = (rgb - 127.5) / 127.5
    chw = np.transpose(rgb, (2, 0, 1))
    return np.expand_dims(chw, axis=0).astype(np.float32)


def l2_normalize(vec: np.ndarray) -> np.ndarray:
    n = float(np.linalg.norm(vec))
    if n < 1e-12:
        return vec.astype(np.float32)
    return (vec / n).astype(np.float32)


def cosine_similarity(a: list[float] | np.ndarray, b: list[float] | np.ndarray) -> float:
    va = np.asarray(a, dtype=np.float32).reshape(-1)
    vb = np.asarray(b, dtype=np.float32).reshape(-1)
    if va.shape[0] == 0 or vb.shape[0] == 0 or va.shape[0] != vb.shape[0]:
        return 0.0
    return float(np.dot(va, vb))


def pose_to_front_min_cosine() -> float:
    try:
        return float(env("FACE_POSE_TO_FRONT_MIN_COSINE") or "0.30")
    except ValueError:
        return 0.30


def apply_pose_to_front_gate(
    embedding_items: list[dict[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    min_cos = pose_to_front_min_cosine()
    front_vec: list[float] | None = None
    for item in embedding_items:
        pose = (item.get("poseId") or "").strip().lower()
        if pose == "front":
            vec = item.get("vector")
            if isinstance(vec, list) and len(vec) > 0:
                front_vec = vec
                break
    if front_vec is None:
        return list(embedding_items), []

    kept: list[dict[str, Any]] = []
    rejected: list[dict[str, Any]] = []
    for item in embedding_items:
        pose = (item.get("poseId") or "").strip().lower()
        if pose in ("", "front", "front_return"):
            kept.append(item)
            continue
        vec = item.get("vector")
        if not isinstance(vec, list) or len(vec) == 0:
            rejected.append({**item, "rejectReason": "pose_too_extreme", "cosineToFront": 0.0})
            continue
        cos = cosine_similarity(front_vec, vec)
        if cos < min_cos:
            rejected.append({**item, "rejectReason": "pose_too_extreme", "cosineToFront": cos})
            continue
        kept.append(item)
    return kept, rejected


class DetectedFace:
    __slots__ = ("x", "y", "w", "h", "kps", "score")

    def __init__(
        self,
        x: float,
        y: float,
        w: float,
        h: float,
        kps: np.ndarray,
        score: float,
    ) -> None:
        self.x = x
        self.y = y
        self.w = w
        self.h = h
        self.kps = np.asarray(kps, dtype=np.float32).reshape(5, 2)
        self.score = score


def quality_from_face(face: DetectedFace, img_w: int, img_h: int) -> float:
    area = (face.w * face.h) / max(float(img_w * img_h), 1.0)
    return float(max(0.0, min(1.0, 0.5 * face.score + 0.5 * min(area * 8.0, 1.0))))


# Org policy: sit ~50–70 cm from the webcam. We approximate distance by face-height
# fraction of the frame (typical laptop / monitor webcam FOV).
DEFAULT_DISTANCE_MIN_FRAC = 0.25  # below → too far (~>70 cm)
DEFAULT_DISTANCE_MAX_FRAC = 0.48  # above → too close (~<50 cm)


def distance_frac_bounds() -> tuple[float, float]:
    try:
        lo = float(env("FACE_DISTANCE_MIN_FRAC") or str(DEFAULT_DISTANCE_MIN_FRAC))
    except ValueError:
        lo = DEFAULT_DISTANCE_MIN_FRAC
    try:
        hi = float(env("FACE_DISTANCE_MAX_FRAC") or str(DEFAULT_DISTANCE_MAX_FRAC))
    except ValueError:
        hi = DEFAULT_DISTANCE_MAX_FRAC
    lo = max(0.05, min(lo, 0.9))
    hi = max(lo + 0.05, min(hi, 0.95))
    return lo, hi


def check_face_distance(face: DetectedFace, img_h: int) -> str | None:
    """Return reject reason `too_far` / `too_close`, or None if within 50–70 cm band."""
    if img_h <= 0:
        return "too_far"
    frac = float(face.h) / float(img_h)
    lo, hi = distance_frac_bounds()
    if frac < lo:
        return "too_far"
    if frac > hi:
        return "too_close"
    return None


class ScrfdDetector:
    """InsightFace SCRFD (det_10g.onnx) — 5-point landmarks + bbox."""

    def __init__(self, onnx_path: Path, det_thresh: float = 0.5, nms_thresh: float = 0.4) -> None:
        self.path = onnx_path
        self.det_thresh = det_thresh
        self.nms_thresh = nms_thresh
        self.session = make_ort_session(onnx_path)
        self.input_name = self.session.get_inputs()[0].name
        self.output_names = [o.name for o in self.session.get_outputs()]
        self.fmc = 3
        self.feat_stride_fpn = [8, 16, 32]
        self.num_anchors = 2
        self.use_kps = len(self.output_names) == 9
        self.input_mean = 127.5
        self.input_std = 128.0
        self._center_cache: dict[tuple[int, int, int], np.ndarray] = {}

    def detect(self, bgr: np.ndarray, input_size: tuple[int, int] = (640, 640)) -> list[DetectedFace]:
        if bgr is None or bgr.size == 0:
            return []
        img_h, img_w = bgr.shape[:2]
        if img_h < 16 or img_w < 16:
            return []

        det_w, det_h = input_size
        im_ratio = float(img_h) / float(img_w)
        model_ratio = float(det_h) / float(det_w)
        if im_ratio > model_ratio:
            new_height = det_h
            new_width = int(new_height / im_ratio)
        else:
            new_width = det_w
            new_height = int(new_width * im_ratio)
        det_scale = float(new_height) / float(img_h)
        resized = cv2.resize(bgr, (new_width, new_height))
        det_img = np.zeros((det_h, det_w, 3), dtype=np.uint8)
        det_img[:new_height, :new_width, :] = resized

        blob = cv2.dnn.blobFromImage(
            det_img,
            scalefactor=1.0 / self.input_std,
            size=(det_w, det_h),
            mean=(self.input_mean, self.input_mean, self.input_mean),
            swapRB=True,
        )
        net_outs = self.session.run(self.output_names, {self.input_name: blob})

        scores_list: list[np.ndarray] = []
        bboxes_list: list[np.ndarray] = []
        kpss_list: list[np.ndarray] = []
        for idx, stride in enumerate(self.feat_stride_fpn):
            scores = np.asarray(net_outs[idx]).reshape(-1)
            bbox_preds = np.asarray(net_outs[idx + self.fmc]).reshape((-1, 4)) * stride
            kps_preds = None
            if self.use_kps:
                kps_preds = np.asarray(net_outs[idx + self.fmc * 2]).reshape((-1, 10)) * stride

            height = det_h // stride
            width = det_w // stride
            key = (height, width, stride)
            if key in self._center_cache:
                anchor_centers = self._center_cache[key]
            else:
                anchor_centers = np.stack(np.mgrid[:height, :width][::-1], axis=-1).astype(np.float32)
                anchor_centers = (anchor_centers * stride).reshape((-1, 2))
                if self.num_anchors > 1:
                    anchor_centers = np.stack([anchor_centers] * self.num_anchors, axis=1).reshape((-1, 2))
                self._center_cache[key] = anchor_centers

            pos_inds = np.where(scores >= self.det_thresh)[0]
            bboxes = distance2bbox(anchor_centers, bbox_preds)
            scores_list.append(scores[pos_inds])
            bboxes_list.append(bboxes[pos_inds])
            if self.use_kps and kps_preds is not None:
                kpss = distance2kps(anchor_centers, kps_preds)
                kpss = kpss.reshape((kpss.shape[0], -1, 2))
                kpss_list.append(kpss[pos_inds])

        if not scores_list:
            return []
        scores_np = np.concatenate(scores_list, axis=0)
        bboxes_np = np.concatenate(bboxes_list, axis=0)
        if self.use_kps and kpss_list:
            kpss_np = np.concatenate(kpss_list, axis=0)
        else:
            kpss_np = None

        order = scores_np.argsort()[::-1]
        scores_np = scores_np[order]
        bboxes_np = bboxes_np[order]
        if kpss_np is not None:
            kpss_np = kpss_np[order]

        pre_det = np.hstack((bboxes_np, scores_np[:, None])).astype(np.float32, copy=False)
        keep = nms(pre_det, self.nms_thresh)
        det = pre_det[keep, :]
        if kpss_np is not None:
            kpss_np = kpss_np[keep, :, :]

        # Scale back to original image coords.
        det[:, :4] /= det_scale
        if kpss_np is not None:
            kpss_np /= det_scale

        out: list[DetectedFace] = []
        for i in range(det.shape[0]):
            x1, y1, x2, y2, score = map(float, det[i])
            w = max(0.0, x2 - x1)
            h = max(0.0, y2 - y1)
            if kpss_np is not None:
                kps = kpss_np[i]
            else:
                # Fallback synthetic 5pts from box (should not happen with det_10g).
                cx, cy = x1 + w * 0.5, y1 + h * 0.5
                kps = np.array(
                    [
                        [cx - w * 0.2, cy - h * 0.15],
                        [cx + w * 0.2, cy - h * 0.15],
                        [cx, cy],
                        [cx - w * 0.15, cy + h * 0.2],
                        [cx + w * 0.15, cy + h * 0.2],
                    ],
                    dtype=np.float32,
                )
            out.append(DetectedFace(x1, y1, w, h, kps, score))
        return out


def distance2bbox(points: np.ndarray, distance: np.ndarray) -> np.ndarray:
    x1 = points[:, 0] - distance[:, 0]
    y1 = points[:, 1] - distance[:, 1]
    x2 = points[:, 0] + distance[:, 2]
    y2 = points[:, 1] + distance[:, 3]
    return np.stack([x1, y1, x2, y2], axis=-1)


def distance2kps(points: np.ndarray, distance: np.ndarray) -> np.ndarray:
    preds = []
    for i in range(0, distance.shape[1], 2):
        px = points[:, 0] + distance[:, i]
        py = points[:, 1] + distance[:, i + 1]
        preds.append(px)
        preds.append(py)
    return np.stack(preds, axis=-1)


def nms(dets: np.ndarray, thresh: float) -> list[int]:
    x1 = dets[:, 0]
    y1 = dets[:, 1]
    x2 = dets[:, 2]
    y2 = dets[:, 3]
    scores = dets[:, 4]
    areas = (x2 - x1 + 1) * (y2 - y1 + 1)
    order = scores.argsort()[::-1]
    keep: list[int] = []
    while order.size > 0:
        i = int(order[0])
        keep.append(i)
        xx1 = np.maximum(x1[i], x1[order[1:]])
        yy1 = np.maximum(y1[i], y1[order[1:]])
        xx2 = np.minimum(x2[i], x2[order[1:]])
        yy2 = np.minimum(y2[i], y2[order[1:]])
        w = np.maximum(0.0, xx2 - xx1 + 1)
        h = np.maximum(0.0, yy2 - yy1 + 1)
        inter = w * h
        ovr = inter / (areas[i] + areas[order[1:]] - inter)
        inds = np.where(ovr <= thresh)[0]
        order = order[inds + 1]
    return keep


class BuffaloEmbedder:
    def __init__(self, onnx_path: Path) -> None:
        self.path = onnx_path
        self.meta = model_meta(onnx_path)
        self.dims = int(env("FACE_BUFFALO_DIMS") or "512")
        self.session = make_ort_session(onnx_path)
        inputs = self.session.get_inputs()
        if not inputs:
            raise RuntimeError("buffalo_l recognition ONNX has no inputs")
        self.input_name = inputs[0].name
        outputs = self.session.get_outputs()
        if not outputs:
            raise RuntimeError("buffalo_l recognition ONNX has no outputs")
        self.output_name = outputs[0].name

    def embed_crop(self, face_bgr_112: np.ndarray) -> list[float]:
        tensor = preprocess_buffalo(face_bgr_112)
        out = self.session.run([self.output_name], {self.input_name: tensor})[0]
        vec = np.asarray(out, dtype=np.float32).reshape(-1)
        if vec.shape[0] != self.dims:
            raise RuntimeError(f"buffalo_l output dims {vec.shape[0]} != {self.dims}")
        return l2_normalize(vec).tolist()


# Back-compat alias
AdaFaceEmbedder = BuffaloEmbedder


def try_load_embedder() -> BuffaloEmbedder | None:
    path = resolve_rec_path()
    if path is None:
        return None
    return BuffaloEmbedder(path)


def try_load_detector(det_thresh: float = 0.5) -> ScrfdDetector | None:
    path = resolve_det_path()
    if path is None:
        return None
    return ScrfdDetector(path, det_thresh=det_thresh)


def detect_faces_detail(
    detector: ScrfdDetector, jpeg_bytes: bytes, min_face_px: int
) -> tuple[np.ndarray | None, list[DetectedFace]]:
    arr = np.frombuffer(jpeg_bytes, dtype=np.uint8)
    mat = cv2.imdecode(arr, cv2.IMREAD_COLOR)
    if mat is None or mat.size == 0:
        return None, []
    faces = detector.detect(mat)
    kept = [f for f in faces if max(f.w, f.h) >= min_face_px]
    return mat, kept


def embed_single_face(
    embedder: BuffaloEmbedder,
    bgr: np.ndarray,
    face: DetectedFace,
) -> tuple[list[float], float] | None:
    crop = align_face_112_from_kps(bgr, face.kps)
    if crop is None:
        return None
    h, w = bgr.shape[:2]
    quality = quality_from_face(face, w, h)
    vector = embedder.embed_crop(crop)
    return vector, quality
