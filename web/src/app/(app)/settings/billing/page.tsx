'use client';

import { motion } from 'framer-motion';
import { CreditCard, Calendar, Users, ArrowUpRight } from 'lucide-react';
import { Button } from '@/components/ui/button';

export default function SettingsBillingPage() {
  return (
    <div className="space-y-6 animate-fade-in">
      <h3 className="font-display font-bold text-lg text-foreground">Billing & Subscription</h3>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4">
        {[
          { label: 'Current Plan', value: 'Growth', icon: CreditCard, color: 'text-primary' },
          { label: 'Billing Period', value: 'Annual', icon: Calendar, color: 'text-success' },
          { label: 'Next Renewal', value: 'Apr 01, 2026', icon: Calendar, color: 'text-warning' },
          { label: 'Seats Used', value: '47 / 50', icon: Users, color: 'text-info' },
        ].map((item, i) => (
          <motion.div key={i} initial={{ opacity: 0, y: 10 }} animate={{ opacity: 1, y: 0 }} transition={{ delay: i * 0.05 }} className="bg-card rounded-xl border border-border p-5">
            <div className="flex items-center gap-2 mb-2">
              <item.icon className={`w-5 h-5 ${item.color}`} />
              <p className="text-sm text-muted-foreground">{item.label}</p>
            </div>
            <p className={`text-xl font-display font-bold ${item.color}`}>{item.value}</p>
          </motion.div>
        ))}
      </div>

      <div className="bg-card rounded-xl border border-border p-6">
        <h4 className="font-display font-bold text-foreground mb-4">Plan Comparison</h4>
        <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
          {['Starter', 'Growth', 'Business', 'Enterprise'].map((plan, i) => (
            <div key={plan} className={`rounded-xl border p-5 ${plan === 'Growth' ? 'border-primary bg-primary/5 ring-2 ring-primary/20' : 'border-border'}`}>
              <h5 className="font-display font-bold text-foreground mb-1">{plan}</h5>
              <p className="text-xs text-muted-foreground mb-3">{plan === 'Enterprise' ? 'Contact sales' : `$${[9, 29, 79][i]}/seat/mo`}</p>
              {plan === 'Growth' && <span className="px-2 py-0.5 rounded-full text-[10px] font-medium bg-primary/15 text-primary">Current</span>}
            </div>
          ))}
        </div>
        <Button className="mt-4 gap-1" variant="outline"><ArrowUpRight className="w-4 h-4" /> Upgrade Plan</Button>
      </div>
    </div>
  );
}
