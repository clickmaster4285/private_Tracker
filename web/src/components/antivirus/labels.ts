import type { AntivirusCommandStatus, AntivirusCommandType } from '@/lib/api';

/** Plain-language names + one-line explanations for each remote antivirus command. */
export const COMMAND_INFO: Record<AntivirusCommandType, { label: string; help: string }> = {
  scan_quick: {
    label: 'Quick scan',
    help: 'Checks the places malware usually hides (memory, startup, system folders). Takes a few minutes.',
  },
  scan_full: {
    label: 'Full scan',
    help: 'Checks every file on every drive. Can take several hours; the PC stays usable.',
  },
  scan_custom: {
    label: 'Custom scan',
    help: 'Checks one folder or file you choose, e.g. a USB drive or the Downloads folder.',
  },
  scan_offline: {
    label: 'Offline scan',
    help: 'Restarts the PC and scans before Windows loads.',
  },
  update_signatures: {
    label: 'Update signatures',
    help: 'Downloads the newest virus definitions from Microsoft so new threats are recognised.',
  },
  remove_threats: {
    label: 'Remove threats',
    help: 'Deletes every threat Defender has found on this PC (including quarantined items).',
  },
  remove_threat: { label: 'Remove one threat', help: 'Deletes one specific threat.' },
  restore_threat: {
    label: 'Restore from quarantine',
    help: 'Puts back a file Defender quarantined (use only for a false alarm).',
  },
};

export const COMMAND_STATUS: Record<AntivirusCommandStatus, { label: string; help: string; cls: string }> = {
  pending: {
    label: 'Waiting for PC',
    help: 'Queued. The PC picks it up on its next check-in (about every 30 seconds while it is on).',
    cls: 'bg-muted text-muted-foreground',
  },
  running: {
    label: 'Running',
    help: 'The PC has started this command.',
    cls: 'bg-primary/15 text-primary',
  },
  done: { label: 'Finished', help: 'The command completed successfully.', cls: 'bg-success/15 text-success' },
  failed: {
    label: 'Failed',
    help: 'The PC tried but the command did not succeed — see the result text.',
    cls: 'bg-destructive/15 text-destructive',
  },
  expired: {
    label: 'Expired',
    help: 'The PC was off or offline too long, so the command was cancelled. Queue it again.',
    cls: 'bg-warning/15 text-warning',
  },
};

export const HEALTH_HELP: Record<string, string> = {
  protected: 'Antivirus is on, scanning files in real time, and virus definitions are up to date.',
  outdated: 'Antivirus is on, but virus definitions are more than 3 days old — new threats may be missed.',
  realtime_off: 'Real-time scanning is off — files are not checked when they are opened. Needs attention.',
  disabled: 'Microsoft Defender is switched off on this PC. Needs attention.',
  third_party: 'Another antivirus product is installed and protecting this PC; it is reported, not changed.',
  unavailable: 'The antivirus status could not be read on this PC.',
  unknown: 'Status not known yet.',
};

/**
 * Same rule the server enforces for scan_custom: an absolute Windows path —
 * a drive path (C:\...) or a network path (\\server\share\...).
 */
export function isValidScanPath(p: string): boolean {
  const v = p.trim();
  if (v.length < 3 || v.length > 1000) return false;
  return /^(?:[A-Za-z]:\\|\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+)[^\x00-\x1f"<>|*?]*$/.test(v);
}
