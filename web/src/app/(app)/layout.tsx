"use client";

import AppLayout from "@/components/layout/AppLayout";
import ProtectedRoute from "@/components/layout/ProtectedRoute";
import RouteGuard from "@/components/layout/RouteGuard";

export default function DashboardLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <ProtectedRoute>
      <AppLayout>
        <RouteGuard>{children}</RouteGuard>
      </AppLayout>
    </ProtectedRoute>
  );
}
