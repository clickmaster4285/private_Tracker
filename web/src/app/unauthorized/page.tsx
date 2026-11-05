'use client';

import { useRouter } from 'next/navigation';
import { motion } from 'framer-motion';
import { ShieldX, ArrowLeft, LayoutDashboard } from 'lucide-react';
import { APP_SHORT_NAME } from '@/config';

export default function UnauthorizedPage() {
  const router = useRouter();

  return (
    <div className="min-h-screen flex items-center justify-center bg-background px-4">
      <motion.div
        initial={{ opacity: 0, y: 16 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.3 }}
        className="bg-card rounded-2xl border border-border shadow-card max-w-md w-full p-8 text-center"
      >
        <div className="w-16 h-16 mx-auto rounded-2xl bg-destructive/10 flex items-center justify-center mb-5">
          <ShieldX className="w-8 h-8 text-destructive" />
        </div>
        <h1 className="font-display font-bold text-2xl text-foreground mb-2">Access denied</h1>
        <p className="text-sm text-muted-foreground mb-1">
          Your role does not have permission to view this page.
        </p>
        <p className="text-xs text-muted-foreground mb-6">
          If you believe this is a mistake, ask an administrator to grant your role
          access to this module in Roles &amp; Permissions.
        </p>
        <div className="flex items-center justify-center gap-3">
          <button
            onClick={() => router.back()}
            className="flex items-center gap-2 bg-card border border-border text-foreground px-4 py-2 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
          >
            <ArrowLeft className="w-4 h-4" /> Go back
          </button>
          <button
            onClick={() => router.replace('/dashboard')}
            className="flex items-center gap-2 gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity"
          >
            <LayoutDashboard className="w-4 h-4" /> Dashboard
          </button>
        </div>
      </motion.div>

      <p className="fixed bottom-4 text-[11px] text-muted-foreground/50 select-none">{APP_SHORT_NAME}</p>
    </div>
  );
}
