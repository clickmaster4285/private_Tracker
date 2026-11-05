'use client'

import { useState } from 'react';
import { motion } from 'framer-motion';
import { FileBarChart, Users, Trophy, Clock, Plus, Download, Calendar, Layers } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';
import { Switch } from '@/components/ui/switch';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import StatsCard from '@/components/ui/StatsCard';

export default function ReportsPage() {
  const [showBuilder, setShowBuilder] = useState(false);

  return (
    <div className="space-y-6 animate-fade-in">
      {/* Daily Summary */}
      <div>
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-display font-bold text-lg text-foreground">Daily Report — Mar 10, 2026</h3>
          <input type="date" defaultValue="2026-03-10" className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground" />
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4">
          <StatsCard title="Active Employees" value="45" icon={Users} subtitle="out of 50" delay={0.05} />
          <StatsCard title="Avg Productivity Score" value="82" icon={Trophy} change={5} delay={0.1} />
          <StatsCard title="Total Focus Hours" value="218h" icon={Clock} subtitle="Aggregate" delay={0.15} />
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 mt-4">
          <div className="bg-card rounded-xl border border-border p-4">
            <p className="text-sm text-muted-foreground">Top App Today</p>
            <p className="text-lg font-display font-bold text-foreground mt-1">Visual Studio Code</p>
          </div>
          <div className="bg-card rounded-xl border border-border p-4">
            <p className="text-sm text-muted-foreground">Absent Count</p>
            <p className="text-lg font-display font-bold text-destructive mt-1">5</p>
          </div>
          <div className="bg-card rounded-xl border border-border p-4">
            <p className="text-sm text-muted-foreground">Total Active Employees</p>
            <p className="text-lg font-display font-bold text-success mt-1">45</p>
          </div>
        </div>
      </div>

      {/* Custom Report Builder */}
      <div className="flex items-center justify-between">
        <h3 className="font-display font-bold text-lg text-foreground">Custom Reports</h3>
        <Button onClick={() => setShowBuilder(true)} size="sm" className="gap-1 gradient-primary text-primary-foreground"><Plus className="w-4 h-4" /> Build Report</Button>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
        {[
          { name: 'Weekly Team Productivity', schedule: 'Weekly', format: 'PDF' },
          { name: 'Monthly Attendance Summary', schedule: 'Monthly', format: 'CSV' },
          { name: 'Department Score Comparison', schedule: 'On-demand', format: 'PDF' },
        ].map((report, i) => (
          <motion.div key={i} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.2 + i * 0.05 }} className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all">
            <div className="flex items-center gap-3 mb-3">
              <div className="w-10 h-10 rounded-lg bg-primary/10 flex items-center justify-center"><FileBarChart className="w-5 h-5 text-primary" /></div>
              <h4 className="font-display font-bold text-foreground">{report.name}</h4>
            </div>
            <div className="flex items-center justify-between text-sm">
              <span className="text-muted-foreground">{report.schedule}</span>
              <div className="flex items-center gap-2">
                <span className="px-2 py-0.5 rounded bg-accent text-accent-foreground text-xs">{report.format}</span>
                <button className="text-primary hover:text-primary/80"><Download className="w-4 h-4" /></button>
              </div>
            </div>
          </motion.div>
        ))}
      </div>

      <Dialog open={showBuilder} onOpenChange={setShowBuilder}>
        <DialogContent className="bg-card max-w-lg">
          <DialogHeader><DialogTitle className="font-display">Build Custom Report</DialogTitle></DialogHeader>
          <div className="space-y-4 mt-2">
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Report Name *</label><Input placeholder="e.g. Q1 Productivity Review" /></div>
            <div><label className="text-sm font-semibold text-foreground mb-1 block">Data Source *</label>
              <div className="flex flex-wrap gap-2">{['Activity', 'Attendance', 'Score', 'GPS'].map(s => (
                <label key={s} className="flex items-center gap-1.5 px-3 py-1.5 rounded-lg border border-border text-sm cursor-pointer hover:border-primary"><input type="checkbox" className="sr-only" />{s}</label>
              ))}</div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-sm font-semibold text-foreground mb-1 block">Grouping</label>
                <Select defaultValue="employee"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="employee">Employee</SelectItem><SelectItem value="team">Team</SelectItem><SelectItem value="department">Department</SelectItem></SelectContent></Select>
              </div>
              <div><label className="text-sm font-semibold text-foreground mb-1 block">Visualization</label>
                <Select defaultValue="table"><SelectTrigger><SelectValue /></SelectTrigger><SelectContent><SelectItem value="table">Table</SelectItem><SelectItem value="bar">Bar Chart</SelectItem><SelectItem value="line">Line Chart</SelectItem><SelectItem value="pie">Pie Chart</SelectItem></SelectContent></Select>
              </div>
            </div>
            <div className="grid grid-cols-2 gap-3">
              <div><label className="text-sm font-semibold text-foreground mb-1 block">From</label><Input type="date" /></div>
              <div><label className="text-sm font-semibold text-foreground mb-1 block">To</label><Input type="date" /></div>
            </div>
            <div className="flex items-center justify-between p-3 bg-muted/50 rounded-lg">
              <span className="text-sm font-medium text-foreground">Schedule Delivery</span>
              <Switch />
            </div>
            <Button className="w-full gradient-primary text-primary-foreground">Generate Report</Button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
