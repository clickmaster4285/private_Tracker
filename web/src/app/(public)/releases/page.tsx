'use client';

import { Suspense, useEffect, useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Download,
  Loader2,
  Monitor,
  Terminal,
  RefreshCw,
  Search,
  ChevronDown,
  ChevronUp,
  AlertCircle,
} from 'lucide-react';
import { APP_SHORT_NAME } from '@/config';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import SafeMarkdown from '@/components/releases/SafeMarkdown';
import {
  assetsForPlatform,
  downloadAssetUrl,
  fetchReleases,
  formatBytes,
  type ReleaseAsset,
  type ReleaseInfo,
} from '@/lib/releases';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

function formatPublished(iso: string): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

function DownloadButtons({ release }: { release: ReleaseInfo }) {
  const windows = assetsForPlatform(release, 'windows');
  const linux = assetsForPlatform(release, 'linux');

  const renderGroup = (label: string, icon: React.ReactNode, assets: ReleaseAsset[]) => {
    if (assets.length === 0) {
      return (
        <div className="flex flex-col gap-1 min-w-[9rem]">
          <span className="text-xs text-muted-foreground flex items-center gap-1.5">
            {icon}
            {label}
          </span>
          <span className="text-xs text-muted-foreground/80">Unavailable</span>
        </div>
      );
    }
    return (
      <div className="flex flex-col gap-1.5 min-w-[9rem]">
        <span className="text-xs text-muted-foreground flex items-center gap-1.5">
          {icon}
          {label}
        </span>
        {assets.map(a => (
          <a
            key={a.id}
            href={downloadAssetUrl(a.id)}
            className="inline-flex items-center justify-center gap-2 rounded-lg gradient-primary text-primary-foreground px-3 py-2 text-sm font-medium hover:opacity-90 transition-opacity focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
          >
            <Download className="w-4 h-4" aria-hidden />
            {assets.length > 1 ? a.name : 'Download'}
            {a.size > 0 && (
              <span className="text-[10px] opacity-80 font-normal">{formatBytes(a.size)}</span>
            )}
          </a>
        ))}
      </div>
    );
  };

  return (
    <div className="flex flex-wrap gap-4">
      {renderGroup('Windows (.exe)', <Monitor className="w-3.5 h-3.5" />, windows)}
      {renderGroup('Linux (.deb)', <Terminal className="w-3.5 h-3.5" />, linux)}
    </div>
  );
}

function ReleaseCard({
  release,
  isLatestStable,
  defaultOpen,
}: {
  release: ReleaseInfo;
  isLatestStable: boolean;
  defaultOpen: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const hasNotes = Boolean(release.body?.trim());

  return (
    <article
      className={`rounded-xl border bg-card p-5 space-y-4 ${
        isLatestStable ? 'border-primary/40 shadow-sm' : 'border-border'
      }`}
    >
      <div className="space-y-1 min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="font-display font-bold text-lg text-foreground truncate">
            {release.name || release.tagName}
          </h2>
          <Badge variant="outline" className="font-mono text-xs">
            {release.tagName}
          </Badge>
          {isLatestStable && (
            <Badge className="bg-success text-success-foreground border-transparent">Latest stable</Badge>
          )}
          {release.prerelease && <Badge variant="secondary">Prerelease</Badge>}
        </div>
        {release.publishedAt && (
          <p className="text-xs text-muted-foreground">Published {formatPublished(release.publishedAt)}</p>
        )}
      </div>

      <DownloadButtons release={release} />

      <div>
        <button
          type="button"
          onClick={() => setOpen(v => !v)}
          className="inline-flex items-center gap-1.5 text-sm text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded"
          aria-expanded={open}
        >
          {open ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
          {open ? 'Hide' : 'Show'} release notes
          {!hasNotes && !open ? ' (empty)' : ''}
        </button>
        {open && (
          <div className="mt-3 rounded-lg border border-border bg-muted/30 p-4">
            <SafeMarkdown content={release.body || ''} />
          </div>
        )}
      </div>
    </article>
  );
}

function ReleasesBody() {
  const [filters, setFilters] = useUrlQueryState(
    { q: {}, platform: {} },
    { q: '', platform: '' },
    { debounceMs: 400 },
  );
  const [qLocal, setQLocal] = useState(filters.q);

  useEffect(() => {
    setQLocal(filters.q);
  }, [filters.q]);

  const { data, isLoading, isError, error, refetch, isFetching } = useQuery({
    queryKey: ['public-releases'],
    queryFn: fetchReleases,
    staleTime: 60_000,
    retry: 1,
  });

  const releases = data?.releases ?? [];
  const filtered = useMemo(() => {
    const q = filters.q.trim().toLowerCase();
    return releases.filter(r => {
      if (filters.platform) {
        const has = (r.assets || []).some(a => a.platform === filters.platform);
        if (!has) return false;
      }
      if (!q) return true;
      return (
        r.tagName.toLowerCase().includes(q) ||
        (r.name || '').toLowerCase().includes(q) ||
        (r.body || '').toLowerCase().includes(q)
      );
    });
  }, [releases, filters.q, filters.platform]);

  return (
    <div className="mx-auto max-w-5xl px-4 py-8 sm:py-12 space-y-8">
      <div className="space-y-3">
        <h1 className="font-display text-3xl sm:text-4xl font-extrabold tracking-tight text-foreground">
          {APP_SHORT_NAME} Releases
        </h1>
        <p className="text-muted-foreground max-w-2xl text-sm sm:text-base">
          Download the Windows and Linux desktop applications. Installers are served directly by the
          Alpha API server — no account required.
        </p>
      </div>

      <div className="flex flex-col sm:flex-row gap-3 sm:items-center">
        <div className="relative flex-1">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
          <Input
            value={qLocal}
            onChange={e => {
              const v = e.target.value;
              setQLocal(v);
              setFilters({ q: v });
            }}
            placeholder="Search version or notes…"
            className="pl-9"
            aria-label="Search releases"
          />
        </div>
        <div className="flex gap-2 flex-wrap">
          {([
            { id: '', label: 'All' },
            { id: 'windows', label: 'Windows' },
            { id: 'linux', label: 'Linux' },
          ] as const).map(p => (
            <button
              key={p.id || 'all'}
              type="button"
              onClick={() => setFilters({ platform: p.id })}
              className={`px-3 py-2 rounded-lg text-sm border transition-colors ${
                filters.platform === p.id
                  ? 'border-primary bg-primary/10 text-foreground'
                  : 'border-border text-muted-foreground hover:bg-accent/50'
              }`}
            >
              {p.label}
            </button>
          ))}
          <button
            type="button"
            onClick={() => refetch()}
            disabled={isFetching}
            className="px-3 py-2 rounded-lg text-sm border border-border text-muted-foreground hover:bg-accent/50 inline-flex items-center gap-1.5"
            aria-label="Refresh releases"
          >
            <RefreshCw className={`w-4 h-4 ${isFetching ? 'animate-spin' : ''}`} />
          </button>
        </div>
      </div>

      {isLoading && (
        <div className="flex items-center justify-center gap-2 py-20 text-muted-foreground">
          <Loader2 className="w-5 h-5 animate-spin" />
          Loading releases…
        </div>
      )}

      {isError && (
        <div className="rounded-xl border border-destructive/30 bg-destructive/5 p-6 text-center space-y-3">
          <AlertCircle className="w-8 h-8 text-destructive mx-auto" />
          <p className="font-medium text-destructive">Could not load releases</p>
          <p className="text-sm text-muted-foreground">{(error as Error)?.message || 'Unknown error'}</p>
          <button
            type="button"
            onClick={() => refetch()}
            className="px-4 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium"
          >
            Retry
          </button>
        </div>
      )}

      {!isLoading && !isError && filtered.length === 0 && (
        <div className="rounded-xl border border-border bg-card p-10 text-center text-muted-foreground">
          {releases.length === 0
            ? 'No published releases are available yet.'
            : 'No releases match your filters.'}
        </div>
      )}

      <div className="space-y-4">
        {filtered.map((r, i) => (
          <ReleaseCard
            key={r.tagName}
            release={r}
            isLatestStable={Boolean(data?.latestStable) && r.tagName === data?.latestStable}
            defaultOpen={i === 0}
          />
        ))}
      </div>
    </div>
  );
}

export default function ReleasesPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center py-24 text-muted-foreground gap-2">
          <Loader2 className="w-5 h-5 animate-spin" />
          Loading…
        </div>
      }
    >
      <ReleasesBody />
    </Suspense>
  );
}
