# Face preprocess v1 + AdaFace IR-50 pin

> Phase 1 engineering pin. **Commercial distribution of pretrained weights requires legal approval.**

## Recognition model (planned)

| Field | Value |
|-------|-------|
| `model_id` | `adaface-ir50` |
| Architecture | AdaFace IR-50 |
| Input | 112×112 RGB face crop (BGR conversion rules must match worker + agent) |
| Output | 512-D float32 embedding |
| Normalization | L2 (`normalization=l2`); compare with cosine = dot product |
| Runtime | ONNX Runtime: **Python FaceWorker** (server) + **.NET** desktop agent (later) |
| `preprocess_version` | `face-preprocess-v1` |
| Package status | **Not shipped** until counsel clears weights / training-data provenance |

## Preprocess contract (`face-preprocess-v1`)

1. Detect single face; reject 0 or 2+ faces.
2. Align using 5-point landmarks to 112×112 (similarity transform).
3. Color order and scale: convert to the exact layout expected by the pinned ONNX graph (document after export: typically `(pixel/255 - 0.5) / 0.5` per AdaFace reference `to_input`).
4. Run recognition ONNX → 512 floats → L2-normalize if the graph does not already emit unit vectors.
5. Store template with `model_id`, `model_version`, `preprocess_version`, `embedding_dims=512`.

## Licensing checklist (must complete before Phase 4 package publish)

- [ ] Confirm AdaFace GitHub commit / LICENSE (MIT for code).
- [ ] Confirm pretrained checkpoint provenance (WebFace* subset).
- [ ] Written counsel opinion on commercial redistribution of weights.
- [ ] If blocked: InsightFace commercial license **or** retrain on cleared data **or** licensed SDK.
- [ ] Detector/alignment weights separately cleared (do not ship InsightFace SCRFD packs without license).

## Parity harness (lab)

Server enrollment embeddings are produced by **Python face_worker** (`server/face_worker`). The Avalonia
agent will later run the **same ONNX graph** under .NET ONNX Runtime — keep a fixture harness that
asserts Python vs .NET embeddings match within tolerance on a fixed image set. Pin SHA-256 of the
approved ONNX file in `face_model_versions.package_sha256` before fleet distribution.
