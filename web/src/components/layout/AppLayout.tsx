"use client";

import { useState } from "react";
import { usePathname } from "next/navigation";
import { APP_NAME } from "@/config";
import AppSidebar from "./AppSidebar";
import TopBar from "./TopBar";

const pageTitles: Record<string, string> = {
  "/dashboard": "Dashboard",
  "/employees": "Employees",
  "/apps": "Apps and Websites",
  "/configuration/apps": "Applications",
  "/configuration/websites": "Websites",
  "/configuration/categories": "Categories & Types",
  "/configuration/productivity-rules": "Productivity Rules",
  "/screenshots": "Screenshots",
  "/logs/insights": "User Insights",
  "/logs/graphical": "Graphical Logs",
  "/logs/comprehensive": "Comprehensive Logs",
  "/charts/productivity": "Productivity Chart",
  "/charts/activity": "Activity Chart",
  "/departments": "Departments",
  "/kpis": "KPIs & KRAs",
  "/roles": "Roles",
  "/live-stream": "Live Stream",
  "/emails": "Emails & Alerts",
  "/projects": "Projects",
  "/ai-summary": "AI Summary",
  "/hours-insights": "Hours Insights",
  "/settings": "Settings",
  "/settings/tracking": "Tracking Settings",
  "/onboarding": "Onboarding",
  "/timesheets": "Timesheets",
  "/attendance": "Attendance Log",
  "/shifts": "Shift Management",
  "/holidays": "Company Holidays",
  "/gps-location": "GPS & Location",
  "/productivity-scoring": "Productivity Scoring",
  "/goals": "Goals & OKRs",
  "/reports": "Reports & Analytics",
  "/audit-log": "Audit Log",
  "/executive-dashboard": "Executive Dashboard",
  "/dlp-alerts": "DLP Alerts",
  "/dlp-rules": "DLP Rules",
  "/web-blocker": "Web Blocker",
  "/shadow-it": "Shadow IT",
  "/settings/billing": "Billing & Subscription",
  "/settings/compliance": "GDPR & Compliance",
  "/settings/security": "Security Settings",
  "/settings/notifications": "Notification Config",
  "/settings/user-management": "User Management",
  "/settings/profile": "My Profile",
};

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const [collapsed, setCollapsed] = useState(false);
  const [mobileOpen, setMobileOpen] = useState(false);
  const pathname = usePathname();

  const title = pageTitles[pathname] || APP_NAME;

  return (
    <div className="flex h-screen overflow-hidden bg-background">
      <AppSidebar
        collapsed={collapsed}
        onToggle={() => setCollapsed(!collapsed)}
        mobileOpen={mobileOpen}
        onMobileClose={() => setMobileOpen(false)}
      />
      <div className="flex-1 flex flex-col overflow-hidden">
        <TopBar title={title} onMenuClick={() => setMobileOpen(true)} />
        <main className="flex-1 overflow-y-auto p-4 lg:p-6">{children}</main>
      </div>
    </div>
  );
}
