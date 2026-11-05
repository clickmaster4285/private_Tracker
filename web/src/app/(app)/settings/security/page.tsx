'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Shield, Lock } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Button } from '@/components/ui/button';

export default function SettingsSecurityPage() {
  const [settings, setSettings] = useState({
    sessionTimeout: 30,
    mfaEnforcement: 'optional',
    minPasswordLength: 8,
    passwordExpiry: 90,
    allowedIPs: '192.168.1.0/24\n10.0.0.0/8',
  });

  return (
    <div className="space-y-6 animate-fade-in">
      <h3 className="font-display font-bold text-lg text-foreground">Security Settings</h3>

      <div className="bg-card rounded-xl border border-border p-6 space-y-5">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-5">
          <div>
            <label className="text-sm font-semibold text-foreground mb-1.5 block">Session Timeout (minutes) *</label>
            <Input type="number" value={settings.sessionTimeout} onChange={e => setSettings({...settings, sessionTimeout: Number(e.target.value)})} min={5} />
            <p className="text-xs text-muted-foreground mt-1">Auto-logout after inactivity. Minimum 5 minutes.</p>
          </div>
          <div>
            <label className="text-sm font-semibold text-foreground mb-1.5 block">MFA Enforcement *</label>
            <Select value={settings.mfaEnforcement} onValueChange={v => setSettings({...settings, mfaEnforcement: v})}>
              <SelectTrigger><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="off">Off</SelectItem>
                <SelectItem value="optional">Optional</SelectItem>
                <SelectItem value="admins">Required for Admins</SelectItem>
                <SelectItem value="all">Required for All</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div>
            <label className="text-sm font-semibold text-foreground mb-1.5 block">Password Min Length *</label>
            <Input type="number" value={settings.minPasswordLength} onChange={e => setSettings({...settings, minPasswordLength: Number(e.target.value)})} min={8} />
          </div>
          <div>
            <label className="text-sm font-semibold text-foreground mb-1.5 block">Password Expiry (days)</label>
            <Input type="number" value={settings.passwordExpiry} onChange={e => setSettings({...settings, passwordExpiry: Number(e.target.value)})} />
            <p className="text-xs text-muted-foreground mt-1">Set 0 for no expiry.</p>
          </div>
        </div>

        <div>
          <label className="text-sm font-semibold text-foreground mb-1.5 block">Allowed IP Ranges</label>
          <Textarea value={settings.allowedIPs} onChange={e => setSettings({...settings, allowedIPs: e.target.value})} rows={4} placeholder="CIDR notation, one per line" />
          <p className="text-xs text-muted-foreground mt-1">Restrict admin access to specific IP ranges. CIDR notation.</p>
        </div>

        <Button className="gradient-primary text-primary-foreground">
          <Lock className="w-4 h-4 mr-2" /> Save Security Settings
        </Button>
      </div>
    </div>
  );
}
