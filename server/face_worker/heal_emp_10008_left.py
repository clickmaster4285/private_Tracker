#!/usr/bin/env python3
"""One-shot heal: mark EMP-10008 left / left_slight samples rejected and drop those embeddings.

Allows HR to retake via the enroll dialog (RetryEnrollment). Remaining poses stay on the
active profile so the PC can still verify front while waiting for retakes.

Loads DATABASE_URL from server/.env (never prints the URL).
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

WORKER_DIR = Path(__file__).resolve().parent
SERVER_ROOT = WORKER_DIR.parent
DOTENV = SERVER_ROOT / ".env"
EMPLOYEE_CODE = "EMP-10008"
BAD_POSES = ("left", "left_slight")


def load_dotenv(path: Path) -> None:
    if not path.is_file():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, _, v = line.partition("=")
        k = k.strip()
        v = v.strip().strip("'").strip('"')
        if k and k not in os.environ:
            os.environ[k] = v


def main() -> int:
    load_dotenv(DOTENV)
    dsn = (os.environ.get("DATABASE_URL") or os.environ.get("POSTGRES_DSN") or "").strip()
    if not dsn:
        host = (os.environ.get("DB_HOST") or "localhost").strip()
        port = (os.environ.get("DB_PORT") or "5432").strip()
        user = (os.environ.get("DB_USER") or "").strip()
        password = (os.environ.get("DB_PASSWORD") or "").strip()
        name = (os.environ.get("DB_NAME") or "").strip()
        sslmode = (os.environ.get("DB_SSLMODE") or "disable").strip()
        if user and name:
            from urllib.parse import quote_plus

            dsn = (
                f"postgresql://{quote_plus(user)}:{quote_plus(password)}"
                f"@{host}:{port}/{name}?sslmode={quote_plus(sslmode)}"
            )
    if not dsn:
        print("DATABASE_URL / DB_* missing in server/.env", file=sys.stderr)
        return 2

    try:
        import psycopg
    except ImportError:
        try:
            import psycopg2 as psycopg  # type: ignore
        except ImportError:
            print("Install psycopg: pip install 'psycopg[binary]'", file=sys.stderr)
            return 2

    conn = psycopg.connect(dsn)
    try:
        with conn.cursor() as cur:
            cur.execute(
                """
                SELECT s.id::text, s.pose_id, s.job_id::text, s.process_status
                FROM face_enrollment_samples s
                WHERE s.employee_id = %s AND s.pose_id = ANY(%s)
                ORDER BY s.created_at DESC
                """,
                (EMPLOYEE_CODE, list(BAD_POSES)),
            )
            samples = cur.fetchall()
            if not samples:
                print("No left/left_slight samples found — nothing to heal")
                return 0

            job_ids = sorted({r[2] for r in samples})
            sample_ids = [r[0] for r in samples]
            print(
                f"Rejecting {len(samples)} samples "
                f"poses={sorted({r[1] for r in samples})} jobs={len(job_ids)}"
            )

            cur.execute(
                """
                UPDATE face_enrollment_samples
                SET process_status = 'rejected',
                    reject_reason = 'pose_too_extreme'
                WHERE id = ANY(%s::uuid[])
                """,
                (sample_ids,),
            )

            cur.execute(
                """
                DELETE FROM employee_face_embeddings
                WHERE employee_id = %s AND pose_id = ANY(%s)
                """,
                (EMPLOYEE_CODE, list(BAD_POSES)),
            )
            deleted = cur.rowcount
            print(f"Deleted {deleted} embeddings for left/left_slight")

            for jid in job_ids:
                cur.execute(
                    """
                    UPDATE face_enrollment_jobs
                    SET status = 'needs_more_samples',
                        error_code = 'sample_rejected',
                        error_message = 'Retake left / left_slight — pose too extreme for AdaFace gate',
                        rejected_samples = (
                            SELECT COUNT(*) FROM face_enrollment_samples s
                            WHERE s.job_id = face_enrollment_jobs.id
                              AND s.process_status = 'rejected'
                        ),
                        finished_at = NOW()
                    WHERE id = %s::uuid
                    """,
                    (jid,),
                )

            cur.execute(
                """
                INSERT INTO face_enrollment_audit (employee_id, actor_user_id, action, metadata_json)
                VALUES (
                    %s, 'heal_script', 'pose_too_extreme_heal',
                    jsonb_build_object('poses', %s::text[], 'deletedEmbeddings', %s)
                )
                """,
                (EMPLOYEE_CODE, list(BAD_POSES), deleted),
            )

            conn.commit()
            print(
                "OK — HR: Employees → Enroll Face → retake left & slight left for EMP-10008. "
                "Client: ship VERSION 2.1.1+, re-sync face package "
                "(or wipe %%LocalAppData%%\\AlphaAITracker\\face)."
            )
            return 0
    finally:
        conn.close()


if __name__ == "__main__":
    raise SystemExit(main())
