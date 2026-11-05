'use client';

import { motion } from 'framer-motion';
import { Shield, Download, Globe } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select';

const erasureRequests = [
  { employee: 'John Doe', requestDate: '2026-02-28', status: 'Pending', deadline: '2026-03-28' },
  { employee: 'Jane Smith', requestDate: '2026-01-15', status: 'Completed', deadline: '2026-02-15' },
];

const consentRecords = [
  { employee: 'Yashodhan Kalia', consentDate: '2025-12-01 09:00', version: 'v1.0', ip: '192.168.1.50' },
  { employee: 'Stuti Srivastava', consentDate: '2025-12-01 09:15', version: 'v1.0', ip: '192.168.1.51' },
  { employee: 'Rakesh Pathania', consentDate: '2026-01-15 10:00', version: 'v1.1', ip: '10.0.0.22' },
];

export default function SettingsCompliancePage() {
  return (
    <div className="space-y-6 animate-fade-in">
      <h3 className="font-display font-bold text-lg text-foreground">GDPR & Compliance</h3>

      <div className="bg-card rounded-xl border border-border p-5">
        <div className="flex items-center gap-3 mb-4">
          <Globe className="w-5 h-5 text-primary" />
          <h4 className="font-display font-bold text-foreground">Data Residency Region</h4>
        </div>
        <Select defaultValue="eu">
          <SelectTrigger className="w-48"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="eu">EU (Frankfurt)</SelectItem>
            <SelectItem value="us">US (Virginia)</SelectItem>
            <SelectItem value="apac">APAC (Singapore)</SelectItem>
            <SelectItem value="me">Middle East (Bahrain)</SelectItem>
          </SelectContent>
        </Select>
        <p className="text-xs text-muted-foreground mt-2">Cannot be changed after data has been stored.</p>
      </div>

      <div>
        <div className="flex items-center justify-between mb-3">
          <h4 className="font-display font-bold text-foreground">Right-to-Erasure Requests</h4>
        </div>
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full">
            <thead><tr className="border-b border-border">
              {['Employee', 'Request Date', 'Deadline', 'Status'].map(h => <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>)}
            </tr></thead>
            <tbody>
              {erasureRequests.map((r, i) => (
                <tr key={i} className="border-b border-border last:border-0">
                  <td className="px-4 py-3 text-sm text-foreground">{r.employee}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{r.requestDate}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{r.deadline}</td>
                  <td className="px-4 py-3"><span className={`px-2.5 py-1 rounded-full text-xs font-medium ${r.status === 'Completed' ? 'bg-success/15 text-success' : 'bg-warning/15 text-warning'}`}>{r.status}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div>
        <h4 className="font-display font-bold text-foreground mb-3">Consent Records</h4>
        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full">
            <thead><tr className="border-b border-border">
              {['Employee', 'Consent Date', 'Policy Version', 'IP Address', 'Export'].map(h => <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>)}
            </tr></thead>
            <tbody>
              {consentRecords.map((r, i) => (
                <tr key={i} className="border-b border-border last:border-0">
                  <td className="px-4 py-3 text-sm text-foreground">{r.employee}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{r.consentDate}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{r.version}</td>
                  <td className="px-4 py-3 text-xs font-mono text-muted-foreground">{r.ip}</td>
                  <td className="px-4 py-3"><button className="text-primary hover:text-primary/80"><Download className="w-4 h-4" /></button></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
