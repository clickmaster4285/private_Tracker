'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Target, TrendingUp, Edit2, Trash2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { toast } from 'sonner';

interface KPI {
  id: string;
  name: string;
  target: string;
  current: number;
  unit: string;
  department: string;
}

const initialKPIs: KPI[] = [
  { id: '1', name: 'Daily Productive Hours', target: '6', current: 4.5, unit: 'hrs', department: 'All' },
  { id: '2', name: 'Task Completion Rate', target: '90', current: 72, unit: '%', department: 'Engineering' },
  { id: '3', name: 'Response Time', target: '30', current: 45, unit: 'min', department: 'Sales' },
  { id: '4', name: 'Code Review Turnaround', target: '4', current: 6, unit: 'hrs', department: 'Engineering' },
  { id: '5', name: 'Customer Satisfaction', target: '95', current: 88, unit: '%', department: 'Sales' },
  { id: '6', name: 'Design Iteration Speed', target: '2', current: 3, unit: 'days', department: 'Design' },
];

export default function KPIsAndKRAs() {
  const [kpis, setKpis] = useState(initialKPIs);
  const [showAdd, setShowAdd] = useState(false);
  const [newName, setNewName] = useState('');
  const [newTarget, setNewTarget] = useState('');
  const [newUnit, setNewUnit] = useState('');
  const [newDept, setNewDept] = useState('All');

  const handleAdd = () => {
    if (!newName || !newTarget) return;
    setKpis(prev => [...prev, { id: String(Date.now()), name: newName, target: newTarget, current: 0, unit: newUnit, department: newDept }]);
    setNewName(''); setNewTarget(''); setNewUnit('');
    setShowAdd(false);
    toast.success('KPI added!');
  };

  const handleDelete = (id: string) => {
    setKpis(prev => prev.filter(k => k.id !== id));
    toast.success('KPI removed');
  };

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <p className="text-sm text-muted-foreground">{kpis.length} KPIs configured</p>
        <button onClick={() => setShowAdd(true)} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90">
          <Plus className="w-4 h-4" /> Add KPI
        </button>
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['KPI Name', 'Department', 'Target', 'Current', 'Progress', 'Actions'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {kpis.map((kpi, i) => {
              const progress = Math.min(100, (kpi.current / Number(kpi.target)) * 100);
              const isGood = progress >= 80;
              return (
                <motion.tr key={kpi.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <Target className="w-4 h-4 text-primary" />
                      <span className="text-sm font-medium text-foreground">{kpi.name}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{kpi.department}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{kpi.target} {kpi.unit}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{kpi.current} {kpi.unit}</td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <div className="flex-1 h-2 bg-muted rounded-full max-w-[120px]">
                        <div className={`h-full rounded-full transition-all ${isGood ? 'bg-success' : 'bg-warning'}`} style={{ width: `${progress}%` }} />
                      </div>
                      <span className="text-xs font-medium text-foreground">{Math.round(progress)}%</span>
                    </div>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-1">
                      <button className="p-1.5 rounded hover:bg-muted transition-colors"><Edit2 className="w-3.5 h-3.5 text-muted-foreground" /></button>
                      <button onClick={() => handleDelete(kpi.id)} className="p-1.5 rounded hover:bg-muted transition-colors"><Trash2 className="w-3.5 h-3.5 text-destructive" /></button>
                    </div>
                  </td>
                </motion.tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Add KPI</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input value={newName} onChange={e => setNewName(e.target.value)} placeholder="KPI Name" className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            <div className="grid grid-cols-2 gap-3">
              <input value={newTarget} onChange={e => setNewTarget(e.target.value)} placeholder="Target Value" className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
              <input value={newUnit} onChange={e => setNewUnit(e.target.value)} placeholder="Unit (hrs, %, etc.)" className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground" />
            </div>
            <select value={newDept} onChange={e => setNewDept(e.target.value)} className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground">
              {['All', 'Engineering', 'Design', 'Marketing', 'Sales', 'HR', 'QA', 'DevOps'].map(d => <option key={d}>{d}</option>)}
            </select>
            <button onClick={handleAdd} className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90">Add KPI</button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
