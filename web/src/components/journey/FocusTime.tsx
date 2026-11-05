'use client';

import { formatSeconds } from '@/lib/format';

/** Stacked bar + text showing how long a session was foreground vs background. */
export default function FocusTime({ fg, bg }: { fg: number; bg: number }) {
  const total = fg + bg;
  if (total <= 0) {
    return <span className="text-xs text-muted-foreground">—</span>;
  }
  const fgPct = Math.round((fg / total) * 100);
  return (
    <div className="flex items-center gap-2">
      <div className="flex h-1.5 w-24 rounded-full bg-muted overflow-hidden flex-shrink-0">
        <div className="bg-success" style={{ width: `${fgPct}%` }} title={`${fgPct}% foreground`} />
      </div>
      <span className="text-xs whitespace-nowrap">
        <span className="text-success font-medium">{formatSeconds(fg)}</span>
        <span className="text-muted-foreground"> / {formatSeconds(bg)}</span>
      </span>
    </div>
  );
}
