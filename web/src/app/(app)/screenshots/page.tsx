'use client';

import { Camera } from 'lucide-react';
import EmptyState from '@/components/employees/EmptyState';

export default function Screenshots() {
  return (
    <div className="space-y-4 animate-fade-in">
      <div className="bg-card rounded-xl border border-border p-5">
        <h3 className="font-display font-semibold text-foreground mb-4">Screenshots</h3>
        <EmptyState
          icon={Camera}
          text="Screenshots are not collected yet — the desktop client does not capture them. This page will populate once screenshot capture ships."
        />
      </div>
    </div>
  );
}
