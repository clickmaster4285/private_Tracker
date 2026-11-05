'use client';

import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Legend, AreaChart, Area } from 'recharts';

const hourlyData = Array.from({ length: 10 }, (_, i) => ({
  hour: `${8 + i}:00`,
  active: Math.floor(Math.random() * 50) + 20,
  idle: Math.floor(Math.random() * 15) + 2,
}));

const dailyTrend = [
  { day: 'Mon', activeHours: 7.2, sessions: 12 },
  { day: 'Tue', activeHours: 6.8, sessions: 10 },
  { day: 'Wed', activeHours: 8.1, sessions: 15 },
  { day: 'Thu', activeHours: 7.5, sessions: 11 },
  { day: 'Fri', activeHours: 6.0, sessions: 9 },
  { day: 'Sat', activeHours: 2.5, sessions: 3 },
  { day: 'Sun', activeHours: 0.5, sessions: 1 },
];

export default function ActivityChart() {
  return (
    <div className="space-y-6 animate-fade-in">
      <div className="flex flex-col sm:flex-row gap-3">
        <select className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground">
          <option>All Employees</option>
        </select>
        <div className="text-sm text-muted-foreground bg-card border border-border rounded-lg px-3 py-2">
          02-Feb-2026 To 03-Mar-2026
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Hourly Activity Distribution</h3>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart data={hourlyData}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="hour" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
                <Legend />
                <Bar dataKey="active" fill="hsl(var(--primary))" radius={[3, 3, 0, 0]} />
                <Bar dataKey="idle" fill="hsl(38, 92%, 55%)" radius={[3, 3, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>

        <div className="bg-card rounded-xl border border-border p-5">
          <h3 className="font-display font-bold text-foreground mb-4">Daily Active Hours Trend</h3>
          <div className="h-[300px]">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={dailyTrend}>
                <CartesianGrid strokeDasharray="3 3" stroke="hsl(var(--border))" />
                <XAxis dataKey="day" stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <YAxis stroke="hsl(var(--muted-foreground))" fontSize={12} />
                <Tooltip contentStyle={{ backgroundColor: 'hsl(var(--card))', border: '1px solid hsl(var(--border))', borderRadius: '0.5rem' }} />
                <Legend />
                <Area type="monotone" dataKey="activeHours" fill="hsl(var(--primary) / 0.2)" stroke="hsl(var(--primary))" strokeWidth={2} />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </div>
      </div>
    </div>
  );
}
