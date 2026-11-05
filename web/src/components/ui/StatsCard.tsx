import { motion } from 'framer-motion';
import { LucideIcon, TrendingUp } from 'lucide-react';

interface StatsCardProps {
  title: string;
  value: string | number;
  icon: LucideIcon;
  change?: number;
  subtitle?: string;
  subtitleColor?: string;
  delay?: number;
}

export default function StatsCard({ title, value, icon: Icon, change, subtitle, subtitleColor, delay = 0 }: StatsCardProps) {
  return (
    <motion.div
      initial={{ opacity: 0, y: 12 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ delay, duration: 0.4 }}
      className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-shadow duration-300"
    >
      <div className="flex items-start justify-between mb-3">
        <p className="text-sm font-medium text-muted-foreground">{title}</p>
        <div className="p-2 rounded-lg bg-accent">
          <Icon className="w-4 h-4 text-accent-foreground" />
        </div>
      </div>
      <p className="text-2xl font-display font-bold text-foreground">{value}</p>
      <div className="flex items-center gap-2 mt-2">
        {subtitle && (
          <span className={`text-xs font-medium ${subtitleColor || 'text-muted-foreground'}`}>{subtitle}</span>
        )}
        {change !== undefined && (
          <span className="flex items-center gap-1 text-xs font-medium text-success">
            <TrendingUp className="w-3 h-3" />
            +{change}%
          </span>
        )}
      </div>
    </motion.div>
  );
}
