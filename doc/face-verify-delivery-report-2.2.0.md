# Face verify 2.2.0 — InsightFace buffalo_l cutover

## Summary

Windows soft-lock and server `face_worker` now use InsightFace **buffalo_l** (`det_10g` + `w600k_r50`) instead of YuNet + AdaFace. Match confidence floor is **≥ 0.85**. Soft-lock runs at **High** process priority with full ORT thread pools; tracker collectors/sync/stream/screenshots yield via `FaceVerifyYield`. Missing or stale local embeddings trigger a tracker re-fetch from the API.

## Ops

1. Place `det_10g.onnx` and `w600k_r50.onnx` under `server/face_worker/models/` (InsightFace commercial license for production).
2. Restart API + `python worker.py`.
3. **Reprocess every face enrollment job** (or HR re-enroll) — AdaFace vectors are not comparable to buffalo_l.
4. Rebuild Windows installer (client **2.2.0**); re-bake `config.enc` if threshold env changed.
5. On each PC: keep tracker online once so models + buffalo_l embeddings download into LocalAppData `face/`.

## Verify

- `GET :8010/healthz` → `buffalo: true`
- Soft-lock shows “Syncing face data…” then camera when embeddings arrive
- Cosine match only unlocks at ≥ 0.85 with 3-frame streak + motion PAD
