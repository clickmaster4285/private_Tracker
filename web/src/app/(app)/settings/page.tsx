'use client';

import { motion } from 'framer-motion';
import { CreditCard, Bell, Shield, BarChart3, Clock, Settings as SettingsIcon, HardDrive, Link, Download } from 'lucide-react';
import { useRouter } from 'next/navigation';
import { APP_SHORT_NAME } from '@/config';

const settingsCards = [
  { title: 'Billing', description: 'Effortlessly manage your subscriptions and stay on top of your payments.', icon: CreditCard, color: 'hsl(38, 92%, 55%)', bg: 'hsl(38, 92%, 95%)' },
  { title: 'Create New Alert', description: 'Set up new alerts to receive notifications based on your preferences.', icon: Bell, color: 'hsl(0, 72%, 55%)', bg: 'hsl(0, 72%, 95%)' },
  { title: 'Role Management', description: 'View and manage user roles in your organization.', icon: Shield, color: 'hsl(210, 80%, 55%)', bg: 'hsl(210, 80%, 95%)' },
  { title: 'Productivity Settings', description: 'Configure settings to optimize productivity tracking for your team.', icon: BarChart3, color: 'hsl(38, 70%, 55%)', bg: 'hsl(38, 70%, 95%)' },
  { title: 'Shift Settings', description: "Set and adjust work shifts as per your organization's needs.", icon: Clock, color: 'hsl(152, 60%, 45%)', bg: 'hsl(152, 60%, 95%)' },
  { title: 'Tracking Settings', description: 'Add and refine new tracking parameters for precise activity monitoring.', icon: SettingsIcon, color: 'hsl(152, 60%, 45%)', bg: 'hsl(152, 60%, 95%)', path: '/settings/tracking' },
  { title: 'Storage Integrations', description: `Integrate your Storage applications with ${APP_SHORT_NAME}.`, icon: HardDrive, color: 'hsl(210, 80%, 55%)', bg: 'hsl(210, 80%, 95%)' },
  { title: 'Integrations', description: `Integrate your project management applications with ${APP_SHORT_NAME}.`, icon: Link, color: 'hsl(262, 80%, 50%)', bg: 'hsl(262, 80%, 95%)' },
  { title: 'Download Report', description: 'Access a detailed breakdown of your data.', icon: Download, color: 'hsl(262, 60%, 50%)', bg: 'hsl(262, 60%, 95%)' },
];

export default function SettingsPage() {
  const router = useRouter();

  return (
    <div className="animate-fade-in">
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
        {settingsCards.map((card, i) => (
          <motion.button
            key={card.title}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.04 }}
            onClick={() => card.path && router.push(card.path)}
            className="bg-card rounded-xl border border-border p-6 shadow-card hover:shadow-card-hover transition-all text-left group"
            style={{ background: `linear-gradient(135deg, ${card.bg}, hsl(var(--card)))` }}
          >
            <div className="w-12 h-12 rounded-xl flex items-center justify-center mb-4" style={{ backgroundColor: card.color + '22' }}>
              <card.icon className="w-6 h-6" style={{ color: card.color }} />
            </div>
            <h3 className="font-display font-bold text-foreground mb-1">{card.title}</h3>
            <p className="text-sm text-muted-foreground">{card.description}</p>
          </motion.button>
        ))}
      </div>
    </div>
  );
}
