-- 075_dlp_file_transfer.sql
-- DLP Rules: two new triggers enforced by the Windows DlpService (FileTransferEnforcer):
--   file_download  — block browser downloads (Chromium DownloadRestrictions=3)
--   file_upload    — block browser uploads (Chromium AllowFileSelectionDialogs=0)
-- Enforced when an enabled rule with action block / alert_block is published.
-- Same constraint shape as 053, plus the two new values.

ALTER TABLE dlp_rules DROP CONSTRAINT IF EXISTS dlp_rules_trigger_check;
ALTER TABLE dlp_rules ADD CONSTRAINT dlp_rules_trigger_check CHECK (trigger IN (
    'usb_storage', 'usb_file',
    'software_install', 'software_uninstall', 'cloud_sync_folder',
    'file_transfer', 'email',
    'file_download', 'file_upload'
));
