'use client'

import { motion } from 'framer-motion';
import { Download } from 'lucide-react';
import { Button } from '@/components/ui/button';

const auditData = [
  { type: 'Login', actor: 'Super Admin', target: 'Auth System', timestamp: '2026-03-10 10:00:00 UTC', ip: '192.168.1.100', result: 'Success' },
  { type: 'Config change', actor: 'Org Admin', target: 'Screenshot Settings', timestamp: '2026-03-10 09:45:00 UTC', ip: '192.168.1.101', result: 'Success' },
  { type: 'Export', actor: 'HR Admin', target: 'Attendance Report', timestamp: '2026-03-10 09:30:00 UTC', ip: '192.168.1.102', result: 'Success' },
  { type: 'Delete', actor: 'Org Admin', target: 'Employee: Kamal Dhami', timestamp: '2026-03-10 09:15:00 UTC', ip: '192.168.1.101', result: 'Failure' },
  { type: 'Login', actor: 'Employee User', target: 'Auth System', timestamp: '2026-03-10 09:00:00 UTC', ip: '10.0.0.55', result: 'Success' },
  { type: 'Config change', actor: 'IT Admin', target: 'DLP Rules', timestamp: '2026-03-10 08:45:00 UTC', ip: '192.168.1.105', result: 'Success' },
];

const typeColors: Record<string, string> = {
  Login: 'bg-info/15 text-info',
  'Config change': 'bg-warning/15 text-warning',
  Export: 'bg-success/15 text-success',
  Delete: 'bg-destructive/15 text-destructive',
};

export default function AuditLogPage() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex items-center justify-between">
        <div className="flex gap-3">
          <input type="date" defaultValue="2026-03-01" className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground" />
          <input type="date" defaultValue="2026-03-10" className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground" />
          <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
            <option>All Events</option><option>Login</option><option>Export</option><option>Delete</option><option>Config change</option>
          </select>
        </div>
        <Button variant="outline" size="sm" className="gap-1"><Download className="w-4 h-4" /> Export CSV</Button>
      </div>

      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[800px]">
          <thead>
            <tr className="border-b border-border">
              {['Event Type', 'Actor', 'Target', 'Timestamp', 'IP Address', 'Result'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {auditData.map((a, i) => (
              <motion.tr key={i} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${typeColors[a.type] || 'bg-muted text-muted-foreground'}`}>{a.type}</span></td>
                <td className="px-4 py-3 text-sm font-medium text-foreground">{a.actor}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{a.target}</td>
                <td className="px-4 py-3 text-xs font-mono text-muted-foreground">{a.timestamp}</td>
                <td className="px-4 py-3 text-xs font-mono text-muted-foreground">{a.ip}</td>
                <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${a.result === 'Success' ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>{a.result}</span></td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
