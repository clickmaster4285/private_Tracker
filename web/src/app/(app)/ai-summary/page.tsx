'use client';

import { Sparkles } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function AISummary() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">AI Summary</h3>
        <EmptyState
          icon={Sparkles}
          text="AI summaries are not generated yet — there is no analysis endpoint. This page will populate once AI summarization ships."
        />
      </div>
    </div>
  );
}
