'use client'

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Target } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Progress } from '@/components/ui/progress';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

const initialGoals = [
  { id: '1', title: 'Increase Sprint Velocity by 15%', description: 'Improve team velocity from 42 to 48 story points', type: 'Team', target: 48, unit: 'story points', start: '2026-03-01', due: '2026-03-31', assignees: ['Yashodhan', 'Rakesh', 'Arush'], status: 'On Track', progress: 65 },
  { id: '2', title: 'Reduce Code Review Time', description: 'Average code review turnaround under 4 hours', type: 'Team', target: 4, unit: 'hours', start: '2026-03-01', due: '2026-03-31', assignees: ['Engineering Team'], status: 'At Risk', progress: 40 },
  { id: '3', title: 'Complete Security Audit', description: 'Full OWASP Top 10 audit of the platform', type: 'Individual', target: 1, unit: 'audit', start: '2026-02-15', due: '2026-03-15', assignees: ['Security Analyst'], status: 'On Track', progress: 80 },
  { id: '4', title: 'Design System v2 Launch', description: 'Ship updated component library', type: 'Department', target: 100, unit: '% complete', start: '2026-01-01', due: '2026-04-01', assignees: ['Design', 'Engineering'], status: 'On Track', progress: 55 },
];

const statusColors: Record<string, string> = {
  'On Track': 'bg-success/15 text-success',
  'At Risk': 'bg-warning/15 text-warning',
  'Complete': 'bg-info/15 text-info',
  'Cancelled': 'bg-muted text-muted-foreground',
};

export default function GoalsPage() {
  const [goals] = useState(initialGoals);
  const [showAdd, setShowAdd] = useState(false);

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between">
        <h3 className="font-display font-bold text-lg text-foreground">Goals & OKRs</h3>
        <Button onClick={() => setShowAdd(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> New Goal</Button>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {goals.map((goal, i) => (
          <motion.div key={goal.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all">
            <div className="flex items-start justify-between mb-3">
              <div className="flex items-center gap-2">
                <Target className="w-5 h-5 text-primary flex-shrink-0" />
                <h4 className="font-display font-bold text-foreground">{goal.title}</h4>
              </div>
              <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${statusColors[goal.status]}`}>{goal.status}</span>
            </div>
            <p className="text-sm text-muted-foreground mb-3">{goal.description}</p>
            <div className="flex items-center gap-3 mb-3">
              <Progress value={goal.progress} className="flex-1 h-2" />
              <span className="text-sm font-bold text-foreground">{goal.progress}%</span>
            </div>
            <div className="flex flex-wrap gap-3 text-xs text-muted-foreground">
              <span>Type: <span className="text-foreground font-medium">{goal.type}</span></span>
              <span>Target: <span className="text-foreground font-medium">{goal.target} {goal.unit}</span></span>
              <span>Due: <span className="text-foreground font-medium">{goal.due}</span></span>
            </div>
            <div className="flex gap-1 mt-2">{goal.assignees.map(a => (
              <span key={a} className="px-2 py-0.5 rounded bg-accent text-accent-foreground text-[10px] font-medium">{a}</span>
            ))}</div>
          </motion.div>
        ))}
      </div>

      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card max-w-lg">
          <DialogHeader><DialogTitle className="font-display">New Goal</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <Input placeholder="Goal Title *" />
            <Textarea placeholder="Description" rows={2} />
            <div className="grid grid-cols-2 gap-3">
              <Select defaultValue="Individual"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="Individual">Individual</SelectItem><SelectItem value="Team">Team</SelectItem><SelectItem value="Department">Department</SelectItem></SelectContent></Select>
              <Select defaultValue="On Track"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="On Track">On Track</SelectItem><SelectItem value="At Risk">At Risk</SelectItem><SelectItem value="Complete">Complete</SelectItem><SelectItem value="Cancelled">Cancelled</SelectItem></SelectContent></Select>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-xs text-muted-foreground">Target Value</label><Input type="number" placeholder="e.g. 20" /></div>
              <div><label className="text-xs text-muted-foreground">Unit</label><Input placeholder="e.g. tasks, PRs" /></div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-xs text-muted-foreground">Start Date</label><Input type="date" /></div>
              <div><label className="text-xs text-muted-foreground">Due Date</label><Input type="date" /></div>
            </div>
            <Button className="w-full gradient-primary text-primary-foreground">Create Goal</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
