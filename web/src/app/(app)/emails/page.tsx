'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { toast } from 'sonner';

interface ReportConfig {
  id: string;
  title: string;
  description: string;
  enabled: boolean;
  comparison: string;
  hours: string;
  minutes: string;
  frequency: string[];
  recipients: string[];
}

const initialReports: ReportConfig[] = [
  { id: '1', title: 'Unproductive Employees', description: 'Get report of employees showing low productivity.', enabled: true, comparison: 'Less Than', hours: '04', minutes: '00', frequency: ['Daily'], recipients: [] },
  { id: '2', title: 'Productive Employees', description: 'Get report of employees with the highest productivity.', enabled: false, comparison: 'Greater Than', hours: '05', minutes: '00', frequency: ['Daily'], recipients: ['pinky'] },
  { id: '3', title: 'Best Performers', description: 'Get report highlighting your best-performing employees.', enabled: false, comparison: 'Greater Than', hours: '06', minutes: '00', frequency: ['Monthly'], recipients: [] },
  { id: '4', title: 'Attendance Report', description: 'Get daily attendance summary of all employees.', enabled: true, comparison: 'Less Than', hours: '08', minutes: '00', frequency: ['Daily', 'Weekly'], recipients: [] },
];

export default function EmailsAndAlerts() {
  const [tab, setTab] = useState<'reports' | 'alerts'>('reports');
  const [reports, setReports] = useState(initialReports);

  const toggleReport = (id: string) => {
    setReports(prev => prev.map(r => r.id === id ? { ...r, enabled: !r.enabled } : r));
    toast.success('Report updated!');
  };

  const toggleFreq = (id: string, freq: string) => {
    setReports(prev => prev.map(r => {
      if (r.id !== id) return r;
      const f = r.frequency.includes(freq) ? r.frequency.filter(x => x !== freq) : [...r.frequency, freq];
      return { ...r, frequency: f };
    }));
  };

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Tabs */}
      <div className="flex gap-2">
        <button onClick={() => setTab('reports')} className={`px-5 py-2.5 rounded-lg text-sm font-medium transition-all ${tab === 'reports' ? 'gradient-primary text-primary-foreground' : 'bg-card border border-border text-foreground hover:bg-muted'}`}>
          Email Reports
        </button>
        <button onClick={() => setTab('alerts')} className={`px-5 py-2.5 rounded-lg text-sm font-medium transition-all ${tab === 'alerts' ? 'gradient-primary text-primary-foreground' : 'bg-card border border-border text-foreground hover:bg-muted'}`}>
          Alerts
        </button>
      </div>

      {tab === 'reports' && (
        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">Choose how often you'd like to receive email updates, daily for real-time alerts, weekly for summaries, or monthly for complete reports.</p>
          {reports.map((report, i) => (
            <motion.div key={report.id} initial={{ opacity: 0, y: 8 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}
              className="bg-card rounded-xl border border-border p-5">
              <div className="flex flex-col lg:flex-row gap-6">
                <div className="flex-1 space-y-4">
                  <div className="flex items-center justify-between">
                    <div>
                      <h3 className="font-display font-bold text-foreground">{report.title}</h3>
                      <p className="text-sm text-muted-foreground">{report.description}</p>
                    </div>
                    <button onClick={() => toggleReport(report.id)} className={`relative w-11 h-6 rounded-full transition-colors ${report.enabled ? 'bg-primary' : 'bg-muted'}`}>
                      <div className={`absolute w-5 h-5 rounded-full bg-card shadow-sm top-0.5 transition-transform ${report.enabled ? 'translate-x-5.5 left-0.5' : 'left-0.5'}`} style={{ transform: report.enabled ? 'translateX(22px)' : 'translateX(0)' }} />
                    </button>
                  </div>
                  <div className="flex flex-wrap items-center gap-3">
                    <span className="text-sm font-medium text-primary flex items-center gap-1">⏱ Select Hours:</span>
                    <select value={report.comparison} className="bg-background border border-border rounded-lg px-2 py-1.5 text-sm text-foreground">
                      <option>Less Than</option>
                      <option>Greater Than</option>
                    </select>
                    <div className="flex items-center gap-1">
                      <select value={report.hours} className="bg-background border border-border rounded-lg px-2 py-1.5 text-sm text-foreground w-16">
                        {Array.from({ length: 13 }, (_, i) => <option key={i} value={String(i).padStart(2, '0')}>{String(i).padStart(2, '0')}</option>)}
                      </select>
                      <span className="text-foreground">:</span>
                      <select value={report.minutes} className="bg-background border border-border rounded-lg px-2 py-1.5 text-sm text-foreground w-16">
                        {['00', '15', '30', '45'].map(m => <option key={m} value={m}>{m}</option>)}
                      </select>
                      <span className="text-xs text-muted-foreground">Hours/Min</span>
                    </div>
                  </div>
                  <select className="bg-background border border-border rounded-lg px-3 py-2 text-sm text-muted-foreground w-full">
                    <option>Select Recipients</option>
                  </select>
                </div>
                <div className="lg:w-64 space-y-2">
                  <p className="text-sm font-medium text-foreground">Select report frequency</p>
                  <p className="text-xs text-muted-foreground">Select the frequency for receiving the {report.title}.</p>
                  <div className="flex gap-4">
                    {['Daily', 'Weekly', 'Monthly'].map(f => (
                      <label key={f} className="flex items-center gap-1.5 text-sm text-foreground">
                        <input type="checkbox" checked={report.frequency.includes(f)} onChange={() => toggleFreq(report.id, f)} className="rounded accent-primary" />
                        {f}
                      </label>
                    ))}
                  </div>
                </div>
              </div>
            </motion.div>
          ))}
        </div>
      )}

      {tab === 'alerts' && (
        <div className="bg-card rounded-xl border border-border p-8 text-center">
          <p className="text-muted-foreground">Configure custom alerts for employee activity thresholds, idle time warnings, and system notifications.</p>
        </div>
      )}
    </div>
  );
}
