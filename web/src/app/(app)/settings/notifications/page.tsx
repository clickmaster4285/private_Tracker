'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Bell, Plus } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Button } from '@/components/ui/button';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const initialAlerts = [
  { id: '1', category: 'Productivity', threshold: 'Score < 40', channels: ['Email', 'In-App'], frequency: 'Immediate' },
  { id: '2', category: 'Attendance', threshold: 'Late > 3 times/week', channels: ['Email', 'Slack'], frequency: 'Daily Digest' },
  { id: '3', category: 'Burnout', threshold: 'Overtime > 3 hrs/day', channels: ['In-App'], frequency: 'Immediate' },
];

export default function SettingsNotificationsPage() {
  const [alerts] = useState(initialAlerts);
  const [showAdd, setShowAdd] = useState(false);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between">
        <h3 className="font-display font-bold text-lg text-foreground">Notification & Alert Config</h3>
        <Button onClick={() => setShowAdd(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> New Alert</Button>
      </div>
      <div className="space-y-3">
        {alerts.map((a, i) => (
          <motion.div key={a.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="bg-card rounded-xl border border-border p-5 flex items-center justify-between">
            <div className="flex items-center gap-3">
              <Bell className="w-5 h-5 text-primary" />
              <div>
                <h4 className="font-display font-bold text-foreground text-sm">{a.category}</h4>
                <p className="text-xs text-muted-foreground">{a.threshold}</p>
              </div>
            </div>
            <div className="flex items-center gap-3">
              <div className="flex gap-1">{a.channels.map(c => <span key={c} className="px-2 py-0.5 rounded bg-accent text-accent-foreground text-[10px]">{c}</span>)}</div>
              <span className="text-xs text-muted-foreground">{a.frequency}</span>
            </div>
          </motion.div>
        ))}
      </div>
      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">New Alert</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Select defaultValue="Productivity"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="DLP">DLP</SelectItem><SelectItem value="Burnout">Burnout</SelectItem><SelectItem value="Productivity">Productivity</SelectItem><SelectItem value="Attendance">Attendance</SelectItem></SelectContent></Select>
            <Input placeholder="Threshold (e.g. Score < 40)" />
            <Select defaultValue="Immediate"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="Immediate">Immediate</SelectItem><SelectItem value="Daily Digest">Daily Digest</SelectItem><SelectItem value="Weekly">Weekly</SelectItem></SelectContent></Select>
            <Button className="w-full gradient-primary text-primary-foreground">Create Alert</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
