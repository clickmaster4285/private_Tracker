/** Multi-angle face enrollment helpers (browser capture only — no authoritative embeddings). */

export const FACE_POSES = [
  { id: 'front', shortLabel: 'Front', label: 'Look straight at the camera (sit 50–70 cm away)' },
  { id: 'left_slight', shortLabel: 'Slight left', label: 'Turn slightly left' },
  { id: 'left', shortLabel: 'Left', label: 'Turn further left' },
  { id: 'right_slight', shortLabel: 'Slight right', label: 'Turn slightly right' },
  { id: 'right', shortLabel: 'Right', label: 'Turn further right' },
  { id: 'up', shortLabel: 'Up', label: 'Look slightly upward' },
  { id: 'down', shortLabel: 'Down', label: 'Look slightly downward' },
  { id: 'front_return', shortLabel: 'Front again', label: 'Return to facing the camera' },
] as const;

/** Minimum quality-approved angles before upload is allowed. */
export const FACE_ENROLL_MIN_SAMPLES = 6;

export type FacePoseId = (typeof FACE_POSES)[number]['id'];

export function poseShortLabel(poseId: string): string {
  const hit = FACE_POSES.find((p) => p.id === poseId);
  return hit?.shortLabel ?? poseId.replace(/_/g, ' ');
}

export interface CapturedFaceSample {
  clientSampleId: string;
  poseId: FacePoseId;
  blob: Blob;
  previewUrl: string;
  width: number;
  height: number;
  /** Browser-side heuristic only (0–1). Server re-validates. */
  qualityScore: number;
}

export function newClientSampleId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
  return `sample-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

/** Laplacian-ish variance on a downscaled grayscale canvas — advisory blur score. */
export function estimateSharpness(imageData: ImageData): number {
  const { data, width, height } = imageData;
  if (width < 16 || height < 16) return 0;
  let sum = 0;
  let sumSq = 0;
  let n = 0;
  const gray = (i: number) => 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
  for (let y = 1; y < height - 1; y += 2) {
    for (let x = 1; x < width - 1; x += 2) {
      const i = (y * width + x) * 4;
      const lap =
        -gray(i - 4) - gray(i + 4) - gray(i - width * 4) - gray(i + width * 4) + 4 * gray(i);
      sum += lap;
      sumSq += lap * lap;
      n++;
    }
  }
  if (n === 0) return 0;
  const mean = sum / n;
  const variance = sumSq / n - mean * mean;
  // Map typical webcam variances into ~0–1 for UI.
  return Math.max(0, Math.min(1, variance / 500));
}

export function estimateBrightness(imageData: ImageData): number {
  const { data } = imageData;
  let sum = 0;
  const step = 16;
  let n = 0;
  for (let i = 0; i < data.length; i += 4 * step) {
    sum += 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
    n++;
  }
  return n === 0 ? 0 : sum / n / 255;
}

export interface FrameQuality {
  ok: boolean;
  sharpness: number;
  brightness: number;
  faceCount?: number;
  reason?: string;
}

export function assessFrame(imageData: ImageData): FrameQuality {
  const sharpness = estimateSharpness(imageData);
  const brightness = estimateBrightness(imageData);
  if (brightness < 0.15) return { ok: false, sharpness, brightness, reason: 'Too dark' };
  if (brightness > 0.92) return { ok: false, sharpness, brightness, reason: 'Overexposed' };
  if (sharpness < 0.12) return { ok: false, sharpness, brightness, reason: 'Too blurry' };
  return { ok: true, sharpness, brightness };
}

/** Virtual / phone-bridge drivers that often show a dual mirrored "start app" placeholder. */
export function isVirtualCameraLabel(label: string): boolean {
  const l = label.toLowerCase();
  return /iriun|obs\s*virtual|virtual\s*camera|manycam|droidcam|epoccam|snap\s*camera|nvidia\s*broadcast|xsplit|unity\s*capture|ivcam|ndi/.test(
    l,
  );
}

/**
 * Iriun / virtual "no signal" frames are nearly black with crisp white placeholder text.
 * Prefer this over showing the driver's dual mirrored string in the enroll oval.
 */
export function looksLikeNoSignalFrame(imageData: ImageData): boolean {
  const brightness = estimateBrightness(imageData);
  if (brightness > 0.22) return false;
  if (brightness < 0.04) return true;
  // Mostly dark with a thin band of bright pixels (placeholder text).
  const { data } = imageData;
  let bright = 0;
  let n = 0;
  const step = 8;
  for (let i = 0; i < data.length; i += 4 * step) {
    const y = 0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2];
    if (y > 200) bright++;
    n++;
  }
  if (n === 0) return true;
  const brightFrac = bright / n;
  return brightness < 0.18 && brightFrac > 0.002 && brightFrac < 0.12;
}

/** After permission, prefer a real webcam over Iriun/OBS virtual devices. */
export async function openPreferredCamera(
  fallbackFacingMode: ConstrainDOMString = 'user',
): Promise<{ stream: MediaStream; label: string; virtual: boolean }> {
  const probe = await navigator.mediaDevices.getUserMedia({
    video: { facingMode: fallbackFacingMode, width: { ideal: 1280 }, height: { ideal: 720 } },
    audio: false,
  });
  try {
    const devices = await navigator.mediaDevices.enumerateDevices();
    const videos = devices.filter((d) => d.kind === 'videoinput' && d.deviceId);
    const currentId = probe.getVideoTracks()[0]?.getSettings().deviceId ?? '';
    const currentLabel = videos.find((d) => d.deviceId === currentId)?.label ?? probe.getVideoTracks()[0]?.label ?? '';
    const real = videos.find((d) => d.label && !isVirtualCameraLabel(d.label));

    if (real && isVirtualCameraLabel(currentLabel)) {
      probe.getTracks().forEach((t) => t.stop());
      const stream = await navigator.mediaDevices.getUserMedia({
        video: {
          deviceId: { exact: real.deviceId },
          width: { ideal: 1280 },
          height: { ideal: 720 },
        },
        audio: false,
      });
      return { stream, label: real.label, virtual: false };
    }

    return {
      stream: probe,
      label: currentLabel,
      virtual: isVirtualCameraLabel(currentLabel),
    };
  } catch {
    return {
      stream: probe,
      label: probe.getVideoTracks()[0]?.label ?? '',
      virtual: isVirtualCameraLabel(probe.getVideoTracks()[0]?.label ?? ''),
    };
  }
}

/** Chrome/Edge Shape Detection API when available — reject 0 or 2+ faces before upload. */
async function countFacesInCanvas(canvas: HTMLCanvasElement): Promise<number | null> {
  const FaceDetectorCtor = (window as unknown as {
    FaceDetector?: new (opts?: { maxDetectedFaces?: number; fastMode?: boolean }) => {
      detect: (source: CanvasImageSource) => Promise<Array<unknown>>;
    };
  }).FaceDetector;
  if (!FaceDetectorCtor) return null;
  try {
    const detector = new FaceDetectorCtor({ maxDetectedFaces: 8, fastMode: true });
    const faces = await detector.detect(canvas);
    return faces.length;
  } catch {
    return null;
  }
}

export async function captureFromVideo(
  video: HTMLVideoElement,
  poseId: FacePoseId,
): Promise<{ sample?: CapturedFaceSample; quality: FrameQuality }> {
  const w = video.videoWidth;
  const h = video.videoHeight;
  if (!w || !h) {
    return { quality: { ok: false, sharpness: 0, brightness: 0, reason: 'Camera not ready' } };
  }
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d', { willReadFrequently: true });
  if (!ctx) {
    return { quality: { ok: false, sharpness: 0, brightness: 0, reason: 'Canvas unavailable' } };
  }
  ctx.drawImage(video, 0, 0, w, h);
  const imageData = ctx.getImageData(0, 0, w, h);
  const quality = assessFrame(imageData);
  if (!quality.ok) return { quality };

  const faceCount = await countFacesInCanvas(canvas);
  if (faceCount === 0) {
    return {
      quality: {
        ok: false,
        sharpness: quality.sharpness,
        brightness: quality.brightness,
        faceCount,
        reason: 'No face detected — center your face in the oval',
      },
    };
  }
  if (faceCount != null && faceCount > 1) {
    return {
      quality: {
        ok: false,
        sharpness: quality.sharpness,
        brightness: quality.brightness,
        faceCount,
        reason: `Multiple faces detected (${faceCount}) — only one person in frame`,
      },
    };
  }

  const blob = await new Promise<Blob | null>((resolve) =>
    canvas.toBlob((b) => resolve(b), 'image/jpeg', 0.92),
  );
  if (!blob) {
    return { quality: { ok: false, sharpness: quality.sharpness, brightness: quality.brightness, reason: 'Encode failed' } };
  }
  const sample: CapturedFaceSample = {
    clientSampleId: newClientSampleId(),
    poseId,
    blob,
    previewUrl: URL.createObjectURL(blob),
    width: w,
    height: h,
    qualityScore: quality.sharpness,
  };
  return { sample, quality: { ...quality, faceCount: faceCount ?? undefined } };
}
