'use client';

import { motion } from 'framer-motion';
import {
  MapPin,
  Navigation,
  Radar,
  Route,
  Shield,
  Sparkles,
} from 'lucide-react';

type Variant = 'fleet' | 'trail';

const FEATURES: Record<Variant, { icon: React.ElementType; title: string; description: string }[]> = {
  fleet: [
    {
      icon: MapPin,
      title: 'Location log',
      description: 'Fleet-wide samples with source, accuracy, and timestamps.',
    },
    {
      icon: Navigation,
      title: 'Geofence zones',
      description: 'Define office sites and track inside / outside status.',
    },
    {
      icon: Shield,
      title: 'Permission-aware',
      description: 'Only employees who grant OS location appear in reports.',
    },
  ],
  trail: [
    {
      icon: Route,
      title: 'Per-employee trail',
      description: 'Chronological coordinates for one employee’s device.',
    },
    {
      icon: Radar,
      title: 'GPS & WiFi fixes',
      description: 'Precise when the OS provides a fix — not IP guesses.',
    },
    {
      icon: MapPin,
      title: 'Journey context',
      description: 'Pair location history with session and attendance data.',
    },
  ],
};

const COPY: Record<Variant, { heading: string; subheading: string }> = {
  fleet: {
    heading: 'GPS & Location',
    subheading:
      'Fleet location tracking, geofence zones, and compliance-ready permission flows are on the way.',
  },
  trail: {
    heading: 'Location Trail',
    subheading:
      'Per-employee location history will appear here once the location module is released.',
  },
};

export default function LocationComingSoon({ variant = 'fleet' }: { variant?: Variant }) {
  const { heading, subheading } = COPY[variant];
  const features = FEATURES[variant];

  return (
    <div className="animate-fade-in min-h-[min(72vh,640px)] flex items-center justify-center py-10 px-4">
      <motion.div
        initial={{ opacity: 0, y: 16 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.45, ease: 'easeOut' }}
        className="relative w-full max-w-2xl"
      >
        {/* Ambient glow */}
        <div
          className="absolute -inset-4 rounded-3xl bg-gradient-to-br from-primary/15 via-transparent to-cyan-500/10 blur-2xl pointer-events-none"
          aria-hidden
        />

        <div className="relative bg-card border border-border rounded-2xl shadow-lg overflow-hidden">
          {/* Top gradient band */}
          <div className="h-1.5 w-full bg-gradient-to-r from-primary via-violet-500 to-cyan-500" />

          <div className="px-8 pt-10 pb-8 sm:px-12 sm:pt-12 sm:pb-10 text-center">
            {/* Icon cluster */}
            <div className="relative mx-auto w-20 h-20 mb-8">
              <motion.div
                className="absolute inset-0 rounded-2xl bg-primary/10"
                animate={{ scale: [1, 1.08, 1], opacity: [0.5, 0.25, 0.5] }}
                transition={{ duration: 3, repeat: Infinity, ease: 'easeInOut' }}
              />
              <motion.div
                className="absolute inset-2 rounded-xl bg-primary/15"
                animate={{ scale: [1, 1.05, 1] }}
                transition={{ duration: 2.2, repeat: Infinity, ease: 'easeInOut', delay: 0.3 }}
              />
              <div className="absolute inset-0 flex items-center justify-center">
                <div className="w-14 h-14 rounded-xl bg-primary flex items-center justify-center shadow-md">
                  <MapPin className="w-7 h-7 text-primary-foreground" strokeWidth={2.2} />
                </div>
              </div>
            </div>

            <motion.span
              initial={{ opacity: 0, scale: 0.9 }}
              animate={{ opacity: 1, scale: 1 }}
              transition={{ delay: 0.15 }}
              className="inline-flex items-center gap-1.5 px-3 py-1 rounded-full text-xs font-semibold tracking-wide uppercase bg-primary/10 text-primary border border-primary/20 mb-5"
            >
              <Sparkles className="w-3.5 h-3.5" />
              Coming soon
            </motion.span>

            <h2 className="font-display text-2xl sm:text-3xl font-bold text-foreground tracking-tight mb-3">
              {heading}
            </h2>
            <p className="text-sm sm:text-base text-muted-foreground max-w-md mx-auto leading-relaxed">
              {subheading}
            </p>
          </div>

          {/* Feature preview cards */}
          <div className="px-6 sm:px-10 pb-10 grid gap-3 sm:grid-cols-3">
            {features.map((f, i) => (
              <motion.div
                key={f.title}
                initial={{ opacity: 0, y: 12 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: 0.2 + i * 0.08 }}
                className="rounded-xl border border-border/80 bg-muted/30 p-4 text-left hover:bg-muted/50 transition-colors"
              >
                <div className="w-9 h-9 rounded-lg bg-background border border-border flex items-center justify-center mb-3">
                  <f.icon className="w-4 h-4 text-primary" />
                </div>
                <p className="text-sm font-semibold text-foreground mb-1">{f.title}</p>
                <p className="text-xs text-muted-foreground leading-relaxed">{f.description}</p>
              </motion.div>
            ))}
          </div>

          <div className="px-8 py-4 border-t border-border bg-muted/20 text-center">
            <p className="text-xs text-muted-foreground">
              Backend APIs and desktop collectors are in place — this dashboard will light up in a future release.
            </p>
          </div>
        </div>
      </motion.div>
    </div>
  );
}
