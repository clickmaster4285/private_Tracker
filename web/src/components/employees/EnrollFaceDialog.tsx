'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Camera, Loader2, RotateCcw, ScanFace, Trash2, Upload } from 'lucide-react';
import { toast } from 'sonner';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  employeesApi,
  type Employee,
  type FaceEnrollmentJob,
  type FaceEnrollmentSample,
} from '@/lib/api';
import {
  FACE_ENROLL_MIN_SAMPLES,
  FACE_POSES,
  captureFromVideo,
  poseShortLabel,
  type CapturedFaceSample,
  type FacePoseId,
  assessFrame,
} from '@/lib/face-enrollment';

type Props = {
  employee: Employee | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onCompleted?: () => void;
};

type Phase = 'capture' | 'review';

function statusBadge(sample: FaceEnrollmentSample): { label: string; className: string } {
  switch (sample.processStatus) {
    case 'validated':
    case 'embedded':
      return { label: 'Verified', className: 'bg-success/90 text-white' };
    case 'rejected':
      return { label: 'Retry', className: 'bg-destructive text-destructive-foreground' };
    case 'processing':
      return { label: 'Checking…', className: 'bg-primary/90 text-primary-foreground' };
    default:
      return { label: 'Checking…', className: 'bg-muted text-muted-foreground' };
  }
}

function rejectLabel(sample: FaceEnrollmentSample): string {
  if (sample.rejectReason === 'multiple_faces') {
    return `multiple faces (${sample.faceCount ?? '?'})`;
  }
  if (sample.rejectReason === 'no_face') return 'no face detected';
  if (sample.rejectReason === 'too_far') return 'too far — sit 50–70 cm from camera';
  if (sample.rejectReason === 'too_close') return 'too close — sit 50–70 cm from camera';
  return sample.rejectReason || 'rejected';
}

export function EnrollFaceDialog({ employee, open, onOpenChange, onCompleted }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const streamRef = useRef<MediaStream | null>(null);
  const pollAbortRef = useRef<AbortController | null>(null);
  const [poseIndex, setPoseIndex] = useState(0);
  const [samples, setSamples] = useState<CapturedFaceSample[]>([]);
  const [liveHint, setLiveHint] = useState('Starting camera…');
  const [qualityOk, setQualityOk] = useState(true);
  const [busy, setBusy] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [job, setJob] = useState<FaceEnrollmentJob | null>(null);
  const [serverSamples, setServerSamples] = useState<FaceEnrollmentSample[]>([]);
  const [phase, setPhase] = useState<Phase>('capture');
  const [retryMode, setRetryMode] = useState(false);
  const [processing, setProcessing] = useState(false);

  const pose = FACE_POSES[poseIndex] ?? FACE_POSES[0];
  const capturedByPose = useMemo(() => {
    const map = new Map<string, CapturedFaceSample>();
    for (const s of samples) map.set(s.poseId, s);
    return map;
  }, [samples]);

  const rejectedServer = useMemo(
    () => serverSamples.filter((s) => s.processStatus === 'rejected'),
    [serverSamples],
  );
  const rejectedPoseIds = useMemo(() => new Set(rejectedServer.map((s) => s.poseId)), [rejectedServer]);

  const remainingForUpload = Math.max(0, FACE_ENROLL_MIN_SAMPLES - samples.length);
  const canUpload =
    !submitting &&
    (retryMode
      ? samples.length > 0 && samples.every((s) => rejectedPoseIds.has(s.poseId))
      : samples.length >= FACE_ENROLL_MIN_SAMPLES);

  const stopCamera = useCallback(() => {
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
    if (videoRef.current) videoRef.current.srcObject = null;
  }, []);

  const startCamera = useCallback(async () => {
    stopCamera();
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } },
        audio: false,
      });
      streamRef.current = stream;
      if (videoRef.current) {
        videoRef.current.srcObject = stream;
        await videoRef.current.play();
      }
      setLiveHint(pose.label);
      setQualityOk(true);
    } catch {
      setLiveHint('Camera unavailable — allow webcam access');
      setQualityOk(false);
      toast.error('Could not open webcam');
    }
  }, [stopCamera, pose.label]);

  const resetLocal = useCallback(() => {
    samples.forEach((s) => URL.revokeObjectURL(s.previewUrl));
    setSamples([]);
    setPoseIndex(0);
    pollAbortRef.current?.abort();
    pollAbortRef.current = null;
    setJob(null);
    setServerSamples([]);
    setBusy(false);
    setSubmitting(false);
    setPhase('capture');
    setRetryMode(false);
    setProcessing(false);
    setQualityOk(true);
  }, [samples]);

  useEffect(() => {
    if (!open) {
      stopCamera();
      resetLocal();
      return;
    }
    void startCamera();
    return () => {
      stopCamera();
      pollAbortRef.current?.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps -- reset only on open/close
  }, [open]);

  useEffect(() => {
    if (!open || phase !== 'capture' || !videoRef.current) return;
    const video = videoRef.current;
    const canvas = document.createElement('canvas');
    const id = window.setInterval(() => {
      if (!video.videoWidth) return;
      canvas.width = Math.min(320, video.videoWidth);
      canvas.height = Math.round((canvas.width / video.videoWidth) * video.videoHeight);
      const ctx = canvas.getContext('2d', { willReadFrequently: true });
      if (!ctx) return;
      ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
      const q = assessFrame(ctx.getImageData(0, 0, canvas.width, canvas.height));
      setQualityOk(q.ok);
      if (!q.ok) setLiveHint(q.reason ?? 'Adjust lighting or hold still');
      else setLiveHint(pose.label);
    }, 400);
    return () => window.clearInterval(id);
  }, [open, pose.label, phase]);

  const enterRetryCapture = useCallback(
    (rejected: FaceEnrollmentSample[]) => {
      if (rejected.length === 0) return;
      samples.forEach((s) => URL.revokeObjectURL(s.previewUrl));
      setSamples([]);
      setRetryMode(true);
      setPhase('capture');
      setProcessing(false);
      const first = rejected[0]?.poseId;
      const idx = FACE_POSES.findIndex((p) => p.id === first);
      if (idx >= 0) setPoseIndex(idx);
      const labels = rejected.map((s) => `${poseShortLabel(s.poseId)} (${rejectLabel(s)})`);
      toast.error(`Retake: ${labels.join(', ')}`);
      void startCamera();
    },
    [samples, startCamera],
  );

  const pollJobUntilDone = useCallback(
    async (employeeId: string, jobId: string, signal: AbortSignal) => {
      setProcessing(true);
      setPhase('review');
      for (let i = 0; i < 90 && !signal.aborted; i++) {
        await new Promise((r) => setTimeout(r, 1200));
        if (signal.aborted) return;
        try {
          const status = await employeesApi.getFaceEnrollmentJob(employeeId, jobId);
          setJob(status.job);
          setServerSamples(status.samples);

          if (['succeeded', 'failed', 'needs_more_samples'].includes(status.job.status)) {
            setProcessing(false);
            const rejected = status.samples.filter((s) => s.processStatus === 'rejected');
            if (status.job.status === 'succeeded') {
              if (status.job.errorCode === 'embeddings_deferred') {
                toast.success('All angles verified. Embeddings wait on AdaFace ONNX setup.');
              } else {
                toast.success('Face enrollment completed');
              }
              onCompleted?.();
            } else if (status.job.status === 'needs_more_samples' || rejected.length > 0) {
              toast.message(status.job.errorMessage || 'Some images need a retake');
              enterRetryCapture(rejected);
            } else if (status.job.errorMessage) {
              toast.error(status.job.errorMessage);
            }
            return;
          }
        } catch {
          // keep polling unless aborted
        }
      }
      setProcessing(false);
    },
    [enterRetryCapture, onCompleted],
  );

  const capture = async () => {
    if (!videoRef.current || busy) return;
    if (retryMode && !rejectedPoseIds.has(pose.id)) {
      toast.message('Only retake rejected angles');
      return;
    }
    setBusy(true);
    try {
      const { sample, quality } = await captureFromVideo(videoRef.current, pose.id as FacePoseId);
      if (!sample) {
        toast.error(quality.reason ?? 'Capture rejected');
        return;
      }
      // Advance pose outside setSamples — React Strict Mode can run updaters twice,
      // which made setPoseIndex(i => i + 1) skip every other angle (1→3→5…).
      const capturedAt = poseIndex;
      let nextSamples: CapturedFaceSample[] = [];
      setSamples((prev) => {
        prev.filter((s) => s.poseId === sample.poseId).forEach((s) => URL.revokeObjectURL(s.previewUrl));
        nextSamples = [...prev.filter((s) => s.poseId !== sample.poseId), sample];
        return nextSamples;
      });
      if (retryMode) {
        const stillNeeded = FACE_POSES.findIndex(
          (p) => rejectedPoseIds.has(p.id) && !nextSamples.some((x) => x.poseId === p.id),
        );
        if (stillNeeded >= 0) setPoseIndex(stillNeeded);
      } else if (capturedAt < FACE_POSES.length - 1) {
        setPoseIndex(capturedAt + 1);
      }
      toast.success(`Saved ${pose.shortLabel}`);
    } finally {
      setBusy(false);
    }
  };

  const removeSample = (poseId: string) => {
    setSamples((prev) => {
      const hit = prev.find((s) => s.poseId === poseId);
      if (hit) URL.revokeObjectURL(hit.previewUrl);
      return prev.filter((s) => s.poseId !== poseId);
    });
    const idx = FACE_POSES.findIndex((p) => p.id === poseId);
    if (idx >= 0) setPoseIndex(idx);
  };

  const submit = async () => {
    if (!employee) return;
    if (retryMode) {
      if (!job || samples.length === 0) {
        toast.error('Capture replacements for rejected angles');
        return;
      }
      setSubmitting(true);
      pollAbortRef.current?.abort();
      const ac = new AbortController();
      pollAbortRef.current = ac;
      try {
        const form = new FormData();
        for (const s of samples) {
          form.append('files', s.blob, `${s.poseId}.jpg`);
          form.append('poseId', s.poseId);
          form.append('clientSampleId', s.clientSampleId);
          form.append('qualityScore', String(s.qualityScore));
        }
        const updated = await employeesApi.retryFaceEnrollment(employee.id, job.id, form);
        samples.forEach((s) => URL.revokeObjectURL(s.previewUrl));
        setSamples([]);
        setRetryMode(false);
        setJob(updated.job);
        setServerSamples(updated.samples);
        setPhase('review');
        toast.success(`Re-uploaded ${updated.samples.filter((s) => s.processStatus === 'uploaded').length || samples.length} image(s)`);
        setSubmitting(false);
        void pollJobUntilDone(employee.id, updated.job.id, ac.signal);
      } catch (e) {
        toast.error(e instanceof Error ? e.message : 'Retry upload failed');
        setSubmitting(false);
      }
      return;
    }

    if (samples.length < FACE_ENROLL_MIN_SAMPLES) {
      toast.error(`Capture at least ${FACE_ENROLL_MIN_SAMPLES} angles before uploading`);
      return;
    }
    setSubmitting(true);
    pollAbortRef.current?.abort();
    const ac = new AbortController();
    pollAbortRef.current = ac;
    try {
      const form = new FormData();
      for (const s of samples) {
        form.append('files', s.blob, `${s.poseId}.jpg`);
        form.append('poseId', s.poseId);
        form.append('clientSampleId', s.clientSampleId);
        form.append('qualityScore', String(s.qualityScore));
      }
      const created = await employeesApi.createFaceEnrollment(employee.id, form);
      samples.forEach((s) => URL.revokeObjectURL(s.previewUrl));
      setSamples([]);
      setJob(created.job);
      setServerSamples(created.samples);
      setPhase('review');
      toast.success(`Saved ${created.samples.length} images on server`);
      setSubmitting(false);
      void pollJobUntilDone(employee.id, created.job.id, ac.signal);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Enrollment upload failed');
      setSubmitting(false);
    }
  };

  const uploadLabel = submitting
    ? retryMode
      ? 'Uploading retakes…'
      : 'Saving images…'
    : retryMode
      ? samples.length === 0
        ? 'Retake rejected angles'
        : `Upload ${samples.length} retake${samples.length === 1 ? '' : 's'}`
      : remainingForUpload > 0
        ? `Upload (need ${remainingForUpload} more)`
        : samples.length < FACE_POSES.length
          ? `Upload ${samples.length} angles`
          : `Upload all ${samples.length} angles`;

  const employeeKey = employee?.employeeId || employee?.id || '';
  const progressLabel = retryMode
    ? `${samples.length}/${rejectedServer.length || rejectedPoseIds.size} retakes`
    : `${samples.length}/${FACE_POSES.length} saved`;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent
        className="bg-card w-[90vw] h-[90vh] max-w-none p-0 gap-0 flex flex-col overflow-hidden sm:rounded-xl"
        onPointerDownOutside={(e) => e.preventDefault()}
        onInteractOutside={(e) => e.preventDefault()}
      >
        <div className="shrink-0 border-b border-border px-5 pt-5 pb-3 pr-12">
          <DialogHeader>
            <DialogTitle className="font-display flex items-center gap-2 text-base sm:text-lg">
              <ScanFace className="w-5 h-5 text-primary" />
              Face enrollment{employee ? ` — ${employee.name}` : ''}
            </DialogTitle>
          </DialogHeader>
          <p className="text-xs text-muted-foreground mt-1.5 text-left leading-relaxed">
            {phase === 'review'
              ? 'Server is checking each image. Verified angles stay; Retry means retake that pose only.'
              : retryMode
                ? 'Retake only the red Retry angles, then upload replacements. Verified images stay.'
                : <>
                    Sit <span className="text-foreground font-medium">50–70 cm</span> from the
                    camera. Follow the pose cue, then{' '}
                    <span className="text-foreground font-medium">Capture</span>. Upload after{' '}
                    {FACE_ENROLL_MIN_SAMPLES}+ angles (all {FACE_POSES.length} recommended).
                  </>}
          </p>
        </div>

        {phase === 'capture' && (
          <div className="min-h-0 flex-1 grid grid-cols-1 lg:grid-cols-[minmax(280px,0.38fr)_1fr] overflow-hidden">
            {/* Left rail — all controls */}
            <aside className="min-h-0 flex flex-col border-b lg:border-b-0 lg:border-r border-border bg-muted/20 overflow-hidden">
              <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 py-4 space-y-4 [scrollbar-gutter:stable]">
                <div className="flex items-center justify-between gap-2">
                  <p className="text-[11px] uppercase tracking-wider text-muted-foreground font-medium">
                    Captures
                  </p>
                  <span className="text-xs tabular-nums text-foreground/80 bg-background border border-border px-2 py-0.5 rounded-md">
                    {progressLabel}
                  </span>
                </div>

                {!retryMode && (
                  <div className="grid grid-cols-3 sm:grid-cols-4 lg:grid-cols-2 xl:grid-cols-3 gap-2" role="list" aria-label="Capture steps">
                    {FACE_POSES.map((p, i) => {
                      const s = capturedByPose.get(p.id);
                      const selected = i === poseIndex;
                      return (
                        <div
                          key={p.id}
                          role="listitem"
                          aria-current={selected ? 'step' : undefined}
                          className={`relative rounded-lg overflow-hidden border aspect-square transition-[box-shadow,border-color,background-color] ${
                            selected
                              ? 'border-primary border-2 bg-primary/10 ring-2 ring-primary/45 shadow-[0_0_18px_rgba(59,130,246,0.45)] z-[1]'
                              : s
                                ? 'border-border bg-muted/30'
                                : 'border-dashed border-border/70 bg-muted/15'
                          }`}
                        >
                          {s ? (
                            <>
                              <button
                                type="button"
                                onClick={() => setPoseIndex(i)}
                                className="absolute inset-0 z-0"
                                aria-label={`Select ${p.shortLabel} step`}
                              >
                                {/* eslint-disable-next-line @next/next/no-img-element */}
                                <img
                                  src={s.previewUrl}
                                  alt={p.shortLabel}
                                  className="w-full h-full object-cover scale-x-[-1]"
                                />
                              </button>
                              <div className="absolute inset-x-0 bottom-0 z-[1] bg-black/70 text-[10px] text-white px-1.5 py-1 flex items-center justify-between gap-1">
                                <span className="truncate font-medium">{p.shortLabel}</span>
                                <button
                                  type="button"
                                  onClick={() => removeSample(p.id)}
                                  className="shrink-0 p-0.5 rounded hover:bg-white/15"
                                  aria-label={`Remove ${p.shortLabel} and retake`}
                                >
                                  <Trash2 className="w-3.5 h-3.5" />
                                </button>
                              </div>
                            </>
                          ) : (
                            <button
                              type="button"
                              onClick={() => setPoseIndex(i)}
                              className={`absolute inset-0 flex flex-col items-center justify-center gap-0.5 hover:bg-muted/25 ${
                                selected ? 'text-primary font-semibold' : 'text-muted-foreground hover:text-foreground'
                              }`}
                              aria-label={`Go to ${p.shortLabel}`}
                            >
                              <span className="text-[10px] font-medium px-1 text-center leading-tight">
                                {p.shortLabel}
                              </span>
                              {selected ? (
                                <span className="text-[9px] uppercase tracking-wide text-primary/90">Current</span>
                              ) : null}
                            </button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}

                {retryMode && rejectedServer.length > 0 && (
                  <div className="grid grid-cols-2 gap-2" role="list" aria-label="Retry steps">
                    {rejectedServer.map((s) => {
                      const local = capturedByPose.get(s.poseId);
                      const selected = pose.id === s.poseId;
                      const img =
                        local?.previewUrl ||
                        s.imageUrl ||
                        (employeeKey ? employeesApi.faceSampleImagePath(employeeKey, s.id) : '');
                      return (
                        <div
                          key={s.id}
                          role="listitem"
                          aria-current={selected ? 'step' : undefined}
                          className={`relative rounded-lg overflow-hidden border aspect-square transition-[box-shadow,border-color] ${
                            selected
                              ? 'border-primary border-2 ring-2 ring-primary/45 shadow-[0_0_18px_rgba(59,130,246,0.45)] z-[1]'
                              : 'border-destructive/50'
                          }`}
                        >
                          {img ? (
                            // eslint-disable-next-line @next/next/no-img-element
                            <img
                              src={img}
                              alt={poseShortLabel(s.poseId)}
                              className="w-full h-full object-cover scale-x-[-1]"
                            />
                          ) : null}
                          <div className="absolute inset-x-0 top-0 bg-destructive/90 text-[10px] text-white px-1.5 py-0.5">
                            Retry · {poseShortLabel(s.poseId)}
                          </div>
                          {local ? (
                            <div className="absolute inset-x-0 bottom-0 bg-black/70 text-[10px] text-white px-1.5 py-1">
                              New capture ready
                            </div>
                          ) : (
                            <button
                              type="button"
                              className="absolute inset-0 bg-black/20"
                              onClick={() => {
                                const idx = FACE_POSES.findIndex((p) => p.id === s.poseId);
                                if (idx >= 0) setPoseIndex(idx);
                              }}
                              aria-label={`Retake ${poseShortLabel(s.poseId)}`}
                            />
                          )}
                        </div>
                      );
                    })}
                  </div>
                )}

                <div className="rounded-lg border border-border/80 bg-background/70 px-3 py-2.5 space-y-1">
                  <p className="text-[11px] uppercase tracking-wider text-muted-foreground">Live cue</p>
                  <p className={`text-sm font-medium leading-snug ${qualityOk ? 'text-foreground' : 'text-warning'}`}>
                    {liveHint}
                  </p>
                </div>

                <div className="flex flex-col gap-2">
                  <button
                    type="button"
                    onClick={() => void capture()}
                    disabled={busy || submitting || (retryMode && !rejectedPoseIds.has(pose.id))}
                    className="inline-flex items-center justify-center gap-2 px-3 py-2.5 rounded-lg bg-primary text-primary-foreground text-sm font-medium disabled:opacity-50"
                  >
                    {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : <Camera className="w-4 h-4" />}
                    Capture this angle
                  </button>
                  <button
                    type="button"
                    onClick={() => void startCamera()}
                    className="inline-flex items-center justify-center gap-2 px-3 py-2.5 rounded-lg border border-border bg-background text-sm hover:bg-muted/40"
                  >
                    <RotateCcw className="w-4 h-4" />
                    Restart camera
                  </button>
                  <button
                    type="button"
                    onClick={() => void submit()}
                    disabled={!canUpload}
                    className="inline-flex items-center justify-center gap-2 px-3 py-2.5 rounded-lg bg-foreground text-background text-sm font-medium disabled:opacity-45"
                  >
                    {submitting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
                    {uploadLabel}
                  </button>
                </div>
              </div>
            </aside>

            {/* Right stage — camera only */}
            <section className="relative min-h-[42vh] lg:min-h-0 bg-black overflow-hidden">
              <video
                ref={videoRef}
                className="absolute inset-0 h-full w-full object-cover scale-x-[-1]"
                playsInline
                muted
              />
              <div
                className="pointer-events-none absolute inset-0 bg-[radial-gradient(ellipse_at_center,transparent_42%,rgba(0,0,0,0.55)_100%)]"
                aria-hidden
              />
              <div
                className={`pointer-events-none absolute left-1/2 top-[46%] -translate-x-1/2 -translate-y-1/2
                  w-[min(42%,280px)] aspect-[3/4] rounded-[50%] border-2 shadow-[0_0_0_9999px_rgba(0,0,0,0.28)]
                  transition-colors duration-300
                  ${qualityOk ? 'border-primary/80' : 'border-warning/90'}`}
                aria-hidden
              />
              <div className="absolute inset-x-0 bottom-0 p-4 sm:p-5 bg-gradient-to-t from-black/85 via-black/45 to-transparent">
                <div className="flex items-end justify-between gap-3">
                  <div className="min-w-0 space-y-1.5">
                    <p className="text-[11px] uppercase tracking-[0.14em] text-white/65">
                      Step {poseIndex + 1} of {FACE_POSES.length}
                      {retryMode ? ' · retake' : ''}
                    </p>
                    <p className="text-lg sm:text-xl font-semibold text-white tracking-tight truncate">
                      {pose.shortLabel}
                    </p>
                    <p className="text-sm text-white/85 max-w-xl truncate">{liveHint}</p>
                  </div>
                  <div className="shrink-0 text-right space-y-1">
                    <span
                      className={`inline-flex text-[11px] font-medium px-2 py-1 rounded-md border ${
                        qualityOk
                          ? 'border-emerald-400/40 bg-emerald-500/20 text-emerald-100'
                          : 'border-amber-400/40 bg-amber-500/20 text-amber-100'
                      }`}
                    >
                      {qualityOk ? 'Framing OK' : 'Adjust'}
                    </span>
                    <p className="text-xs text-white/80 tabular-nums">{progressLabel}</p>
                  </div>
                </div>
              </div>
            </section>
          </div>
        )}

        {phase === 'review' && (
          <div className="min-h-0 flex-1 grid grid-cols-1 lg:grid-cols-[minmax(260px,0.32fr)_1fr] overflow-hidden">
            <aside className="min-h-0 overflow-y-auto border-b lg:border-b-0 lg:border-r border-border bg-muted/20 px-4 py-4 space-y-4">
              <div className="space-y-1">
                <p className="text-[11px] uppercase tracking-wider text-muted-foreground font-medium">
                  Server job
                </p>
                <p className="text-sm">
                  <span className="font-medium capitalize">{job?.status ?? '…'}</span>
                  {job?.sampleCount != null ? ` · ${job.sampleCount} images` : null}
                  {job?.rejectedSamples ? ` · ${job.rejectedSamples} rejected` : null}
                </p>
                {processing && (
                  <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground pt-1">
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                    Checking one-by-one…
                  </span>
                )}
              </div>
              {job?.errorMessage ? (
                <p className="text-xs text-warning leading-relaxed">{job.errorMessage}</p>
              ) : null}
              {rejectedServer.length > 0 && !processing && (
                <button
                  type="button"
                  onClick={() => enterRetryCapture(rejectedServer)}
                  className="inline-flex w-full items-center justify-center gap-2 px-3 py-2.5 rounded-lg bg-primary text-primary-foreground text-sm font-medium"
                >
                  <Camera className="w-4 h-4" />
                  Retake rejected angles
                </button>
              )}
            </aside>
            <div className="min-h-0 overflow-y-auto p-4 sm:p-5">
              <div className="grid grid-cols-2 sm:grid-cols-3 xl:grid-cols-4 gap-3">
                {serverSamples.map((s) => {
                  const badge = statusBadge(s);
                  const src =
                    s.imageUrl ||
                    (employeeKey ? employeesApi.faceSampleImagePath(employeeKey, s.id) : '');
                  return (
                    <div
                      key={s.id}
                      className="relative rounded-xl overflow-hidden border border-border aspect-square bg-muted/20"
                    >
                      {src ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img
                          src={src}
                          alt={poseShortLabel(s.poseId)}
                          className="w-full h-full object-cover scale-x-[-1]"
                        />
                      ) : null}
                      <span
                        className={`absolute top-2 left-2 text-[10px] font-medium px-1.5 py-0.5 rounded ${badge.className}`}
                      >
                        {badge.label}
                      </span>
                      <div className="absolute inset-x-0 bottom-0 bg-black/70 text-[11px] text-white px-2 py-1.5">
                        <div className="font-medium truncate">{poseShortLabel(s.poseId)}</div>
                        {s.processStatus === 'rejected' ? (
                          <div className="text-[10px] text-red-200 truncate">{rejectLabel(s)}</div>
                        ) : null}
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}
