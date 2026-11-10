'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ChevronLeft,
  ChevronRight,
  Loader2,
  X,
  ZoomIn,
  ZoomOut,
  Maximize2,
} from 'lucide-react';
import { screenshotsApi, type ScreenshotRow } from '@/lib/api';
import { formatDateTimeSeconds } from '@/lib/format';

const MIN_ZOOM = 1;
const MAX_ZOOM = 5;
const ZOOM_STEP = 0.5;

export function ScreenshotLightbox({
  rows,
  index,
  onIndexChange,
  onClose,
  hasMore,
  loadingMore,
  onNeedMore,
}: {
  rows: ScreenshotRow[];
  index: number;
  onIndexChange: (next: number) => void;
  onClose: () => void;
  hasMore: boolean;
  loadingMore: boolean;
  onNeedMore: () => void;
}) {
  const row = rows[index] ?? null;
  const [zoom, setZoom] = useState(1);
  const [offset, setOffset] = useState({ x: 0, y: 0 });
  const [dragging, setDragging] = useState(false);
  const dragRef = useRef<{
    startX: number;
    startY: number;
    origX: number;
    origY: number;
  } | null>(null);

  const resetView = useCallback(() => {
    setZoom(1);
    setOffset({ x: 0, y: 0 });
  }, []);

  useEffect(() => {
    resetView();
  }, [row?.id, resetView]);

  const goPrev = useCallback(() => {
    if (index > 0) onIndexChange(index - 1);
  }, [index, onIndexChange]);

  const goNext = useCallback(() => {
    if (index < rows.length - 1) {
      onIndexChange(index + 1);
      return;
    }
    if (hasMore && !loadingMore) onNeedMore();
  }, [index, rows.length, hasMore, loadingMore, onIndexChange, onNeedMore]);

  const zoomBy = useCallback((delta: number) => {
    setZoom((z) => {
      const next = Math.min(MAX_ZOOM, Math.max(MIN_ZOOM, Math.round((z + delta) * 100) / 100));
      if (next <= MIN_ZOOM) setOffset({ x: 0, y: 0 });
      return next;
    });
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        onClose();
        return;
      }
      if (e.key === 'ArrowLeft') {
        e.preventDefault();
        goPrev();
      } else if (e.key === 'ArrowRight') {
        e.preventDefault();
        goNext();
      } else if (e.key === '+' || e.key === '=') {
        e.preventDefault();
        zoomBy(ZOOM_STEP);
      } else if (e.key === '-' || e.key === '_') {
        e.preventDefault();
        zoomBy(-ZOOM_STEP);
      } else if (e.key === '0') {
        e.preventDefault();
        resetView();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [goPrev, goNext, zoomBy, onClose, resetView]);

  const onPointerDown = (e: React.PointerEvent) => {
    if (zoom <= MIN_ZOOM) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    dragRef.current = {
      startX: e.clientX,
      startY: e.clientY,
      origX: offset.x,
      origY: offset.y,
    };
    setDragging(true);
  };

  const onPointerMove = (e: React.PointerEvent) => {
    const d = dragRef.current;
    if (!d) return;
    setOffset({
      x: d.origX + (e.clientX - d.startX),
      y: d.origY + (e.clientY - d.startY),
    });
  };

  const onPointerUp = (e: React.PointerEvent) => {
    if (dragRef.current) {
      try {
        e.currentTarget.releasePointerCapture(e.pointerId);
      } catch {
        /* ignored */
      }
    }
    dragRef.current = null;
    setDragging(false);
  };

  const onWheel = (e: React.WheelEvent) => {
    if (!e.ctrlKey && Math.abs(e.deltaY) < 40) return;
    e.preventDefault();
    zoomBy(e.deltaY > 0 ? -ZOOM_STEP : ZOOM_STEP);
  };

  if (!row) return null;

  const canPrev = index > 0;
  const canNext = index < rows.length - 1 || hasMore;
  const zoomed = zoom > MIN_ZOOM;

  return (
    <div
      className="fixed inset-0 z-50 bg-black/85 flex flex-col"
      role="dialog"
      aria-modal="true"
      onClick={onClose}
    >
      <div
        className="absolute top-3 left-3 right-3 z-20 flex items-center justify-between gap-2 pointer-events-none"
        onClick={(e) => e.stopPropagation()}
      >
        <p className="pointer-events-auto rounded-md bg-black/60 px-2 py-1 text-xs text-white/90 truncate max-w-[50vw]">
          {row.employeeName || row.employeeId} · {formatDateTimeSeconds(row.capturedAt)}
          {row.width > 0 ? ` · ${row.width}×${row.height}` : ''}
          <span className="text-white/50"> · {index + 1}/{rows.length}{hasMore ? '+' : ''}</span>
        </p>
        <div className="pointer-events-auto flex items-center gap-1">
          <ToolbarBtn
            label="Zoom out"
            onClick={() => zoomBy(-ZOOM_STEP)}
            disabled={zoom <= MIN_ZOOM}
          >
            <ZoomOut className="w-4 h-4" />
          </ToolbarBtn>
          <span className="min-w-[3rem] text-center text-xs text-white/80 tabular-nums">
            {Math.round(zoom * 100)}%
          </span>
          <ToolbarBtn
            label="Zoom in"
            onClick={() => zoomBy(ZOOM_STEP)}
            disabled={zoom >= MAX_ZOOM}
          >
            <ZoomIn className="w-4 h-4" />
          </ToolbarBtn>
          <ToolbarBtn label="Reset zoom" onClick={resetView} disabled={!zoomed}>
            <Maximize2 className="w-4 h-4" />
          </ToolbarBtn>
          <ToolbarBtn label="Close" onClick={onClose}>
            <X className="w-4 h-4" />
          </ToolbarBtn>
        </div>
      </div>

      <button
        type="button"
        className="absolute left-2 sm:left-4 top-1/2 -translate-y-1/2 z-20 h-11 w-11 rounded-full bg-black/60 text-white inline-flex items-center justify-center hover:bg-black/80 disabled:opacity-30 disabled:pointer-events-none"
        onClick={(e) => {
          e.stopPropagation();
          goPrev();
        }}
        disabled={!canPrev}
        aria-label="Previous screenshot"
      >
        <ChevronLeft className="w-6 h-6" />
      </button>
      <button
        type="button"
        className="absolute right-2 sm:right-4 top-1/2 -translate-y-1/2 z-20 h-11 w-11 rounded-full bg-black/60 text-white inline-flex items-center justify-center hover:bg-black/80 disabled:opacity-30 disabled:pointer-events-none"
        onClick={(e) => {
          e.stopPropagation();
          goNext();
        }}
        disabled={!canNext || (index >= rows.length - 1 && loadingMore)}
        aria-label="Next screenshot"
      >
        {index >= rows.length - 1 && loadingMore ? (
          <Loader2 className="w-5 h-5 animate-spin" />
        ) : (
          <ChevronRight className="w-6 h-6" />
        )}
      </button>

      <div
        className="flex-1 min-h-0 flex items-center justify-center overflow-hidden px-14 py-16"
        onClick={(e) => e.stopPropagation()}
        onWheel={onWheel}
      >
        <div
          className={`relative max-w-full max-h-full touch-none ${
            zoomed ? (dragging ? 'cursor-grabbing' : 'cursor-grab') : 'cursor-default'
          }`}
          style={{
            transform: `translate(${offset.x}px, ${offset.y}px) scale(${zoom})`,
            transformOrigin: 'center center',
            transition: dragging ? 'none' : 'transform 120ms ease-out',
          }}
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
        >
          <img
            src={screenshotsApi.imageSrc(row.id)}
            alt={`Screenshot ${row.employeeId}`}
            draggable={false}
            className="max-w-[min(92vw,1400px)] max-h-[78vh] object-contain rounded-md bg-black select-none pointer-events-none"
          />
        </div>
      </div>
    </div>
  );
}

function ToolbarBtn({
  label,
  onClick,
  disabled,
  children,
}: {
  label: string;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      type="button"
      title={label}
      aria-label={label}
      disabled={disabled}
      onClick={onClick}
      className="h-9 w-9 rounded-full bg-black/60 text-white inline-flex items-center justify-center hover:bg-black/80 disabled:opacity-40 disabled:pointer-events-none"
    >
      {children}
    </button>
  );
}
