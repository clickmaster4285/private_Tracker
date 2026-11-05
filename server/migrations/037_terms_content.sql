-- 037_terms_content.sql
-- Stores editable Terms & Conditions content per feature.
-- body is HTML — compatible with both web rich-text editor and Avalonia HTML rendering.
-- feature_id maps to client feature IDs (app_usage, browser_journey, etc.)
-- term_type: 'featured_based' (system-seeded, not deletable) or 'manual_based' (admin-created, deletable)
-- is_active: 0 = inactive, 1 = active

CREATE TABLE IF NOT EXISTS terms_content (
    id              VARCHAR(36) PRIMARY KEY DEFAULT gen_random_uuid()::text,
    heading         TEXT NOT NULL DEFAULT '',
    body            TEXT NOT NULL DEFAULT '',
    terms_version   VARCHAR(50) NOT NULL DEFAULT '1.0',
    is_system       BOOLEAN NOT NULL DEFAULT FALSE,
    feature_id      VARCHAR(100) NOT NULL DEFAULT '',
    term_type       VARCHAR(50) NOT NULL DEFAULT 'manual_based',
    is_active       INTEGER NOT NULL DEFAULT 1,
    sort_order      INTEGER NOT NULL DEFAULT 0,
    updated_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    deleted_at      TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS idx_terms_content_updated
    ON terms_content (updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_terms_content_feature_id
    ON terms_content (feature_id)
    WHERE deleted_at IS NULL;

-- Seed featured (system) content for each built-in feature
INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order)
VALUES
    ('tc-001', 'Application Usage Tracking',
     '<h3>What We Track</h3><p>Alpha AI Tracker monitors which desktop applications are open and actively used on your work machine. This includes the application name, process name, and the duration each application remains in focus.</p><h3>How This Data Is Used</h3><p>Application usage data is used to generate productivity reports and help managers understand how time is allocated across tools and workflows. It is not used for punitive purposes.</p><h3>Data Retention</h3><p>Application usage records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, session data is automatically purged.</p><h3>Your Rights</h3><p>This feature is required for the Alpha AI Tracker to function. You cannot revoke consent for application usage tracking while the tracker is active on your machine.</p>',
     '1.0', true, 'app_usage', 'featured_based', 1, 1),
    ('tc-002', 'Browser Journey Tracking',
     '<h3>What We Track</h3><p>When enabled, Alpha AI Tracker records the web pages you visit in supported browsers (Chrome, Firefox, Edge, Brave, Opera, and others). This includes the page URL, page title, and the time spent on each page.</p><h3>How This Data Is Used</h3><p>Browser journey data helps your organization understand which websites and web applications are used for work. It can identify training needs, blocked resources, or productivity patterns.</p><h3>Incognito / Private Browsing</h3><p>By default, incognito and private browsing windows are NOT tracked. If your organization enables incognito capture, a separate consent prompt will appear before any private browsing data is collected.</p><h3>Data Retention</h3><p>Browser journey records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, journey data is automatically purged.</p><h3>Your Rights</h3><p>You may revoke consent for browser journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization''s data retention policy.</p>',
     '1.0', true, 'browser_journey', 'featured_based', 1, 2),
    ('tc-003', 'File Explorer Journey Tracking',
     '<h3>What We Track</h3><p>When enabled, Alpha AI Tracker monitors file manager activity on your machine. This includes which folders you navigate to, and any files you create, rename, or delete through the file explorer.</p><h3>How This Data Is Used</h3><p>File journey data helps understand file organization patterns and collaboration workflows. It is used to generate insights about how your team manages documents and project files.</p><h3>What We Do NOT Track</h3><p>This feature does not read file contents, track file modifications made through applications (e.g., saving in VS Code or Word), or access files outside the directories you actively browse.</p><h3>Data Retention</h3><p>File journey records are retained on the server for the period configured by your organization''s administrator (default: 30 days). After this period, journey data is automatically purged.</p><h3>Your Rights</h3><p>You may revoke consent for file journey tracking at any time. Revoking consent will stop future data collection for this feature. Previously collected data will be retained until it expires under the organization''s data retention policy.</p>',
     '1.0', true, 'file_journey', 'featured_based', 1, 3),
    ('tc-004', 'Live Screen Viewing',
     '<h3>What This Feature Does</h3><p>When enabled, authorized administrators can view your screen in real-time. This feature is intended for remote support, training, and collaboration scenarios.</p><h3>How This Data Is Used</h3><p>Live screen viewing is used only when an administrator explicitly initiates a viewing session. Your screen is not continuously recorded or streamed without your knowledge.</p><h3>Notification</h3><p>When an administrator initiates a live viewing session, you will receive a notification on your desktop. You will always know when your screen is being viewed.</p><h3>Your Rights</h3><p>You may revoke consent for live screen viewing at any time. Revoking consent will immediately terminate any active viewing session and prevent future sessions until you re-consent.</p>',
     '1.0', true, 'live_view', 'featured_based', 1, 4)
ON CONFLICT (id) DO NOTHING;
