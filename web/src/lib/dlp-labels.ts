// Human-readable labels for the DLP vocabulary.
//
// The server stores machine tokens (`usb_storage`, `blocked`, `false_positive`,
// ...) so the UI must translate them. The maps are DATA, not a hardcoded
// product list — a new token the agent reports simply falls back to a
// prettified version of itself (see `titleize`), so an unrecognised value is
// still readable instead of blank.

import type { DlpAlertStatus, DlpSeverity } from '@/lib/api';

/** Alert type -> short human label. */
export const ALERT_TYPE_LABELS: Record<string, string> = {
  usb_storage: 'USB storage',
  usb_file: 'USB file',
  web_navigate: 'Website visit',
  software_install: 'Software install',
  software_uninstall: 'Software uninstall',
  cloud_sync_folder: 'Cloud sync folder',
  file_transfer: 'File transfer',
  email: 'Email',
  clipboard: 'Clipboard',
  screenshot: 'Screenshot',
  print: 'Print',
  document: 'Document',
};

/** What the client actually did when it raised the alert. */
export const ACTION_LABELS: Record<string, string> = {
  alerted: 'Alerted only',
  blocked: 'Blocked',
  blocked_failed: 'Block failed',
};

export const STATUS_LABELS: Record<DlpAlertStatus, string> = {
  open: 'Open',
  investigating: 'Investigating',
  resolved: 'Resolved',
  false_positive: 'False Positive',
};

export const SEVERITY_LABELS: Record<DlpSeverity, string> = {
  critical: 'Critical',
  high: 'High',
  medium: 'Medium',
  low: 'Low',
};

/** Turn an unknown snake_case token into a readable label. */
export function titleize(token?: string | null): string {
  if (!token) return '—';
  const s = token.replace(/[_-]+/g, ' ').trim();
  if (!s) return '—';
  return s.charAt(0).toUpperCase() + s.slice(1);
}

export function alertTypeLabel(type?: string | null): string {
  if (!type) return 'Unknown event';
  return ALERT_TYPE_LABELS[type] ?? titleize(type);
}

export function actionLabel(action?: string | null): string {
  if (!action) return '—';
  return ACTION_LABELS[action] ?? titleize(action);
}

export function statusLabel(status?: string | null): string {
  if (!status) return '—';
  return STATUS_LABELS[status as DlpAlertStatus] ?? titleize(status);
}

export function severityLabel(severity?: string | null): string {
  if (!severity) return '—';
  return SEVERITY_LABELS[severity as DlpSeverity] ?? titleize(severity);
}

/**
 * Parse blocked / related domains from free-form evidence.
 * Agents may send `domains` as CSV, JSON array string, or a single `domain` / `url`.
 */
export function evidenceDomains(evidence?: Record<string, unknown> | null): string[] {
  if (!evidence || typeof evidence !== 'object') return [];
  const out: string[] = [];
  const push = (raw: string) => {
    const s = raw.trim().toLowerCase();
    if (!s || out.includes(s)) return;
    out.push(s);
  };

  const domainsVal = evidence.domains;
  if (typeof domainsVal === 'string' && domainsVal.trim()) {
    const t = domainsVal.trim();
    if (t.startsWith('[')) {
      try {
        const parsed = JSON.parse(t) as unknown;
        if (Array.isArray(parsed)) {
          for (const item of parsed) {
            if (typeof item === 'string') push(item);
          }
        }
      } catch {
        for (const part of t.split(/[,;\s]+/)) push(part);
      }
    } else {
      for (const part of t.split(/[,;]+/)) push(part);
    }
  } else if (Array.isArray(domainsVal)) {
    for (const item of domainsVal) {
      if (typeof item === 'string') push(item);
    }
  }

  for (const key of ['domain', 'host', 'hostname'] as const) {
    const v = evidence[key];
    if (typeof v === 'string' && v.trim()) push(v);
  }

  const url = evidence.url;
  if (typeof url === 'string' && url.trim()) {
    try {
      const u = new URL(url.trim());
      if (u.hostname) push(u.hostname);
    } catch {
      /* not a URL */
    }
  }

  return out;
}

/** One-line domain hint for list rows (first domain + optional +N). */
export function evidenceDomainLabel(evidence?: Record<string, unknown> | null): string {
  const domains = evidenceDomains(evidence);
  if (domains.length === 0) return '';
  if (domains.length === 1) return domains[0];
  return `${domains[0]} +${domains.length - 1}`;
}

/**
 * Extract a short, human "what happened" string from the evidence object.
 *
 * The evidence JSON is free-form (the agent decides the shape), so we look
 * for the first known, human-meaningful key instead of dumping raw JSON at
 * the user. Falls back to the first scalar value.
 */
export function evidenceSummary(evidence?: Record<string, unknown> | null): string {
  if (!evidence || typeof evidence !== 'object') return '';
  const domainLabel = evidenceDomainLabel(evidence);
  if (domainLabel) return domainLabel;
  const preferred = [
    'path', 'file', 'url', 'app', 'application', 'process',
    'device', 'volume', 'reason', 'action', 'extension', 'target', 'source',
  ];
  for (const key of preferred) {
    const v = evidence[key];
    if (typeof v === 'string' && v.trim()) return v.trim();
    if (typeof v === 'number' && Number.isFinite(v)) return String(v);
  }
  for (const v of Object.values(evidence)) {
    if (typeof v === 'string' && v.trim()) return v.trim();
    if (typeof v === 'number' && Number.isFinite(v)) return String(v);
  }
  return '';
}
