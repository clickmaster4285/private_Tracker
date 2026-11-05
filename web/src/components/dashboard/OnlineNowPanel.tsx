'use client';

import Link from 'next/link';
import { Loader2, Wifi, WifiOff, Radio } from 'lucide-react';
import type { LiveStreamEmployee } from '@/lib/api';

export function OnlineNowPanel({
  employees,
  total,
  loading,
  error,
  disabled,
  onRetry,
}: {
  employees?: LiveStreamEmployee[];
  total?: number;
  loading?: boolean;
  error?: boolean;
  disabled?: boolean;
  onRetry?: () => void;
}) {
  return (
    <div className="bg-card rounded-xl border border-border p-5 h-full">
      <div className="flex items-center justify-between mb-3">
        <h3 className="font-display font-bold text-foreground flex items-center gap-2">
          <Wifi className="w-4 h-4 text-primary" /> Online now
        </h3>
        {typeof total === 'number' && !disabled && (
          <span className="text-xs text-muted-foreground">{total} online</span>
        )}
      </div>

      {disabled ? (
        <div className="flex flex-col items-center justify-center py-8 text-center gap-2">
          <WifiOff className="w-5 h-5 text-muted-foreground" />
          <p className="text-sm text-muted-foreground">Live stream / presence is disabled on the server.</p>
          <Link href="/live-stream" className="text-xs text-primary hover:underline">Open Live Stream</Link>
        </div>
      ) : loading && !employees ? (
        <div className="flex justify-center py-8"><Loader2 className="w-5 h-5 animate-spin text-primary" /></div>
      ) : error && !employees ? (
        <div className="text-sm text-muted-foreground flex items-center justify-between gap-2">
          <span>Could not load presence.</span>
          {onRetry && (
            <button type="button" onClick={onRetry} className="text-primary hover:underline text-xs">Retry</button>
          )}
        </div>
      ) : !employees || employees.length === 0 ? (
        <p className="text-sm text-muted-foreground py-6 text-center">No one online right now.</p>
      ) : (
        <ul className="space-y-2 max-h-[280px] overflow-y-auto">
          {employees.map((e) => (
            <li key={e.employeeId} className="flex items-center justify-between gap-2 text-sm">
              <div className="min-w-0">
                <p className="font-medium truncate">{e.name}</p>
                <p className="text-xs text-muted-foreground truncate">{e.department || e.employeeId}</p>
              </div>
              <div className="flex items-center gap-2 flex-shrink-0">
                {e.streaming && (
                  <span className="inline-flex items-center gap-1 text-[10px] uppercase tracking-wide text-primary">
                    <Radio className="w-3 h-3" /> Live
                  </span>
                )}
                <Link
                  href={`/live-stream?ids=${encodeURIComponent(e.employeeId)}`}
                  className="text-xs text-primary hover:underline"
                >
                  Watch
                </Link>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
