'use client';

import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend, PieChart, Pie, Cell } from 'recharts';

const teamData = [
  { dept: 'Engineering', productive: 78, unproductive: 15, idle: 7 },
  { dept: 'Design', productive: 72, unproductive: 18, idle: 10 },
  { dept: 'Marketing', productive: 65, unproductive: 25, idle: 10 },
  { dept: 'Sales', productive: 60, unproductive: 28, idle: 12 },
  { dept: 'HR', productive: 55, unproductive: 30, idle: 15 },
  { dept: 'QA', productive: 70, unproductive: 20, idle: 10 },
];

const pieData = [
  { name: 'Productive', value: 62, color: 'hsl(152, 60%, 45%)' },
  { name: 'Unproductive', value: 25, color: 'hsl(0, 72%, 55%)' },
  { name: 'Idle', value: 8, color: 'hsl(38, 92%, 55%)' },
  { name: 'Neutral', value: 5, color: 'hsl(210, 15%, 70%)' },
];

export default function ProductivityChart() {
  return (
    <div className="space-y-6 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>All Departments</option>
        </select>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          02-Feb-2026 To 03-Mar-2026
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Productivity by Department</h3>
          <div className="h-[350px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={teamData} layout="vertical">
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis type="number" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis dataKey="dept" type="category" stroke="hsl(var(--muted-foreground))" fontSize={12} width={90} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
                <Legend />
                <Bar dataKey="productive" fill="hsl(152, 60%, 45%)" stackId="a" radius={[0, 0, 0, 0]} />
                <Bar dataKey="unproductive" fill="hsl(0, 72%, 55%)" stackId="a" />
                <Bar dataKey="idle" fill="hsl(38, 92%, 55%)" stackId="a" radius={[0, 3, 3, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Overall Productivity Split</h3>
          <div className="h-[350px]">
            <ResponsiveContainer width="100%" height="100%">
              <PieChart>
                <Pie data={pieData} cx="50%" cy="50%" innerRadius={70} outerRadius={120} paddingAngle={3} dataKey="value" label={({ name, value }) => `${name} ${value}%`}>
                  {pieData.map((entry, i) => <Cell key={i} fill={entry.color} />)}
                </Pie>
                <Tooltip formatter={(v: number) => `${v}%`} />
              </PieChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>
    </div>
  );
}
