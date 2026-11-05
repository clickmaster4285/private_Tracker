'use client';

import { motion } from 'framer-motion';
import {
  Gauge,
  ListChecks,
  Scale,
  Settings2,
  Sliders,
  Sparkles,
  TimerReset,
  Wand2,
} from 'lucide-react';

const FEATURES = [
  {
    icon: Sliders,
    title: 'Weighted scoring',
    description:
      'Tune productive, unproductive, and neutral weights per app, category, or department.',
  },
  {
    icon: TimerReset,
    title: 'Time-of-day rules',
    description:
      'Boost or discount activity during meetings, lunch hours, and shift boundaries.',
  },
  {
    icon: ListChecks,
    title: 'Exception lists',
    description:
      'Allowlist critical apps and blocklist distractions without rewriting the whole policy.',
  },
  {
    icon: Scale,
    title: 'Per-role baselines',
    description:
      'Different rules for engineers, sales, and support — fair scores across teams.',
  },
  {
    icon: Settings2,
    title: 'Override queue',
    description:
      'Review and resolve disputes where an employee contests their score for the day.',
  },
  {
    icon: Wand2,
    title: 'Suggested presets',
    description:
      'Industry-aware starting points (engineering, marketing, support) you can adopt in one click.',
  },
];

/**
 * Route shell for /configuration/productivity-rules.
 *
 * Productivity scoring lives in /productivity-scoring already; this dedicated
 * configuration surface will host the rule editor (weights, exceptions,
 * per-department policies) in a future release. Following the same gate
 * pattern as /gps-location and /employee-journey/location, we render a
 * clear "coming soon" state so the sidebar entry is honest and the route
 * is reachable today.
 */
export default function ProductivityRulesPage() {
  return (
    <div className="animate-fade-in min-h-[min(72vh,640px)] flex items-center justify-center py-10 px-4">
      <motion.div
        initial={{ opacity: 0, y: 16 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.45, ease: 'easeOut' }}
        className="relative w-full max-w-3xl"
      >
        <div
          className="absolute -inset-4 rounded-3xl bg-gradient-to-br from-primary/15 via-transparent to-cyan-500/10 blur-2xl pointer-events-none"
          aria-hidden
        />

        <div className="relative bg-card border border-border rounded-2xl shadow-lg overflow-hidden">
          <div className="h-1.5 w-full bg-gradient-to-r from-primary via-violet-500 to-cyan-500" />

          <div className="px-8 pt-10 pb-8 sm:px-12 sm:pt-12 sm:pb-10 text-center">
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
                  <Gauge className="w-7 h-7 text-primary-foreground" strokeWidth={2.2} />
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
              Productivity Rules
            </h2>
            <p className="text-sm sm:text-base text-muted-foreground max-w-lg mx-auto leading-relaxed">
              A dedicated editor for the weights, exceptions, and per-department policies
              that power the daily score. Use the live <span className="font-medium text-foreground">Productivity</span> view today;
              fine-grained rule configuration is on the way.
            </p>
          </div>

          <div className="px-6 sm:px-10 pb-10 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {FEATURES.map((f, i) => (
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
              Scoring infrastructure is live in <span className="font-medium text-foreground">/productivity-scoring</span> — this
              configuration surface ships in a future release.
            </p>
          </div>
        </div>
      </motion.div>
    </div>
  );
}
