'use client';

import { Monitor } from 'lucide-react';
import type { LiveStreamEmployee } from '@/lib/api';
import { LiveStreamWatchTile, LiveStreamEmptyPane } from './LiveStreamWatchTile';
import type { LiveStreamCols } from './live-stream-selection';

/** Legacy auto-fit for the main `/live-stream` console (max 4, fill pane). */
export function liveStreamGridClass(tileCount: number): string {
  if (tileCount <= 1) return 'grid-cols-1 grid-rows-1';
  if (tileCount === 2) return 'grid-cols-2 grid-rows-1';
  return 'grid-cols-2 grid-rows-2';
}

export function LiveStreamPreviewGrid({
  selectedIds,
  byId,
  maxTiles,
  onRemove,
  emptyTitle = 'Select employees',
  emptyBody,
  /** Theater: fixed columns + vertical scroll. Omit for console fill layout. */
  columns,
}: {
  selectedIds: string[];
  byId: Map<string, LiveStreamEmployee>;
  maxTiles: number;
  onRemove: (employeeId: string) => void;
  emptyTitle?: string;
  emptyBody?: string;
  columns?: LiveStreamCols;
}) {
  const tileCount = selectedIds.length;
  const scrollable = columns != null;

  if (scrollable) {
    return (
      <div className="flex-1 relative bg-muted/20 min-h-0 overflow-y-auto">
        {tileCount === 0 ? (
          <div className="relative min-h-full">
            <LiveStreamEmptyPane
              icon={Monitor}
              title={emptyTitle}
              body={
                emptyBody ??
                `Choose employees from the list (up to ${maxTiles}). Scroll the wall when many screens are open.`
              }
            />
          </div>
        ) : (
          <div
            className="grid gap-3 p-3 auto-rows-fr"
            style={{
              gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))`,
            }}
          >
            {selectedIds.map((id) => (
              <div
                key={id}
                className="aspect-video min-h-[160px] sm:min-h-[200px] rounded-lg overflow-hidden shadow-sm ring-1 ring-border/60"
              >
                <LiveStreamWatchTile
                  emp={byId.get(id) ?? null}
                  employeeId={id}
                  onClose={() => onRemove(id)}
                />
              </div>
            ))}
          </div>
        )}
      </div>
    );
  }

  const gridClass = liveStreamGridClass(tileCount);

  return (
    <div className={`flex-1 relative bg-muted/30 overflow-hidden grid gap-2 p-2 ${gridClass}`}>
      {tileCount === 0 && (
        <LiveStreamEmptyPane
          icon={Monitor}
          title={emptyTitle}
          body={
            emptyBody ??
            `Choose up to ${maxTiles} people from the list. Multiple admins can watch the same employee — each PC encodes once.`
          }
        />
      )}
      {selectedIds.map((id) => (
        <LiveStreamWatchTile
          key={id}
          emp={byId.get(id) ?? null}
          employeeId={id}
          onClose={() => onRemove(id)}
        />
      ))}
    </div>
  );
}
