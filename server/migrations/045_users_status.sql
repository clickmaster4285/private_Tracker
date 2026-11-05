-- 045_users_status.sql
-- Account status for web-dashboard logins: 'active' (default) or 'suspended'.
-- A suspended account keeps its row, role link and permissions; it simply cannot
-- sign in:
--   * AuthService.Login refuses to mint tokens (HTTP 403)
--   * AuthService.RefreshSession refuses to rotate the refresh cookie, so an
--     already-issued 30-day session cannot outlive the suspension
--   * AuthHandler.CheckAuth reports authenticated=false and clears the cookies
-- Every existing row is backfilled to 'active' by the DEFAULT, so all current
-- accounts keep working. Values are validated in UserService.Update; there is no
-- CHECK constraint (this mirrors users.tracking_status, which is also unconstrained).

ALTER TABLE users ADD COLUMN IF NOT EXISTS status VARCHAR(20) NOT NULL DEFAULT 'active';
