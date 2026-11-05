'use client';

import ProtectedRoute from '@/components/layout/ProtectedRoute';
import RouteGuard from '@/components/layout/RouteGuard';

/**
 * Full-bleed shell for the live-stream theater popout (no app sidebar / top bar).
 * Auth + RBAC still apply via ProtectedRoute / RouteGuard.
 */
export default function LivePopoutLayout({ children }: { children: React.ReactNode }) {
  return (
    <ProtectedRoute>
      <RouteGuard>
        <div className="h-screen w-screen overflow-hidden bg-background">{children}</div>
      </RouteGuard>
    </ProtectedRoute>
  );
}
