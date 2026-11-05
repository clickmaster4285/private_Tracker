'use client';

import { useEffect, useRef, useState } from 'react';

/**
 * Decoded display FPS for a <video> element (requestVideoFrameCallback).
 * Returns null when inactive / unsupported.
 */
export function useVideoFps(
  videoRef: React.RefObject<HTMLVideoElement | null>,
  active: boolean,
): number | null {
  const [fps, setFps] = useState<number | null>(null);
  const framesRef = useRef(0);
  const windowStartRef = useRef(0);

  useEffect(() => {
    if (!active) {
      setFps(null);
      return;
    }

    let cancelled = false;
    let handle = 0;
    let rafFallback = 0;
    framesRef.current = 0;
    windowStartRef.current = performance.now();

    const report = (now: number) => {
      const elapsed = now - windowStartRef.current;
      if (elapsed >= 1000) {
        setFps(Math.round((framesRef.current * 1000) / elapsed));
        framesRef.current = 0;
        windowStartRef.current = now;
      }
    };

    const video = videoRef.current;
    if (video && typeof video.requestVideoFrameCallback === 'function') {
      const onFrame = (now: number) => {
        if (cancelled) return;
        framesRef.current += 1;
        report(now);
        handle = video.requestVideoFrameCallback(onFrame);
      };
      handle = video.requestVideoFrameCallback(onFrame);
      return () => {
        cancelled = true;
        if (typeof video.cancelVideoFrameCallback === 'function') {
          video.cancelVideoFrameCallback(handle);
        }
      };
    }

    // Fallback: sample currentTime changes (~display cadence, less accurate).
    let lastTime = -1;
    const tick = () => {
      if (cancelled) return;
      const v = videoRef.current;
      if (v && !v.paused && v.currentTime !== lastTime) {
        lastTime = v.currentTime;
        framesRef.current += 1;
        report(performance.now());
      }
      rafFallback = requestAnimationFrame(tick);
    };
    rafFallback = requestAnimationFrame(tick);
    return () => {
      cancelled = true;
      cancelAnimationFrame(rafFallback);
    };
  }, [active, videoRef]);

  return fps;
}
