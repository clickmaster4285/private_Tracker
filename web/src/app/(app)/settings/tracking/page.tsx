'use client';

import { useState } from 'react';
import { APP_SHORT_NAME } from '@/config';
import { toast } from 'sonner';
import { ArrowLeft, Info } from 'lucide-react';
import { useRouter } from 'next/navigation';

const DEFAULT_SETTINGS: Record<string, unknown> = {
  screenshotTime: 5,
  appTime: 5,
  geoLocationTime: 5,
  systemStatusTime: 10,
  maxIdleTime: 5,
  offlineTime: 60,
  blurImage: false,
  appVisibility: 'visible',
};

export default function TrackingSettings() {
  const router = useRouter();
  const [settings, setSettings] = useState<Record<string, unknown>>({ ...DEFAULT_SETTINGS });

  const update = (key: string, value: unknown) => {
    setSettings(prev => ({ ...prev, [key]: value }));
  };

  const handleSave = () => {
    toast.info('Saved for this session', {
      description: 'Server-side config sync is not wired yet — values reset on reload.',
    });
  };

  const timeOptions = [1, 2, 3, 5, 10, 15, 30, 60];

  return (
    <div className="max-w-2xl animate-fade-in space-y-6">
      <button onClick={() => router.push('/settings')} className="flex items-center gap-2 text-sm text-primary hover:underline">
        <ArrowLeft className="w-4 h-4" /> Back to Settings
      </button>

      <div className="bg-card rounded-xl border border-border p-6">
        <h2 className="font-display font-bold text-lg text-foreground mb-1">Tracking Settings</h2>
        <p className="text-sm text-muted-foreground mb-2">Set up various time tracking options.</p>
        <p className="text-xs text-muted-foreground flex items-center gap-1.5 mb-6">
          <Info className="w-3.5 h-3.5" /> Not persisted yet — these will sync to tracked clients once server-side configuration ships.
        </p>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          {[
            { key: 'screenshotTime', label: 'Screenshot Time' },
            { key: 'appTime', label: 'App Time' },
            { key: 'geoLocationTime', label: 'Geo location Time' },
            { key: 'systemStatusTime', label: 'System Status Time' },
            { key: 'maxIdleTime', label: 'Max Idle Time' },
            { key: 'offlineTime', label: 'Offline Time' },
          ].map(({ key, label }) => (
            <div key={key}>
              <label className="flex items-center gap-2 mb-2">
                <input type="checkbox" defaultChecked className="rounded border-border accent-primary" />
                <span className="text-sm font-medium text-foreground">{label}</span>
              </label>
              <select value={(settings[key] as number) || 5} onChange={e => update(key, Number(e.target.value))} className="w-full bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground">
                {timeOptions.map(t => <option key={t} value={t}>{t} minutes</option>)}
              </select>
            </div>
          ))}
        </div>

        <div className="mt-4">
          <label className="flex items-center gap-2">
            <input type="checkbox" checked={!!settings.blurImage} onChange={e => update('blurImage', e.target.checked)} className="rounded border-border accent-primary" />
            <span className="text-sm font-medium text-foreground">Blur Image</span>
          </label>
        </div>
      </div>

      <div className="bg-card rounded-xl border border-border p-6">
        <h3 className="font-display font-semibold text-foreground mb-4">App Visibility</h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          {['visible', 'stealth'].map(mode => (
            <button key={mode} onClick={() => update('appVisibility', mode)}
              className={`p-4 rounded-xl border-2 text-left transition-all ${settings.appVisibility === mode ? 'border-primary bg-accent' : 'border-border hover:border-primary/30'}`}>
              <p className="font-semibold text-foreground capitalize mb-1">{mode}</p>
              <p className="text-xs text-muted-foreground">
                {mode === 'visible' ? `Employees will see the ${APP_SHORT_NAME} app icon on their computer.` : `${APP_SHORT_NAME} app icon will be hidden from employees' computers.`}
              </p>
            </button>
          ))}
        </div>
      </div>

      <div className="flex gap-3 justify-end">
        <button onClick={() => router.push('/settings')} className="px-5 py-2.5 rounded-lg border border-border text-sm font-medium text-foreground hover:bg-muted transition-colors">Cancel</button>
        <button onClick={handleSave} className="px-5 py-2.5 rounded-lg gradient-primary text-primary-foreground text-sm font-medium hover:opacity-90 transition-opacity">Save</button>
      </div>
    </div>
  );
}
