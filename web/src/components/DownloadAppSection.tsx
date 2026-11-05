'use client';

import { useState } from 'react';
import Link from 'next/link';
import { motion } from 'framer-motion';
import { Download, Loader2, Check, Monitor, Terminal, Apple, TrendingUp, ExternalLink } from 'lucide-react';
import { APP_SHORT_NAME } from '@/config';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import {
  assetsForPlatform,
  downloadAssetUrl,
  fetchReleases,
  formatBytes,
  type ReleaseAsset,
} from '@/lib/releases';

const OS_OPTIONS = [
  { id: 'windows', label: 'Windows', icon: Monitor, desc: '.exe installer' },
  { id: 'linux', label: 'Linux', icon: Terminal, desc: '.deb package' },
  { id: 'macos', label: 'macOS', icon: Apple, desc: '.dmg image' },
] as const;

export default function DownloadAppSection({ compact = false }: { compact?: boolean }) {
  const [showDownload, setShowDownload] = useState(false);
  const [selectedOs, setSelectedOs] = useState<string | null>(null);
  const [downloadState, setDownloadState] = useState<'idle' | 'fetching' | 'ready' | 'error'>('idle');
  const [asset, setAsset] = useState<ReleaseAsset | null>(null);
  const [errorMsg, setErrorMsg] = useState('');

  const handleOsSelect = async (osId: string) => {
    setSelectedOs(osId);
    setDownloadState('fetching');
    setAsset(null);
    setErrorMsg('');

    try {
      const payload = await fetchReleases();
      const stableTag = payload.latestStable;
      const stable =
        payload.releases.find(r => r.tagName === stableTag && !r.prerelease) ||
        payload.releases.find(r => !r.prerelease) ||
        payload.releases[0];

      if (!stable) {
        throw new Error('No published releases found');
      }

      const matches = assetsForPlatform(stable, osId);
      if (matches.length === 0) {
        setDownloadState('error');
        setErrorMsg(`No ${osId} installer in ${stable.tagName}`);
        return;
      }

      setAsset(matches[0]);
      setDownloadState('ready');
    } catch (e) {
      setDownloadState('error');
      setErrorMsg(e instanceof Error ? e.message : 'Could not fetch latest release');
    }
  };

  const handleDownload = () => {
    if (!asset) return;
    // Native navigation — do not fetch() the installer into memory.
    window.location.href = downloadAssetUrl(asset.id);
  };

  const openDialog = () => {
    setShowDownload(true);
    setSelectedOs(null);
    setDownloadState('idle');
    setAsset(null);
    setErrorMsg('');
  };

  return (
    <>
      {compact ? (
        <motion.div
          initial={{ opacity: 0, y: -10 }}
          animate={{ opacity: 1, y: 0 }}
          className="bg-card/80 backdrop-blur-sm rounded-xl border border-border px-4 py-3 flex items-center justify-between gap-4"
        >
          <div className="flex items-center gap-3 min-w-0">
            <div className="w-9 h-9 rounded-lg gradient-primary flex items-center justify-center flex-shrink-0">
              <Download className="w-4 h-4 text-primary-foreground" />
            </div>
            <div className="min-w-0">
              <p className="font-display font-bold text-foreground text-sm truncate">
                Download {APP_SHORT_NAME} Desktop App
              </p>
              <p className="text-xs text-muted-foreground truncate">
                Get the desktop client to unlock full monitoring
              </p>
            </div>
          </div>
          <button
            onClick={openDialog}
            className="px-4 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity flex items-center gap-2 flex-shrink-0"
          >
            <Download className="w-4 h-4" /> Download App
          </button>
        </motion.div>
      ) : (
        <motion.div
          initial={{ opacity: 0, y: -10 }}
          animate={{ opacity: 1, y: 0 }}
          className="bg-card rounded-xl border border-border p-5 flex flex-col lg:flex-row items-start lg:items-center justify-between gap-4"
        >
          <div className="flex items-start gap-4">
            <div className="w-12 h-12 rounded-xl gradient-primary flex items-center justify-center flex-shrink-0">
              <TrendingUp className="w-6 h-6 text-primary-foreground" />
            </div>
            <div>
              <h2 className="font-display font-bold text-foreground text-lg">
                Download {APP_SHORT_NAME} Desktop App
              </h2>
              <p className="text-sm text-muted-foreground mt-0.5">
                Download and install the {APP_SHORT_NAME} desktop app to access all features.
              </p>
              <p className="text-xs text-warning mt-1 font-medium">
                Note: To uninstall/quit the {APP_SHORT_NAME} app, please use this password ****************
              </p>
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Link
              href="/releases"
              className="px-4 py-2 rounded-lg border border-primary text-primary text-sm font-medium hover:bg-accent transition-colors inline-flex items-center gap-1.5"
            >
              All releases <ExternalLink className="w-3.5 h-3.5" />
            </Link>
            <button
              onClick={openDialog}
              className="px-4 py-2 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity flex items-center gap-2"
            >
              <Download className="w-4 h-4" /> Download App
            </button>
          </div>
        </motion.div>
      )}

      <Dialog open={showDownload} onOpenChange={setShowDownload}>
        <DialogContent className="bg-card sm:max-w-[480px]">
          <DialogHeader>
            <DialogTitle className="font-display text-lg">Download Desktop App</DialogTitle>
          </DialogHeader>
          <div className="mt-2 space-y-4">
            <p className="text-sm text-muted-foreground">
              Select your operating system to download the latest {APP_SHORT_NAME} desktop client.
            </p>

            <div className="grid grid-cols-3 gap-3">
              {OS_OPTIONS.map(os => {
                const Icon = os.icon;
                const isSelected = selectedOs === os.id;
                const isLoading = isSelected && downloadState === 'fetching';
                const isReady = isSelected && downloadState === 'ready';

                return (
                  <button
                    key={os.id}
                    onClick={() => handleOsSelect(os.id)}
                    disabled={downloadState === 'fetching'}
                    className={`relative flex flex-col items-center gap-2 p-4 rounded-xl border-2 transition-all
                      ${isSelected
                        ? 'border-primary bg-primary/5'
                        : 'border-border hover:border-primary/50 hover:bg-accent/50'
                      }
                      ${isLoading ? 'opacity-70 cursor-wait' : 'cursor-pointer'}
                    `}
                  >
                    {isLoading && (
                      <div className="absolute -top-1 -right-1">
                        <Loader2 className="w-4 h-4 animate-spin text-primary" />
                      </div>
                    )}
                    {isReady && (
                      <div className="absolute -top-1 -right-1">
                        <Check className="w-4 h-4 text-success" />
                      </div>
                    )}
                    <Icon className="w-8 h-8 text-foreground" />
                    <span className="text-sm font-medium text-foreground">{os.label}</span>
                    <span className="text-[10px] text-muted-foreground">{os.desc}</span>
                  </button>
                );
              })}
            </div>

            {downloadState === 'ready' && asset && (
              <motion.div
                initial={{ opacity: 0, y: 10 }}
                animate={{ opacity: 1, y: 0 }}
                className="space-y-3"
              >
                <div className="bg-success/10 border border-success/20 rounded-lg p-3 text-sm text-center">
                  <p className="text-success font-medium">
                    {selectedOs === 'windows' && 'Windows installer ready'}
                    {selectedOs === 'linux' && 'Linux package ready'}
                    {selectedOs === 'macos' && 'macOS package ready'}
                  </p>
                  <p className="text-muted-foreground text-xs mt-1">
                    {asset.name}
                    {asset.size > 0 ? ` · ${formatBytes(asset.size)}` : ''}
                  </p>
                </div>
                <button
                  onClick={handleDownload}
                  className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity flex items-center justify-center gap-2"
                >
                  <Download className="w-4 h-4" />
                  Download Now
                </button>
              </motion.div>
            )}

            {downloadState === 'error' && (
              <div className="bg-destructive/10 border border-destructive/20 rounded-lg p-3 text-sm text-center space-y-2">
                <p className="text-destructive font-medium">{errorMsg || 'Could not fetch latest release'}</p>
                <Link href="/releases" className="text-primary hover:underline text-xs inline-block">
                  Browse all releases
                </Link>
              </div>
            )}

            <p className="text-center text-xs text-muted-foreground">
              <Link href="/releases" className="text-primary hover:underline">
                View all versions and release notes
              </Link>
            </p>
          </div>
        </DialogContent>
      </Dialog>
    </>
  );
}
