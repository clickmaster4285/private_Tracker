'use client'

import { motion } from 'framer-motion';
import { Users, Trophy, DollarSign, AlertTriangle } from 'lucide-react';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import StatsCard from '@/components/ui/StatsCard';

const deptData = [
  { dept: 'Engineering', score: 85 }, { dept: 'Design', score: 78 },
  { dept: 'Marketing', score: 72 }, { dept: 'Sales', score: 68 },
  { dept: 'HR', score: 80 }, { dept: 'Finance', score: 75 },
];

export default function ExecutiveDashboard() {
  return (
    <div className="space-y-6 animate-fade-in">
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard title="Total Workforce Tracked" value="247" icon={Users} subtitle="Active employees" delay={0.05} />
        <StatsCard title="Org Productivity Score" value="79" icon={Trophy} change={3} delay={0.1} />
        <StatsCard title="Cost per Productive Hour" value="$24.50" icon={DollarSign} subtitle="USD" delay={0.15} />
        <StatsCard title="Departments at Risk" value="2" icon={AlertTriangle} subtitle="Below threshold" delay={0.2} />
      </div>

      <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.25 }} className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-bold text-foreground mb-4">Department Productivity</h3>
        <div className="h-[300px]">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={deptData} layout="vertical">
              <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
              <XAxis type="number" domain={[0, 100]} stroke="hsl(var(--muted-foreground))" fontSize={12} />
              <YAxis dataKey="dept" type="category" stroke="hsl(var(--muted-foreground))" fontSize={12} width={100} />
              <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
              <Bar dataKey="score" fill="hsl(var(--primary))" radius={[0, 4, 4, 0]} />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </motion.div>
    </div>
  );
}
