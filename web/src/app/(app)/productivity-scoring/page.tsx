'use client';

import { motion } from 'framer-motion';
import { Trophy, Activity, Target, Zap } from 'lucide-react';
import { LineChart, Line, PieChart, Pie, Cell, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import StatsCard from '@/components/ui/StatsCard';
import { useAuth } from '@/lib/auth';

const scoreHistory = [
  { day: 'Mon', score: 78 }, { day: 'Tue', score: 82 }, { day: 'Wed', score: 91 },
  { day: 'Thu', score: 75 }, { day: 'Fri', score: 88 }, { day: 'Sat', score: 42 }, { day: 'Sun', score: 0 },
];

const scoreBreakdown = [
  { name: 'Activity', value: 40, color: 'hsl(262, 80%, 50%)' },
  { name: 'Outcome', value: 45, color: 'hsl(152, 60%, 45%)' },
  { name: 'Focus', value: 15, color: 'hsl(38, 92%, 55%)' },
];

const employees = [
  { name: 'Yashodhan Kalia', overall: 92, activity: 88, outcome: 95, focus: 90, dept: 'Engineering' },
  { name: 'Priya Mehta', overall: 87, activity: 85, outcome: 88, focus: 92, dept: 'Design' },
  { name: 'Rakesh Pathania', overall: 84, activity: 90, outcome: 78, focus: 85, dept: 'Engineering' },
  { name: 'Arush Sharma', overall: 79, activity: 75, outcome: 82, focus: 80, dept: 'Engineering' },
  { name: 'Stuti Srivastava', overall: 73, activity: 70, outcome: 75, focus: 78, dept: 'Design' },
];

const getScoreColor = (score: number) => {
  if (score >= 80) return 'text-success';
  if (score >= 60) return 'text-warning';
  return 'text-destructive';
};

export default function ProductivityScoringPage() {
  const { user } = useAuth();
  const canSeeAll = user?.role === 'super_admin' || user?.role === 'org_admin';

  // Non-admin users only see their own scores
  const visibleEmployees = canSeeAll ? employees : employees.filter(e => e.name === user?.name).length > 0
    ? employees.filter(e => e.name === user?.name)
    : [{ name: user?.name || 'You', overall: 82, activity: 80, outcome: 84, focus: 78, dept: user?.department || 'N/A' }];

  const avgOverall = Math.round(visibleEmployees.reduce((s, e) => s + e.overall, 0) / visibleEmployees.length);
  const avgActivity = Math.round(visibleEmployees.reduce((s, e) => s + e.activity, 0) / visibleEmployees.length);
  const avgOutcome = Math.round(visibleEmployees.reduce((s, e) => s + e.outcome, 0) / visibleEmployees.length);
  const avgFocus = Math.round(visibleEmployees.reduce((s, e) => s + e.focus, 0) / visibleEmployees.length);

  return (
    <div className="space-y-6 animate-fade-in">
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-4 gap-4">
        <StatsCard title={canSeeAll ? "Overall Score" : "Your Score"} value={String(avgOverall)} icon={Trophy} subtitle={canSeeAll ? "Team Average" : "Your Average"} delay={0.05} />
        <StatsCard title="Activity Score" value={String(avgActivity)} icon={Activity} change={5} delay={0.1} />
        <StatsCard title="Outcome Score" value={String(avgOutcome)} icon={Target} change={8} delay={0.15} />
        <StatsCard title="Focus Score" value={String(avgFocus)} icon={Zap} change={-2} delay={0.2} />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.25 }} className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">{canSeeAll ? 'Score Trend (7 Days)' : 'Your Score Trend (7 Days)'}</h3>
          <div className="h-[220px]">
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={scoreHistory}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} domain={[0, 100]} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
                <Line type="monotone" dataKey="score" stroke="hsl(var(--primary))" strokeWidth={2.5} dot={{ r: 4 }} />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </motion.div>

        <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.3 }} className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Score Breakdown</h3>
          <div className="h-[220px] flex items-center justify-center">
            <ResponsiveContainer width="100%" height="100%">
              <PieChart>
                <Pie data={scoreBreakdown} cx="50%" cy="50%" innerRadius={60} outerRadius={90} dataKey="value" label={({ name, value }) => `${name} ${value}%`}>
                  {scoreBreakdown.map((entry, i) => <Cell key={i} fill={entry.color} />)}
                </Pie>
                <Tooltip />
              </PieChart>
            </ResponsiveContainer>
          </div>
        </motion.div>
      </div>

      <motion.div initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: 0.35 }} className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['Employee', 'Department', 'Overall', 'Activity', 'Outcome', 'Focus'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {visibleEmployees.map((emp, i) => (
              <motion.tr key={emp.name} initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ delay: 0.4 + i * 0.03 }} className="border-b border-border last:border-0 hover:bg-muted/30">
                <td className="px-4 py-3 text-sm font-medium text-foreground">{emp.name}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground">{emp.dept}</td>
                <td className="px-4 py-3"><span className={`text-sm font-bold ${getScoreColor(emp.overall)}`}>{emp.overall}</span></td>
                <td className="px-4 py-3"><span className={`text-sm ${getScoreColor(emp.activity)}`}>{emp.activity}</span></td>
                <td className="px-4 py-3"><span className={`text-sm ${getScoreColor(emp.outcome)}`}>{emp.outcome}</span></td>
                <td className="px-4 py-3"><span className={`text-sm ${getScoreColor(emp.focus)}`}>{emp.focus}</span></td>
              </motion.tr>
            ))}
          </tbody>
        </table>
      </motion.div>
    </div>
  );
}
