'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Search, MoreVertical, ArrowLeft, FolderKanban } from 'lucide-react';

interface Integration {
  id: string;
  name: string;
  description: string;
  color: string;
}

interface Project {
  id: string;
  name: string;
  key: string;
  timeSpent: number;
  taskCount: number;
  assignee: number;
}

const integrations: Integration[] = [
  { id: '1', name: 'ClickUp', description: 'Import the ClickUp items you want to track time on', color: 'hsl(262, 80%, 50%)' },
  { id: '2', name: 'Jira', description: 'Import the Jira items you want to track time on', color: 'hsl(210, 80%, 55%)' },
  { id: '3', name: 'Redmine', description: 'Import the Redmine items you want to track time on', color: 'hsl(0, 72%, 55%)' },
];

const sampleProjects: Project[] = [
  { id: '1', name: 'Project 1', key: '—', timeSpent: 0, taskCount: 24, assignee: 3 },
  { id: '2', name: 'Project 3', key: '—', timeSpent: 0, taskCount: 13, assignee: 3 },
  { id: '3', name: 'Imported from Spreadsheet', key: '—', timeSpent: 0, taskCount: 1485, assignee: 1 },
  { id: '4', name: 'Imported from Spreadsheet 1', key: '—', timeSpent: 0, taskCount: 500, assignee: 0 },
  { id: '5', name: 'J1', key: 'Spreadsheet Import', timeSpent: 0, taskCount: 434, assignee: 1 },
  { id: '6', name: 'J3', key: 'Spreadsheet Import', timeSpent: 0, taskCount: 446, assignee: 1 },
  { id: '7', name: 'J2', key: 'Spreadsheet Import', timeSpent: 0, taskCount: 434, assignee: 0 },
];

export default function ProjectsPage() {
  const [view, setView] = useState<'integrations' | 'projects'>('integrations');
  const [tab, setTab] = useState<'Integrated' | 'Archived'>('Integrated');
  const [search, setSearch] = useState('');

  const filtered = sampleProjects.filter(p => p.name.toLowerCase().includes(search.toLowerCase()));

  if (view === 'projects') {
    return (
      <div className="space-y-4 animate-fade-in">
        <button onClick={() => setView('integrations')} className="flex items-center gap-2 text-sm text-primary hover:underline">
          <ArrowLeft className="w-4 h-4" /> Back
        </button>

        <div className="flex border-b border-border gap-1">
          {(['Integrated', 'Archived'] as const).map(t => (
            <button key={t} onClick={() => setTab(t)} className={`px-4 py-2.5 text-sm font-medium transition-colors relative ${tab === t ? 'text-primary' : 'text-muted-foreground hover:text-foreground'}`}>
              {t}
              {tab === t && <motion.div layoutId="proj-tab" className="absolute bottom-0 left-0 right-0 h-0.5 gradient-primary rounded-full" />}
            </button>
          ))}
        </div>

        <div className="flex justify-end">
          <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 w-64">
            <Search className="w-4 h-4 text-muted-foreground" />
            <input value={search} onChange={e => setSearch(e.target.value)} placeholder="Search" className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground" />
          </div>
        </div>

        <div className="bg-card rounded-xl border border-border overflow-x-auto">
          <table className="w-full min-w-[700px]">
            <thead>
              <tr className="border-b border-border bg-accent/30">
                {['Project Name', 'Key', 'Time Spent', 'Task Count', 'Assignee', 'Actions'].map(h => (
                  <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {filtered.map((proj, i) => (
                <motion.tr key={proj.id} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: i * 0.03 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                  <td className="px-4 py-3 text-sm font-medium text-foreground">{proj.name}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{proj.key}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{proj.timeSpent}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{proj.taskCount}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{proj.assignee}</td>
                  <td className="px-4 py-3">
                    <button className="p-1.5 rounded hover:bg-muted transition-colors"><MoreVertical className="w-4 h-4 text-muted-foreground" /></button>
                  </td>
                </motion.tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <p className="text-sm text-muted-foreground">Settings &gt; Integrations</p>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {integrations.map((intg, i) => (
          <motion.div key={intg.id} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all">
            <div className="flex items-center gap-3 mb-3">
              <div className="w-12 h-12 rounded-xl flex items-center justify-center" style={{ backgroundColor: intg.color + '22' }}>
                <FolderKanban className="w-6 h-6" style={{ color: intg.color }} />
              </div>
              <h3 className="font-display font-bold text-foreground">{intg.name}</h3>
            </div>
            <p className="text-sm text-muted-foreground mb-4">{intg.description}</p>
            <div className="flex gap-2">
              <button onClick={() => setView('projects')} className="gradient-primary text-primary-foreground px-3 py-1.5 rounded-lg text-sm font-medium hover:opacity-90">View Projects</button>
              <button className="border border-primary text-primary px-3 py-1.5 rounded-lg text-sm font-medium hover:bg-accent">Reconfigure</button>
            </div>
          </motion.div>
        ))}
      </div>
    </div>
  );
}
