/** Public releases / download helpers (no login; installers come from the API server setup folder). */

const API_BASE = process.env.NEXT_PUBLIC_API_URL || '/api/v1';

export interface ReleaseAsset {
  id: number;
  name: string;
  platform: 'windows' | 'linux' | 'macos' | string;
  size: number;
  contentType?: string;
}

export interface ReleaseInfo {
  tagName: string;
  name: string;
  publishedAt: string;
  prerelease: boolean;
  body: string;
  assets: ReleaseAsset[];
}

export interface ReleasesPayload {
  latestStable: string;
  releases: ReleaseInfo[];
}

export function downloadAssetUrl(assetId: number): string {
  return `${API_BASE}/downloads/${assetId}`;
}

export function formatBytes(size: number): string {
  if (!size || size < 0) return '';
  if (size < 1024) return `${size} B`;
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`;
  return `${(size / (1024 * 1024)).toFixed(1)} MB`;
}

export async function fetchReleases(): Promise<ReleasesPayload> {
  const res = await fetch(`${API_BASE}/releases`, {
    method: 'GET',
    credentials: 'omit',
    headers: { Accept: 'application/json' },
  });
  if (!res.ok) {
    let message = `Could not load releases (${res.status})`;
    try {
      const body = await res.json();
      if (body?.error) message = String(body.error);
    } catch {
      // ignore
    }
    throw new Error(message);
  }
  return res.json();
}

export function assetsForPlatform(release: ReleaseInfo, platform: string): ReleaseAsset[] {
  return (release.assets || []).filter(a => a.platform === platform);
}
