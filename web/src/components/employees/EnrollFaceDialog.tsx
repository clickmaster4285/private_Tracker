'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Camera, Check, Loader2, RotateCcw, ScanFace, Trash2, Upload } from 'lucide-react';
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
      setSamples((prev) => {
        prev.filter((s) => s.poseId === sample.poseId).forEach((s) => URL.revokeObjectURL(s.previewUrl));
        const next = [...prev.filter((s) => s.poseId !== sample.poseId), sample];
        if (retryMode) {
          const stillNeeded = FACE_POSES.findIndex(
            (p) => rejectedPoseIds.has(p.id) && !next.some((x) => x.poseId === p.id),
          );
          if (stillNeeded >= 0) setPoseIndex(stillNeeded);
        } else if (poseIndex < FACE_POSES.length - 1) {
          setPoseIndex((i) => i + 1);
        }
        return next;
      });
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

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="bg-card sm:max-w-2xl w-[calc(100vw-1.5rem)] max-h-[min(92vh,900px)] p-0 gap-0 flex flex-col overflow-hidden">
        <div className="shrink-0 border-b border-border px-6 pt-6 pb-3 pr-12">
          <DialogHeader>
            <DialogTitle className="font-display flex items-center gap-2">
              <ScanFace className="w-5 h-5 text-primary" />
              Face enrollment{employee ? ` — ${employee.name}` : ''}
            </DialogTitle>
          </DialogHeader>
          <p className="text-xs text-muted-foreground mt-2 text-left">
            {phase === 'review'
              ? 'Server is checking each image. Verified angles stay; Retry means retake that pose only.'
              : retryMode
                ? 'Retake only the red Retry angles, then upload replacements. Verified images stay.'
                : <>
                    Follow each pose cue, then press{' '}
                    <span className="text-foreground font-medium">Capture this angle</span>. Upload after{' '}
                    {FACE_ENROLL_MIN_SAMPLES} angles (all {FACE_POSES.length} recommended). Originals go to the
                    server; the server face_worker verifies faces.
                  </>}
          </p>
        </div>

        <div className="min-h-0 flex-1 overflow-y-auto overscroll-contain px-6 py-4 space-y-4 [scrollbar-gutter:stable]">
          {phase === 'capture' && (
            <>
              <div className="relative rounded-xl overflow-hidden bg-black aspect-video max-h-[min(42vh,360px)] mx-auto w-full">
                <video ref={videoRef} className="w-full h-full object-cover scale-x-[-1]" playsInline muted />
                <div
                  className={`pointer-events-none absolute left-1/2 top-[48%] -translate-x-1/2 -translate-y-1/2
                    w-[46%] max-w-[200px] aspect-[3/4] rounded-[50%] border-2
                    ${qualityOk ? 'border-primary/70' : 'border-warning/80'}`}
                  aria-hidden
                />
                <div className="absolute bottom-2 left-2 right-2 flex items-end justify-between gap-2">
                  <div className="min-w-0 space-y-1">
                    <p className="text-[11px] uppercase tracking-wide text-white/70 bg-black/55 inline-block px-2 py-0.5 rounded">
                      Step {poseIndex + 1} of {FACE_POSES.length}: {pose.shortLabel}
                      {retryMode ? ' · retake' : ''}
                    </p>
                    <p className="text-sm font-medium text-white bg-black/65 px-2.5 py-1 rounded-md max-w-full truncate">
                      {liveHint}
                    </p>
                  </div>
                  <span className="shrink-0 text-xs bg-black/65 text-white px-2 py-1 rounded-md tabular-nums">
                    {retryMode
                      ? `${samples.length}/${rejectedServer.length || rejectedPoseIds.size} retakes`
                      : `${samples.length}/${FACE_POSES.length} saved`}
                  </span>
                </div>
              </div>

              <div className="flex flex-wrap gap-1.5" role="list" aria-label="Enrollment angles">
                {FACE_POSES.map((p, i) => {
                  const done = capturedByPose.has(p.id);
                  const active = i === poseIndex;
                  const needsRetry = retryMode && rejectedPoseIds.has(p.id);
                  const lockedOk = retryMode && !needsRetry;
                  return (
                    <button
                      key={p.id}
                      type="button"
                      role="listitem"
                      disabled={lockedOk}
                      onClick={() => {
                        if (!lockedOk) setPoseIndex(i);
                      }}
                      title={p.label}
                      className={`inline-flex items-center gap-1 text-xs px-2.5 py-1.5 rounded-md border transition-colors ${
                        lockedOk
                          ? 'border-success/40 bg-success/10 text-success opacity-70 cursor-default'
                          : active
                            ? 'border-primary bg-primary/15 text-foreground'
                            : done
                              ? 'border-success/50 bg-success/10 text-success'
                              : needsRetry
                                ? 'border-destructive/60 text-destructive'
                                : 'border-border text-muted-foreground hover:text-foreground'
                      }`}
                    >
                      {done || lockedOk ? <Check className="w-3 h-3 shrink-0" aria-hidden /> : null}
                      {p.shortLabel}
                    </button>
                  );
                })}
              </div>

              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <div className="flex flex-wrap items-center gap-2">
                  <button
                    type="button"
                    onClick={() => void capture()}
                    disabled={busy || submitting || (retryMode && !rejectedPoseIds.has(pose.id))}
                    className="inline-flex items-center gap-2 px-3 py-2 rounded-lg bg-primary text-primary-foreground text-sm font-medium disabled:opacity-50"
                  >
                    {busy ? <Loader2 className="w-4 h-4 animate-spin" /> : <Camera className="w-4 h-4" />}
                    Capture this angle
                  </button>
                  <button
                    type="button"
                    onClick={() => void startCamera()}
                    className="inline-flex items-center gap-2 px-3 py-2 rounded-lg border border-border text-sm"
                  >
                    <RotateCcw className="w-4 h-4" />
                    Restart camera
                  </button>
                </div>
                <button
                  type="button"
                  onClick={() => void submit()}
                  disabled={!canUpload}
                  className="inline-flex items-center justify-center gap-2 px-3 py-2 rounded-lg bg-foreground text-background text-sm font-medium disabled:opacity-45 sm:min-w-[11rem]"
                >
                  {submitting ? <Loader2 className="w-4 h-4 animate-spin" /> : <Upload className="w-4 h-4" />}
                  {uploadLabel}
                </button>
              </div>

              {!retryMode && (
                <div className="grid grid-cols-4 gap-2 pb-1">
                  {FACE_POSES.map((p) => {
                    const s = capturedByPose.get(p.id);
                    return (
                      <div
                        key={p.id}
                        className={`relative rounded-lg overflow-hidden border aspect-square ${
                          s ? 'border-border bg-muted/30' : 'border-dashed border-border/70 bg-muted/15'
                        }`}
                      >
                        {s ? (
                          <>
                            {/* eslint-disable-next-line @next/next/no-img-element */}
                            <img
                              src={s.previewUrl}
                              alt={p.shortLabel}
                              className="w-full h-full object-cover scale-x-[-1]"
                            />
                            <div className="absolute inset-x-0 bottom-0 bg-black/70 text-[11px] text-white px-1.5 py-1 flex items-center justify-between gap-1">
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
                            onClick={() => setPoseIndex(FACE_POSES.findIndex((x) => x.id === p.id))}
                            className="absolute inset-0 flex flex-col items-center justify-center gap-1 text-muted-foreground hover:text-foreground hover:bg-muted/25"
                            aria-label={`Go to ${p.shortLabel}`}
                          >
                            <span className="text-[11px] font-medium px-1 text-center leading-tight">
                              {p.shortLabel}
                            </span>
                            <span className="text-[10px] opacity-70">Not captured</span>
                          </button>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}

              {retryMode && rejectedServer.length > 0 && (
                <div className="grid grid-cols-4 gap-2 pb-1">
                  {rejectedServer.map((s) => {
                    const local = capturedByPose.get(s.poseId);
                    const img =
                      local?.previewUrl ||
                      s.imageUrl ||
                      (employeeKey ? employeesApi.faceSampleImagePath(employeeKey, s.id) : '');
                    return (
                      <div key={s.id} className="relative rounded-lg overflow-hidden border border-destructive/50 aspect-square">
                        {img ? (
                          // eslint-disable-next-line @next/next/no-img-element
                          <img src={img} alt={poseShortLabel(s.poseId)} className="w-full h-full object-cover scale-x-[-1]" />
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
            </>
          )}

          {phase === 'review' && (
            <>
              <div className="flex items-center justify-between gap-2 text-sm">
                <p>
                  Server job: <span className="font-medium">{job?.status ?? '…'}</span>
                  {job?.sampleCount != null ? ` · ${job.sampleCount} images` : null}
                  {job?.rejectedSamples ? ` · ${job.rejectedSamples} rejected` : null}
                </p>
                {processing && (
                  <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground">
                    <Loader2 className="w-3.5 h-3.5 animate-spin" />
                    Checking one-by-one…
                  </span>
                )}
              </div>

              <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
                {serverSamples.map((s) => {
                  const badge = statusBadge(s);
                  const src =
                    s.imageUrl ||
                    (employeeKey ? employeesApi.faceSampleImagePath(employeeKey, s.id) : '');
                  return (
                    <div key={s.id} className="relative rounded-lg overflow-hidden border border-border aspect-square bg-muted/20">
                      {src ? (
                        // eslint-disable-next-line @next/next/no-img-element
                        <img src={src} alt={poseShortLabel(s.poseId)} className="w-full h-full object-cover scale-x-[-1]" />
                      ) : null}
                      <span className={`absolute top-1.5 left-1.5 text-[10px] font-medium px-1.5 py-0.5 rounded ${badge.className}`}>
                        {badge.label}
                      </span>
                      <div className="absolute inset-x-0 bottom-0 bg-black/70 text-[11px] text-white px-1.5 py-1">
                        <div className="font-medium truncate">{poseShortLabel(s.poseId)}</div>
                        {s.processStatus === 'rejected' ? (
                          <div className="text-[10px] text-red-200 truncate">{rejectLabel(s)}</div>
                        ) : null}
                      </div>
                    </div>
                  );
                })}
              </div>

              {job?.errorMessage ? (
                <p className="text-xs text-warning">{job.errorMessage}</p>
              ) : null}

              {rejectedServer.length > 0 && !processing && (
                <button
                  type="button"
                  onClick={() => enterRetryCapture(rejectedServer)}
                  className="inline-flex items-center gap-2 px-3 py-2 rounded-lg bg-primary text-primary-foreground text-sm font-medium"
                >
                  <Camera className="w-4 h-4" />
                  Retake rejected angles
                </button>
              )}
            </>
          )}
        </div>
      </DialogContent>
    </Dialog>
  );
}
