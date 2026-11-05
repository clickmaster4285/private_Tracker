'use client';

import {
  Columns2,
  Columns3,
  LayoutGrid,
  Maximize2,
  Minimize2,
  PanelLeft,
  Square,
  Trash2,
  ExternalLink,
  Wifi,
} from 'lucide-react';
import {
  LIVE_STREAM_COLS_OPTIONS,
  type LiveStreamCols,
} from './live-stream-selection';

const COL_ICONS: Record<LiveStreamCols, typeof Square> = {
  1: Square,
  2: Columns2,
  3: Columns3,
  4: LayoutGrid,
};

export function LiveStreamTheaterNavbar({
  tileCount,
  maxTiles,
  cols,
  onColsChange,
  sidebarCollapsed,
  onToggleSidebar,
  onlineOnly,
  onOnlineOnlyChange,
  onClear,
  onToggleFullscreen,
  isFullscreen,
}: {
  tileCount: number;
  maxTiles: number;
  cols: LiveStreamCols;
  onColsChange: (cols: LiveStreamCols) => void;
  sidebarCollapsed: boolean;
  onToggleSidebar: () => void;
  onlineOnly: boolean;
  onOnlineOnlyChange: (value: boolean) => void;
  onClear: () => void;
  onToggleFullscreen: () => void;
  isFullscreen: boolean;
}) {
  return (
    <header className="h-12 shrink-0 px-3 border-b border-border bg-card/95 backdrop-blur flex items-center gap-2 sm:gap-3">
      <button
        type="button"
        onClick={onToggleSidebar}
        className="h-8 w-8 inline-flex items-center justify-center rounded-lg text-muted-foreground hover:text-foreground hover:bg-muted/60 transition-colors"
        title={sidebarCollapsed ? 'Show employees' : 'Hide employees'}
        aria-label={sidebarCollapsed ? 'Show employees' : 'Hide employees'}
      >
        <PanelLeft className="w-4 h-4" />
      </button>

      <div className="min-w-0 flex-1">
        <p className="text-sm font-semibold text-foreground truncate leading-tight">
          Live theater
        </p>
        <p className="text-[11px] text-muted-foreground truncate leading-tight">
          {tileCount === 0
            ? 'Select employees to preview'
            : `${tileCount} of ${maxTiles} screens · ${cols} per row`}
        </p>
      </div>

      <div
        className="hidden sm:flex items-center rounded-lg border border-border bg-background p-0.5"
        role="group"
        aria-label="Screens per row"
      >
        {LIVE_STREAM_COLS_OPTIONS.map((n) => {
          const Icon = COL_ICONS[n];
          const active = cols === n;
          return (
            <button
              key={n}
              type="button"
              onClick={() => onColsChange(n)}
              className={`h-7 min-w-8 px-1.5 inline-flex items-center justify-center gap-1 rounded-md text-[11px] font-medium transition-colors ${
                active
                  ? 'bg-primary text-primary-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground hover:bg-muted/50'
              }`}
              title={`${n} screen${n === 1 ? '' : 's'} per row`}
              aria-pressed={active}
            >
              <Icon className="w-3.5 h-3.5" />
              <span className="tabular-nums">{n}</span>
            </button>
          );
        })}
      </div>

      {/* Mobile cols select */}
      <label className="sm:hidden flex items-center gap-1 text-[11px] text-muted-foreground">
        <span className="sr-only">Screens per row</span>
        <select
          value={cols}
          onChange={(e) => onColsChange(Number(e.target.value) as LiveStreamCols)}
          className="h-8 rounded-lg border border-border bg-background px-2 text-xs text-foreground"
        >
          {LIVE_STREAM_COLS_OPTIONS.map((n) => (
            <option key={n} value={n}>
              {n} / row
            </option>
          ))}
        </select>
      </label>

      <button
        type="button"
        onClick={() => onOnlineOnlyChange(!onlineOnly)}
        className={`h-8 px-2 inline-flex items-center gap-1.5 rounded-lg text-xs font-medium transition-colors border ${
          onlineOnly
            ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400'
            : 'border-border text-muted-foreground hover:text-foreground hover:bg-muted/50'
        }`}
        title="Show only online employees in the sidebar"
        aria-pressed={onlineOnly}
      >
        <Wifi className="w-3.5 h-3.5" />
        <span className="hidden md:inline">Online</span>
      </button>

      <button
        type="button"
        onClick={onClear}
        disabled={tileCount === 0}
        className="h-8 px-2 inline-flex items-center gap-1.5 rounded-lg text-xs font-medium text-muted-foreground hover:text-destructive hover:bg-destructive/10 transition-colors disabled:opacity-40 disabled:pointer-events-none"
        title="Clear all selected screens"
      >
        <Trash2 className="w-3.5 h-3.5" />
        <span className="hidden md:inline">Clear</span>
      </button>

      <button
        type="button"
        onClick={onToggleFullscreen}
        className="h-8 w-8 inline-flex items-center justify-center rounded-lg text-muted-foreground hover:text-foreground hover:bg-muted/60 transition-colors"
        title={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
        aria-label={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
      >
        {isFullscreen ? (
          <Minimize2 className="w-4 h-4" />
        ) : (
          <Maximize2 className="w-4 h-4" />
        )}
      </button>

      <a
        href="/live-stream"
        className="h-8 px-2 inline-flex items-center gap-1.5 rounded-lg text-xs font-medium text-muted-foreground hover:text-primary hover:bg-muted/60 transition-colors"
        title="Open main Live Stream console"
      >
        <ExternalLink className="w-3.5 h-3.5" />
        <span className="hidden lg:inline">Console</span>
      </a>
    </header>
  );
}
