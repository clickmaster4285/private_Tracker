# Face verify FAR / FRR lab protocol (stub)

> **Status:** Protocol stub only — **do not invent numeric FAR/FRR**.  
> Threshold `ALPHA_FACE_MATCH_THRESHOLD=0.85` is an **initial proposal**.

## Metrics to record

| Metric | Definition |
|--------|------------|
| FAR | Impostor accepted / impostor attempts |
| FRR | Genuine rejected / genuine attempts |
| EER | Operating point where FAR ≈ FRR |
| FTA | Failure to acquire (no face / quality) |

## Required conditions

- Same AdaFace ONNX SHA as face_worker
- Same preprocess (LMEDS, RGB scale, L2)
- 3-frame streak + motion PAD enabled
- Lawfully collected enrollment + probe sets
- Lighting / camera / pose / presentation-attack subsets

## Runbook

1. Enroll N employees (multi-pose) via web → worker.
2. Warmup client templates.
3. Run genuine probes (live) and impostor probes (other employees + print/phone replay).
4. Log scores, decisions, PAD outcomes to a CSV (lab tooling TBD).
5. Choose threshold only after measured FAR/FRR meet the deployment policy.
