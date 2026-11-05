'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { liveStreamApi, type IceServerConfig, ApiError } from '@/lib/api';

export type LiveStreamSocketStatus =
  | 'idle'
  | 'connecting'
  | 'live'
  | 'offline'
  | 'consentMissing'
  | 'unavailable'
  | 'error';

export interface LiveStreamMonitor {
  index: number;
  name: string;
  width: number;
  height: number;
  isPrimary: boolean;
}

export interface LiveStreamSocketState {
  status: LiveStreamSocketStatus;
  streaming: boolean;
  streamAvailable: boolean;
  clientConnected: boolean;
  consentMissing: boolean;
  error: string | null;
  monitors: LiveStreamMonitor[];
  selectedMonitor: number;
  selectMonitor: (index: number) => void;
}

/**
 * Per-PeerConnection signaling state. Remote ICE candidates that arrive before
 * the answer is applied MUST be queued — calling addIceCandidate with a null
 * remoteDescription throws InvalidStateError and silently drops the candidate,
 * breaking the whole ICE handshake (LIVE but no frames).
 */
interface PcEntry {
  pc: RTCPeerConnection;
  remoteReady: boolean;
  queue: RTCIceCandidateInit[];
}

function resolveWsBase(): string {
  const fromEnv = process.env.NEXT_PUBLIC_WS_URL?.trim();
  if (fromEnv) return fromEnv.replace(/\/$/, '');
  if (typeof window !== 'undefined') {
    const proto = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
    if (window.location.port === '3000') {
      return `${proto}//${window.location.hostname}:8080`;
    }
    return `${proto}//${window.location.host}`;
  }
  return 'ws://localhost:8080';
}

function toRtcIceServers(servers: IceServerConfig[] | undefined): RTCIceServer[] {
  if (!servers?.length) {
    return [{ urls: 'stun:stun.l.google.com:19302' }];
  }
  return servers.map((s) => ({
    urls: s.urls,
    username: s.username,
    credential: s.credential,
  }));
}

/**
 * Opens a watch WebSocket + RTCPeerConnection for one employee (WebRTC SFU).
 * Attach the remote MediaStream to a <video> via videoRef.
 *
 * Negotiation rules:
 * - One offer on socket open.
 * - One fresh offer on `track_ready` (publisher arrived after empty answer).
 * - NEVER re-offer on periodic `status` ticks (that caused stuck/pixelated video).
 */
export function useLiveStreamSocket(
  employeeId: string | null,
  videoRef: React.RefObject<HTMLVideoElement | null>,
  opts?: { enabled?: boolean },
): LiveStreamSocketState {
  const enabled = opts?.enabled !== false;
  const [state, setState] = useState<Omit<LiveStreamSocketState, 'selectMonitor'>>({
    status: 'idle',
    streaming: false,
    streamAvailable: false,
    clientConnected: false,
    consentMissing: false,
    error: null,
    monitors: [],
    selectedMonitor: 0,
  });

  const wsRef = useRef<WebSocket | null>(null);
  const pcRef = useRef<RTCPeerConnection | null>(null);
  const pcStateRef = useRef<PcEntry | null>(null);
  const videoHolder = useRef(videoRef);
  videoHolder.current = videoRef;
  const iceServersRef = useRef<RTCIceServer[]>([
    { urls: 'stun:stun.l.google.com:19302' },
  ]);
  const makingOffer = useRef(false);
  const liveRef = useRef(false);

  const selectMonitor = useCallback((index: number) => {
    const ws = wsRef.current;
    if (!ws || ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ type: 'select_monitor', index }));
    setState((s) => ({ ...s, selectedMonitor: index }));
  }, []);

  useEffect(() => {
    if (!employeeId || !enabled) {
      liveRef.current = false;
      setState({
        status: 'idle',
        streaming: false,
        streamAvailable: false,
        clientConnected: false,
        consentMissing: false,
        error: null,
        monitors: [],
        selectedMonitor: 0,
      });
      return;
    }

    let cancelled = false;
    let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
    let openOfferTimer: ReturnType<typeof setTimeout> | null = null;
    let attempt = 0;

    const stopVideoStream = (stream: MediaStream | null | undefined) => {
      if (!stream) return;
      for (const track of stream.getTracks()) {
        try {
          track.stop();
        } catch {
          /* ignored */
        }
      }
    };

    const cleanupPc = () => {
      const pc = pcRef.current;
      pcRef.current = null;
      pcStateRef.current = null;
      if (pc) {
        try {
          pc.close();
        } catch {
          /* ignored */
        }
      }
      const video = videoHolder.current.current;
      if (video) {
        try {
          video.pause();
        } catch {
          /* ignored */
        }
        stopVideoStream(video.srcObject as MediaStream | null);
        video.srcObject = null;
      }
    };

    const attachPcHandlers = (pc: RTCPeerConnection, ws: WebSocket) => {
      pc.addTransceiver('video', { direction: 'recvonly' });

      pc.ontrack = (ev) => {
        const stream = ev.streams[0] ?? new MediaStream([ev.track]);
        const video = videoHolder.current.current;
        if (video) {
          // Detach previous stream without track.stop() — stopping remote tracks
          // mid-renegotiation races with track_ready recreate and can blank video.
          try {
            video.pause();
          } catch {
            /* ignored */
          }
          video.srcObject = stream;
          void video.play().catch(() => undefined);
        }
        liveRef.current = true;
        setState((s) => ({ ...s, status: 'live', streaming: true }));
        // LIVE + black / — FPS: track attached but no decoded frames (missed IDR).
        // One recovery renegotiate after 5s if videoWidth stays 0.
        window.setTimeout(() => {
          if (cancelled || makingOffer.current) return;
          const v = videoHolder.current.current;
          if (!liveRef.current || !v || v.videoWidth > 0) return;
          if (ws.readyState !== WebSocket.OPEN) return;
          console.warn('[live-stream] live but no frames — renegotiating', employeeId);
          void createOffer(ws, true);
        }, 5000);
      };

      pc.onicecandidate = (ev) => {
        if (!ev.candidate || ws.readyState !== WebSocket.OPEN) return;
        ws.send(
          JSON.stringify({
            type: 'ice',
            candidate: ev.candidate.candidate,
            sdpMid: ev.candidate.sdpMid,
            sdpMLineIndex: ev.candidate.sdpMLineIndex,
          }),
        );
      };

      pc.onconnectionstatechange = () => {
        if (pc.connectionState === 'failed' || pc.connectionState === 'closed') {
          liveRef.current = false;
          setState((s) => ({
            ...s,
            streaming: false,
            status: s.status === 'consentMissing' ? 'consentMissing' : 'connecting',
          }));
        }
      };
    };

    const createOffer = async (ws: WebSocket, recreate: boolean) => {
      if (makingOffer.current || ws.readyState !== WebSocket.OPEN || cancelled) return;
      makingOffer.current = true;
      try {
        if (recreate) cleanupPc();
        let pc = pcRef.current;
        if (!pc) {
          pc = new RTCPeerConnection({ iceServers: iceServersRef.current });
          pcRef.current = pc;
          pcStateRef.current = { pc, remoteReady: false, queue: [] };
          attachPcHandlers(pc, ws);
        }
        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);
        if (ws.readyState === WebSocket.OPEN) {
          ws.send(JSON.stringify({ type: 'offer', sdp: offer.sdp }));
        }
      } catch (err) {
        console.warn('[live-stream] createOffer failed', err);
      } finally {
        makingOffer.current = false;
      }
    };

    const connect = async () => {
      if (cancelled) return;
      cleanupPc();
      liveRef.current = false;
      setState((s) => ({ ...s, status: 'connecting', error: null, streaming: false }));

      let ticket: string;
      let watchBase = resolveWsBase();
      try {
        const res = await liveStreamApi.watchTicket(employeeId);
        ticket = res.ticket;
        iceServersRef.current = toRtcIceServers(res.iceServers);
        if (res.watchBaseUrl) {
          // http(s)://host → ws(s)://host for the watch WebSocket.
          watchBase = res.watchBaseUrl
            .replace(/^http:\/\//i, 'ws://')
            .replace(/^https:\/\//i, 'wss://')
            .replace(/\/$/, '');
        }
      } catch (err) {
        if (cancelled) return;
        const msg = err instanceof Error ? err.message : 'ticket_failed';
        const detail = err instanceof ApiError ? `${err.message} ${err.detail ?? ''}` : msg;
        const consent = /consent_required/i.test(detail);
        setState((s) => ({
          ...s,
          status: consent ? 'consentMissing' : 'error',
          consentMissing: consent,
          error: msg,
          streaming: false,
        }));
        if (!consent) {
          attempt += 1;
          const delay = Math.min(15_000, 1500 * Math.pow(2, Math.min(attempt, 4)));
          reconnectTimer = setTimeout(() => {
            void connect();
          }, delay);
        }
        return;
      }

      if (cancelled) return;

      const url =
        `${watchBase}/api/v1/live-stream/watch` +
        `?employeeId=${encodeURIComponent(employeeId)}` +
        `&ticket=${encodeURIComponent(ticket)}`;
      const ws = new WebSocket(url);
      wsRef.current = ws;

      ws.onopen = () => {
        attempt = 0;
        if (!cancelled) {
          setState((s) => ({ ...s, status: 'connecting', error: null }));
          // Small delay so the server can deliver `start` to the employee
          // before we offer — SFU then waits for the track and answers with media.
          if (openOfferTimer) clearTimeout(openOfferTimer);
          openOfferTimer = setTimeout(() => {
            openOfferTimer = null;
            void createOffer(ws, false);
          }, 300);
        }
      };

      ws.onmessage = async (ev) => {
        if (cancelled || typeof ev.data !== 'string') return;
        try {
          const msg = JSON.parse(ev.data) as {
            type?: string;
            sdp?: string;
            candidate?: string;
            sdpMid?: string;
            sdpMLineIndex?: number;
            streaming?: boolean;
            streamAvailable?: boolean;
            clientConnected?: boolean;
            consentMissing?: boolean;
            code?: string;
            monitors?: LiveStreamMonitor[];
            selectedMonitor?: number;
            iceServers?: IceServerConfig[];
          };

          if (msg.type === 'ice_servers' && msg.iceServers) {
            iceServersRef.current = toRtcIceServers(msg.iceServers);
            return;
          }

          if (msg.type === 'status') {
            setState((s) => ({
              ...s,
              streaming: liveRef.current || !!msg.streaming,
              streamAvailable: !!msg.streamAvailable,
              clientConnected: !!msg.clientConnected,
              consentMissing: !!msg.consentMissing,
              monitors: Array.isArray(msg.monitors) ? msg.monitors : s.monitors,
              selectedMonitor:
                typeof msg.selectedMonitor === 'number' ? msg.selectedMonitor : s.selectedMonitor,
              status: msg.consentMissing
                ? 'consentMissing'
                : liveRef.current
                  ? 'live'
                  : !msg.clientConnected
                    ? 'connecting'
                    : msg.streamAvailable === false
                      ? 'unavailable'
                      : 'connecting',
            }));
            // Do NOT renegotiate on status — that used to fire every 2s and
            // tore down working PeerConnections (blank / pixelated / stuck).
            return;
          }

          if (msg.type === 'track_ready') {
            // Only renegotiate if the first answer had no media (not live yet).
            if (!liveRef.current && !makingOffer.current) {
              void createOffer(ws, true);
            }
            return;
          }

          if (msg.type === 'answer' && msg.sdp) {
            const entry = pcStateRef.current;
            if (!entry || entry.remoteReady) return;
            if (entry.pc.signalingState !== 'have-local-offer') return;
            try {
              await entry.pc.setRemoteDescription({ type: 'answer', sdp: msg.sdp });
            } catch (err) {
              console.warn('[live-stream] setRemoteDescription failed', err);
              return;
            }
            entry.remoteReady = true;
            // Flush every candidate that trickled in before the answer landed.
            const queued = entry.queue;
            entry.queue = [];
            for (const c of queued) {
              void entry.pc.addIceCandidate(c).catch(() => undefined);
            }
            return;
          }

          if (msg.type === 'ice' && msg.candidate) {
            const entry = pcStateRef.current;
            if (!entry) return;
            const init: RTCIceCandidateInit = {
              candidate: msg.candidate,
              sdpMid: msg.sdpMid ?? undefined,
              sdpMLineIndex: msg.sdpMLineIndex,
            };
            if (!entry.remoteReady) {
              entry.queue.push(init);
              return;
            }
            void entry.pc.addIceCandidate(init).catch(() => undefined);
            return;
          }

          if (msg.type === 'error') {
            const code = msg.code || 'error';
            // Never treat offer/ICE failures as consent — that was a false UI state.
            const consent = code === 'consent_required';
            setState((s) => ({
              ...s,
              status: consent ? 'consentMissing' : 'error',
              error: code,
              consentMissing: consent,
            }));
          }
        } catch {
          /* ignore */
        }
      };

      ws.onerror = () => {
        if (!cancelled) {
          setState((s) => ({ ...s, status: 'error', error: 'WebSocket error' }));
        }
      };

      ws.onclose = () => {
        wsRef.current = null;
        cleanupPc();
        liveRef.current = false;
        if (cancelled) return;
        attempt += 1;
        const delay = Math.min(15_000, 1500 * Math.pow(2, Math.min(attempt, 4)));
        setState((s) => ({
          ...s,
          streaming: false,
          status: s.status === 'consentMissing' ? 'consentMissing' : 'connecting',
        }));
        reconnectTimer = setTimeout(() => {
          void connect();
        }, delay);
      };
    };

    void connect();

    return () => {
      cancelled = true;
      if (reconnectTimer) clearTimeout(reconnectTimer);
      if (openOfferTimer) clearTimeout(openOfferTimer);
      if (wsRef.current) {
        wsRef.current.close();
        wsRef.current = null;
      }
      cleanupPc();
      liveRef.current = false;
    };
  }, [employeeId, enabled]);

  return { ...state, selectMonitor };
}
