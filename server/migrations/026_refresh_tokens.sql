-- 026_refresh_tokens.sql
-- Web-admin refresh tokens.
--
-- The access JWT cookie (auth_token) is short-lived (JWT_ACCESS_EXPIRY, default 15m).
-- A second httpOnly cookie (refresh_token) carries an opaque 32-byte token whose
-- SHA-256 hash lives here; POST /api/v1/auth/refresh validates + ROTATES it
-- (revoke old row, insert a new one) and re-mints both cookies.
-- When the refresh row is expired/revoked/deleted the web client force-redirects to /login.
--
-- Desktop clients are unaffected: their employee-login JWT keeps its own TTL
-- (JWT_EMPLOYEE_ACCESS_EXPIRY) and is sent in sync request bodies, never as a cookie.
--
-- Retention: revoked/expired rows accumulate slowly (one per login/rotation);
-- they are kept for audit until purged manually or by a future retention rule.

CREATE TABLE IF NOT EXISTS refresh_tokens (
    id          BIGSERIAL PRIMARY KEY,
    user_id     UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    token_hash  VARCHAR(64) NOT NULL UNIQUE,   -- hex(sha256(raw_token))
    expires_at  TIMESTAMPTZ NOT NULL,
    revoked_at  TIMESTAMPTZ,                   -- set when rotated or logged out
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at  TIMESTAMPTZ                    -- soft-delete convention
);

CREATE INDEX IF NOT EXISTS idx_refresh_tokens_user_id ON refresh_tokens(user_id);
CREATE INDEX IF NOT EXISTS idx_refresh_tokens_expires_at ON refresh_tokens(expires_at);
