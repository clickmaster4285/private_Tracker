-- 038_terms_content_add_columns.sql
-- Adds feature_id, term_type, is_active to existing terms_content table
-- (migration 037 ran before these columns were added).

ALTER TABLE terms_content
    ADD COLUMN IF NOT EXISTS is_system    BOOLEAN     NOT NULL DEFAULT FALSE,
    ADD COLUMN IF NOT EXISTS feature_id   VARCHAR(100) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS term_type    VARCHAR(50)  NOT NULL DEFAULT 'manual_based',
    ADD COLUMN IF NOT EXISTS is_active    INTEGER      NOT NULL DEFAULT 1,
    ADD COLUMN IF NOT EXISTS sort_order   INTEGER      NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS idx_terms_content_feature_id
    ON terms_content (feature_id)
    WHERE deleted_at IS NULL;

-- Seed featured terms only if they don't already exist
INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order)
SELECT 'tc-001', 'Application Usage Tracking',
     '<h3>What we track</h3><p>The tracker records which applications you open and close on this computer, how long each one stays open, and whether it is in the foreground or background.</p><h3>What is collected</h3><p>Application name, when you opened and closed it, how long it was used, and whether it was the active window.</p><h3>Why it is collected</h3><p>Your organisation uses this information to measure productivity, track working time, and generate attendance and activity reports.</p><h3>Who can see it</h3><p>Only authorised administrators in your organisation. The data is not sold or shared outside your company.</p><h3>Your rights</h3><p>This feature is required. You must accept these terms to continue using the tracker. You cannot revoke consent for Application Usage Tracking while the tracker is active on your machine.</p>',
     '1.0', true, 'app_usage', 'featured_based', 1, 1
WHERE NOT EXISTS (SELECT 1 FROM terms_content WHERE feature_id = 'app_usage' AND deleted_at IS NULL);

INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order)
SELECT 'tc-002', 'Browser Journey Tracking',
     '<h3>What we track</h3><p>The tracker records the websites and web pages you visit in browsers (Chrome, Firefox, Edge, and others) and in embedded browsers inside other apps (for example VS Code, Slack, or Teams).</p><h3>What is collected</h3><p>Page title, website address (URL), domain name, browser name, whether the window was private or incognito, and when the page was opened and closed.</p><h3>Why it is collected</h3><p>Your organisation uses this information to understand which online resources are used for work and to support compliance and productivity reviews.</p><h3>Who can see it</h3><p>Only authorised administrators in your organisation.</p><h3>Your rights</h3><p>This feature is optional. You may decline now or revoke your consent later. Turning it off does not affect Application Usage Tracking or any other features you have accepted. Previously collected data is retained until it expires under your organisation''s retention policy.</p>',
     '1.0', true, 'browser_journey', 'featured_based', 1, 2
WHERE NOT EXISTS (SELECT 1 FROM terms_content WHERE feature_id = 'browser_journey' AND deleted_at IS NULL);

INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order)
SELECT 'tc-003', 'File Explorer Journey Tracking',
     '<h3>What we track</h3><p>The tracker records activity in the file manager (File Explorer, Nautilus, Finder, and similar): folders you open and files you create, rename, delete, or open.</p><h3>What is collected</h3><p>Folder and file paths, the action performed (open, create, rename, delete, navigate), and when the action happened.</p><h3>What is not collected</h3><p>The content of your files is never opened or read. Only the path and the type of action are recorded. System, cache, and application-data folders are excluded.</p><h3>Why it is collected</h3><p>Your organisation uses this information for security reviews and to understand how work documents and project folders are used.</p><h3>Your rights</h3><p>This feature is optional. You may decline now or revoke your consent later. Previously collected data is retained until it expires under your organisation''s retention policy.</p>',
     '1.0', true, 'file_journey', 'featured_based', 1, 3
WHERE NOT EXISTS (SELECT 1 FROM terms_content WHERE feature_id = 'file_journey' AND deleted_at IS NULL);

INSERT INTO terms_content (id, heading, body, terms_version, is_system, feature_id, term_type, is_active, sort_order)
SELECT 'tc-004', 'Live Screen Viewing',
     '<h3>What we track</h3><p>If you accept, authorised administrators may start a live viewing session of your screen when needed (for example for remote support or a specific compliance check).</p><h3>How it works</h3><p>Live viewing does not run continuously in the background. A session only starts when an authorised administrator explicitly begins it. Sessions are temporary and are logged.</p><h3>Who can start a session</h3><p>Only administrators who have the required permissions in your organisation.</p><h3>Your rights</h3><p>This feature is optional and is off by default. You may decline now or revoke your consent later. After you revoke consent, no new live viewing sessions can be started on your device until you accept again.</p>',
     '1.0', true, 'live_view', 'featured_based', 1, 4
WHERE NOT EXISTS (SELECT 1 FROM terms_content WHERE feature_id = 'live_view' AND deleted_at IS NULL);
