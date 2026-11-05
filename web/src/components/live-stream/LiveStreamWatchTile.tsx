'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Loader2,
  Monitor,
  WifiOff,
  ShieldAlert,
  Circle,
  X,
  Maximize2,
  Minimize2,
} from 'lucide-react';
import type { LiveStreamEmployee } from '@/lib/api';
import { useLiveStreamSocket } from '@/lib/useLiveStreamSocket';
import { useVideoFps } from '@/hooks/use-video-fps';

export function LiveStreamWatchTile({
  emp,
  employeeId,
  onClose,
}: {
  emp: LiveStreamEmployee | null;
  employeeId: string;
  onClose: () => void;
}) {
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const tileRef = useRef<HTMLDivElement | null>(null);
  const [isFullscreen, setIsFullscreen] = useState(false);
  const canWatch = !emp?.consentMissing;
  const socket = useLiveStreamSocket(employeeId, videoRef, { enabled: canWatch });
  const fps = useVideoFps(videoRef, socket.status === 'live');

  useEffect(() => {
    const onFs = () => {
      setIsFullscreen(document.fullscreenElement === tileRef.current);
    };
    document.addEventListener('fullscreenchange', onFs);
    return () => document.removeEventListener('fullscreenchange', onFs);
  }, []);

  const toggleFullscreen = useCallback(async () => {
    const el = tileRef.current;
    if (!el) return;
    try {
      if (document.fullscreenElement === el) {
        await document.exitFullscreen();
      } else if (document.fullscreenElement) {
        await document.exitFullscreen();
        await el.requestFullscreen();
      } else {
        await el.requestFullscreen();
      }
    } catch {
      /* browser may deny fullscreen */
    }
  }, []);

  return (
    <div
      ref={tileRef}
      className="group relative h-full w-full min-h-0 rounded-lg border border-border bg-black overflow-hidden flex flex-col [:fullscreen]:h-screen [:fullscreen]:w-screen [:fullscreen]:rounded-none [:fullscreen]:border-0"
    >
      <div className="absolute top-2 left-2 right-2 z-10 flex items-start justify-between gap-2 pointer-events-none">
        <div className="min-w-0 rounded-md bg-black/60 px-2 py-1 pointer-events-auto">
          <p className="text-xs font-medium text-white truncate">
            {emp?.name ?? employeeId}
          </p>
          <p className="text-[10px] text-white/70 truncate">{employeeId}</p>
        </div>
        <div className="flex items-center gap-1 pointer-events-auto">
          {socket.status === 'live' && (
            <>
              <span className="inline-flex items-center gap-1 px-1.5 py-0.5 rounded bg-red-500/90 text-white text-[10px] font-medium">
                <Circle className="w-1.5 h-1.5 fill-current" />
                LIVE
              </span>
              <span
                className="inline-flex items-center px-1.5 py-0.5 rounded bg-black/70 text-white text-[10px] font-medium tabular-nums"
                title="Decoded display frame rate"
              >
                {fps != null ? `${fps} FPS` : '— FPS'}
              </span>
            </>
          )}
          <div
            className={`flex items-center gap-1 transition-opacity ${
              isFullscreen
                ? 'opacity-100'
                : 'opacity-0 pointer-events-none group-hover:opacity-100 group-hover:pointer-events-auto focus-within:opacity-100 focus-within:pointer-events-auto'
            }`}
          >
            {emp && socket.clientConnected && socket.monitors.length > 1 && (
              <select
                value={socket.selectedMonitor}
                onChange={(e) => socket.selectMonitor(Number(e.target.value))}
                className="h-6 max-w-[9rem] rounded border border-white/20 bg-black/70 px-1 text-[10px] text-white"
              >
                {socket.monitors.map((m) => (
                  <option key={m.index} value={m.index}>
                    {m.name || `Display ${m.index + 1}`}
                  </option>
                ))}
              </select>
            )}
            <button
              type="button"
              onClick={toggleFullscreen}
              className="h-6 w-6 inline-flex items-center justify-center rounded bg-black/60 text-white hover:bg-black/80"
              title={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
              aria-label={isFullscreen ? 'Exit fullscreen' : 'Fullscreen'}
            >
              {isFullscreen ? (
                <Minimize2 className="w-3.5 h-3.5" />
              ) : (
                <Maximize2 className="w-3.5 h-3.5" />
              )}
            </button>
            {!isFullscreen && (
              <button
                type="button"
                onClick={onClose}
                className="h-6 w-6 inline-flex items-center justify-center rounded bg-black/60 text-white hover:bg-black/80"
                title="Remove"
              >
                <X className="w-3.5 h-3.5" />
              </button>
            )}
          </div>
        </div>
      </div>

      <div className="flex-1 relative min-h-0 bg-black">
        {/*
          Always fill the tile (inset-0 + w/h full). object-contain keeps the whole
          desktop visible with letterboxing — without this, intrinsic video size
          made some employees look "fullscreen" and others tiny/partial.
        */}
        <video
          ref={videoRef}
          autoPlay
          playsInline
          muted
          className={`absolute inset-0 h-full w-full object-contain ${
            socket.status === 'live' ? 'block' : 'hidden'
          }`}
        />
        <WatchOverlay emp={emp} socket={socket} />
      </div>
    </div>
  );
}

function WatchOverlay({
  emp,
  socket,
}: {
  emp: LiveStreamEmployee | null;
  socket: ReturnType<typeof useLiveStreamSocket>;
}) {
  if (!emp) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Unknown employee"
        body="This employee is no longer in the list."
      />
    );
  }
  if (emp.consentMissing || socket.status === 'consentMissing') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Consent required"
        body="This employee has not accepted Live Screen Viewing terms."
      />
    );
  }
  if (
    socket.status === 'unavailable' ||
    (socket.clientConnected && !socket.streamAvailable && socket.status !== 'live')
  ) {
    return (
      <EmptyPane
        icon={Monitor}
        title="Stream unavailable"
        body="Screen capture is not available on this OS (Windows-only for WebRTC publish)."
      />
    );
  }
  if (socket.status === 'error') {
    return (
      <EmptyPane
        icon={ShieldAlert}
        title="Connection error"
        body={socket.error || 'Could not open the WebRTC watch session.'}
      />
    );
  }
  if (socket.status === 'live') return null;
  if (!emp.online && !socket.clientConnected) {
    return (
      <EmptyPane
        icon={WifiOff}
        title="Employee offline"
        body="WS connection is down. Preview starts automatically when the desktop client reconnects."
      />
    );
  }
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 text-muted-foreground px-4 text-center">
      <Loader2 className="w-5 h-5 animate-spin" />
      <p className="text-xs font-medium text-foreground">
        {socket.clientConnected
          ? 'Starting WebRTC stream…'
          : emp.wsConnected
            ? 'Waiting for stream client…'
            : 'Waiting for desktop client…'}
      </p>
    </div>
  );
}

export function LiveStreamEmptyPane({
  icon: Icon,
  title,
  body,
}: {
  icon: typeof Monitor;
  title: string;
  body: string;
}) {
  return (
    <div className="absolute inset-0 flex flex-col items-center justify-center gap-2 px-6 text-center">
      <Icon className="w-8 h-8 text-muted-foreground/60" />
      <p className="text-sm font-medium text-foreground">{title}</p>
      <p className="text-xs text-muted-foreground max-w-sm">{body}</p>
    </div>
  );
}

function EmptyPane(props: {
  icon: typeof Monitor;
  title: string;
  body: string;
}) {
  return <LiveStreamEmptyPane {...props} />;
}
