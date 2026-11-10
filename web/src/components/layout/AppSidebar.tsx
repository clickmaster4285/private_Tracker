"use client";

import { useState, useEffect } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { APP_SHORT_NAME } from "@/config";
import { motion, AnimatePresence } from "framer-motion";
import {
  LayoutDashboard, Users, AppWindow, Camera, FileText, BarChart3,
  Building2, Target, Shield, Radio, Mail, FolderKanban, Sparkles,
  Clock, Settings, ChevronLeft, ChevronDown, ChevronRight, X,
  ClipboardList, UserCheck, MapPin, Trophy, FileBarChart, AlertTriangle,
  Eye, CalendarDays, Navigation, Route,
  Monitor, ListChecks, Globe2,
} from "lucide-react";
import { useAuth } from "@/lib/auth";
import { usePermissions } from "@/lib/permissions";

// ─── Types ──────────────────────────────────────────────────────────────────

interface NavChild {
  label: string;
  path: string;
  module?: string;
}

interface NavItem {
  label: string;
  icon: React.ElementType;
  path?: string;
  module?: string;
  children?: NavChild[];
}

interface NavSection {
  title?: string; // omit for the first unlabelled block
  items: NavItem[];
}

// ─── Navigation structure ────────────────────────────────────────────────────
//
// Sections give the sidebar a clear information hierarchy without adding visual
// weight — each section title is a quiet all-caps label, not a heading.

const navSections: NavSection[] = [
  // ── Top-level ──────────────────────────────────────────────────────────────
  {
    items: [
      {
        label: "Dashboard",
        icon: LayoutDashboard,
        path: "/dashboard",
        module: "dashboard",
      },
    ],
  },

  // ── People ─────────────────────────────────────────────────────────────────
  {
    title: "People",
    items: [
      {
        label: "HR",
        icon: Users,
        children: [
          {
            label: "Employees",
            path: "/employees",
            module: "users",
          },
          {
            label: "Departments",
            path: "/departments",
            module: "departments",
          },
          {
            label: "Roles",
            path: "/roles",
            module: "roles",
          },
          {
            label: "KPIs & KRAs",
            path: "/kpis",
            module: "kpis",
          },
          // {
          //   label: "Onboarding",
          //   path: "/onboarding",
          //   module: "onboarding",
          // },
          {
            label: "Shift Management",
            path: "/shifts",
            module: "shifts",
          },
          {
            label: "Company Holidays",
            path: "/holidays",
            module: "shifts",
          },
        ],
      },

      {
        label: "Time & Attendance",
        icon: CalendarDays,
        children: [
          {
            label: "Timesheets",
            path: "/timesheets",
            module: "timesheets",
          },
          {
            label: "Attendance Log",
            path: "/attendance",
            module: "attendance",
          },
          {
            label: "GPS & Location",
            path: "/gps-location",
            module: "gps-location",
          },
          {
            label: "Hours Insights",
            path: "/hours-insights",
            module: "hours-insights",
          },
        ],
      },

      {
        label: "Productivity",
        icon: Trophy,
        children: [
          {
            label: "Score Card",
            path: "/productivity-scoring",
            module: "productivity-scoring",
          },
          {
            label: "Goals & OKRs",
            path: "/goals",
            module: "goals",
          },
        ],
      },
    ],
  },

  // ── Monitoring ─────────────────────────────────────────────────────────────
  {
    title: "Monitoring",
    items: [
      {
        label: "Employee Journey",
        icon: Route,
        children: [
          {
            label: "Session Timeline",
            path: "/employee-journey/timeline",
            module: "employee-journey",
          },
          {
            label: "App Usage",
            path: "/employee-journey/apps",
            module: "employee-journey",
          },
          {
            label: "Web Activity",
            path: "/employee-journey/web",
            module: "employee-journey",
          },
          {
            label: "Screenshots",
            path: "/employee-journey/screenshots",
            module: "employee-journey",
          },
          {
            label: "Location Trail",
            path: "/employee-journey/location",
            module: "employee-journey",
          },
        ],
      },

      {
        label: "Device Specs",
        icon: Monitor,
        children: [
          {
            label: "Hardware Overview",
            path: "/device-specs",
            module: "device-specs",
          },
          {
            label: "Installed Software",
            path: "/device-specs/software",
            module: "device-specs",
          },
          {
            label: "Peripherals",
            path: "/device-specs/peripherals",
            module: "device-specs",
          },
          {
            label: "Ports Control",
            path: "/device-specs/ports",
            module: "device-specs",
          },
          {
            label: "Permissions",
            path: "/device-specs/permissions",
            module: "device-specs",
          },
        ],
      },

      {
        label: "Screenshots",
        icon: Camera,
        path: "/screenshots",
        module: "screenshots",
      },

      {
        label: "Live Stream",
        icon: Radio,
        path: "/live-stream",
        module: "live-stream",
      },
    ],
  },

  // ── Configuration ──────────────────────────────────────────────────────────
  {
    title: "Configuration",
    items: [
      {
        label: "Apps & Websites",
        icon: AppWindow,
        children: [
          {
            label: "Applications",
            path: "/configuration/apps",
            module: "configuration/apps",
          },
          {
            label: "Websites",
            path: "/configuration/websites",
            module: "configuration/websites",
          },
        ],
      },

      {
        label: "Categories",
        icon: FolderKanban,
        path: "/configuration/categories",
        module: "configuration/categories",
      },

      {
        label: "Productivity Rules",
        icon: Target,
        path: "/configuration/productivity-rules",
        module: "configuration/productivity-rules",
      },
    ],
  },

  // ── Insights ───────────────────────────────────────────────────────────────
  {
    title: "Insights",
    items: [
      {
        label: "Logs",
        icon: FileText,
        children: [
          {
            label: "User Insights",
            path: "/logs/insights",
            module: "logs",
          },
          {
            label: "Graphical Logs",
            path: "/logs/graphical",
            module: "logs",
          },
          {
            label: "Comprehensive Logs",
            path: "/logs/comprehensive",
            module: "logs",
          },
        ],
      },

      {
        label: "Charts",
        icon: BarChart3,
        children: [
          {
            label: "Productivity",
            path: "/charts/productivity",
            module: "charts",
          },
          {
            label: "Activity",
            path: "/charts/activity",
            module: "charts",
          },
        ],
      },

      {
        label: "Reports & Analytics",
        icon: FileBarChart,
        children: [
          {
            label: "Reports",
            path: "/reports",
            module: "reports",
          },
          {
            label: "Audit Log",
            path: "/audit-log",
            module: "audit-log",
          },
          {
            label: "Executive Dashboard",
            path: "/executive-dashboard",
            module: "executive-dashboard",
          },
        ],
      },
    ],
  },

  // ── Security ───────────────────────────────────────────────────────────────
  {
    title: "Security",
    items: [
      {
        label: "Security & DLP",
        icon: AlertTriangle,
        children: [
          {
            label: "DLP Alerts",
            path: "/dlp-alerts",
            module: "dlp-alerts",
          },
          {
            label: "DLP Rules",
            path: "/dlp-rules",
            module: "dlp-rules",
          },
          {
            label: "Web Blocker",
            path: "/web-blocker",
            module: "web-blocker",
          },
          {
            label: "Shadow IT",
            path: "/shadow-it",
            module: "shadow-it",
          },
        ],
      },
      {
        label: "Allowlists",
        icon: ListChecks,
        children: [
          {
            label: "Web Allow Lists",
            path: "/allowlists/web",
            module: "allowlists/web",
          },
          {
            label: "App Allow List",
            path: "/allowlists/apps",
            module: "allowlists/apps",
          },
        ],
      },
    ],
  },

  // ── Workspace ──────────────────────────────────────────────────────────────
  {
    title: "Workspace",
    items: [
      {
        label: "Projects",
        icon: FolderKanban,
        path: "/projects",
        module: "projects",
      },
      {
        label: "Emails & Alerts",
        icon: Mail,
        path: "/emails",
        module: "emails",
      },
      {
        label: "AI Summary",
        icon: Sparkles,
        path: "/ai-summary",
        module: "ai-summary",
      },
    ],
  },

  // ── Settings ────────────────────────────────────────────────────────────────
  {
    items: [
      {
        label: "Settings",
        icon: Settings,
        children: [
          {
            label: "General",
            path: "/settings",
            module: "settings",
          },
          {
            // Self-service profile: visible to every authenticated user. No
            // `module` key means the sidebar's `isItemVisible` skips the
            // canAccess check, and `findModuleForPath` returns undefined for
            // RouteGuard — the page is reachable without a permission grant.
            label: "My Profile",
            path: "/settings/profile",
          },
          {
            label: "Tracking",
            path: "/settings/tracking",
            module: "settings/tracking",
          },
          {
            label: "User Management",
            path: "/settings/user-management",
            module: "settings/user-management",
          },
          {
            label: "Notifications",
            path: "/settings/notifications",
            module: "settings/notifications",
          },
          {
            label: "Billing",
            path: "/settings/billing",
            module: "settings/billing",
          },
          {
            label: "Compliance",
            path: "/settings/compliance",
            module: "settings/compliance",
          },
          {
            label: "Security",
            path: "/settings/security",
            module: "settings/security",
          },
        ],
      },
    ],
  },
];

// ─── Path → permission-module resolution ─────────────────────────────────────
//
// Single source of truth for navigation guards: every guarded route maps to the
// same module key the sidebar uses, so hidden nav items and blocked pages agree.

function collectModulePaths(): { path: string; module?: string }[] {
  return navSections.flatMap(s => s.items).flatMap(item => {
    const own = item.path ? [{ path: item.path, module: item.module }] : [];
    const children = (item.children ?? []).map(c => ({ path: c.path, module: c.module }));
    return [...own, ...children];
  });
}

const MODULE_PATHS = collectModulePaths();

/** Resolve the permission module key guarding a pathname (undefined = unguarded). */
export function findModuleForPath(pathname: string): string | undefined {
  for (const entry of MODULE_PATHS) {
    if (!entry.module) continue;
    if (entry.path === pathname) return entry.module;
  }
  // Nested/deep routes inherit the closest parent's module (e.g. /roles/sub → roles).
  let best: { length: number; module: string } | undefined;
  for (const entry of MODULE_PATHS) {
    if (!entry.module) continue;
    if (pathname.startsWith(entry.path + "/")) {
      if (!best || entry.path.length > best.length) {
        best = { length: entry.path.length, module: entry.module };
      }
    }
  }
  return best?.module;
}

// ─── Component ───────────────────────────────────────────────────────────────

interface AppSidebarProps {
  collapsed: boolean;
  onToggle: () => void;
  mobileOpen: boolean;
  onMobileClose: () => void;
}

export default function AppSidebar({
  collapsed,
  onToggle,
  mobileOpen,
  onMobileClose,
}: AppSidebarProps) {
  const pathname = usePathname();
  const [openMenus, setOpenMenus] = useState<string[]>([]);
  const { user } = useAuth();
  const { canAccess } = usePermissions();

  // Reset open menus when the user changes (e.g. after logout/login)
  useEffect(() => {
    if (user) setOpenMenus([]);
  }, [user]);

  // Keep parent menus open when the current path is inside one of their children.
  // This prevents the sidebar section from collapsing just because the user
  // navigated to a child page.
  useEffect(() => {
    if (!user) return;
    const expanded = navSections.flatMap(s => s.items).reduce<string[]>((acc, item) => {
      if (item.children && item.children.some(c => c.path === pathname)) {
        acc.push(item.label);
      }
      return acc;
    }, []);
    if (expanded.length > 0) {
      setOpenMenus(prev => {
        const next = new Set(prev);
        expanded.forEach(label => next.add(label));
        return Array.from(next);
      });
    }
  }, [pathname, user]);

  const toggleMenu = (label: string) => {
    setOpenMenus(prev =>
      prev.includes(label) ? prev.filter(l => l !== label) : [...prev, label]
    );
  };

  const isItemActive = (path?: string, children?: NavChild[]) => {
    if (path) return pathname === path;
    return children?.some(c => pathname === c.path) ?? false;
  };

  // Filter a single NavItem's visibility
  const isItemVisible = (item: NavItem): boolean => {
    if (!user) return false;
    if (item.module) return canAccess(user.role, item.module);
    if (item.children) return item.children.some(c => c.module ? canAccess(user.role, c.module) : true);
    return true;
  };

  const visibleChildrenOf = (children: NavChild[]): NavChild[] =>
    user ? children.filter(c => (c.module ? canAccess(user.role, c.module) : true)) : [];

  // ── Render a single nav item (leaf or group) ──
  const renderItem = (item: NavItem) => {
    if (!isItemVisible(item)) return null;
    const Icon = item.icon;
    const active = isItemActive(item.path, item.children);
    const isOpen = openMenus.includes(item.label);

    // ── Group (has children) ──
    if (item.children) {
      const visibleChildren = visibleChildrenOf(item.children);
      if (visibleChildren.length === 0) return null;

      return (
        <div key={item.label}>
          <button
            onClick={() => toggleMenu(item.label)}
            aria-expanded={isOpen}
            className={`
              w-full flex items-center gap-3 px-3 py-2 rounded-lg text-sm
              transition-colors duration-150
              ${active
                ? "bg-sidebar-accent text-sidebar-primary"
                : "text-sidebar-foreground/80 hover:bg-sidebar-accent/40 hover:text-sidebar-foreground"
              }
            `}
          >
            <Icon className="w-[17px] h-[17px] flex-shrink-0" />
            {!collapsed && (
              <>
                <span className="flex-1 text-left font-medium">{item.label}</span>
                <ChevronDown
                  className={`w-3.5 h-3.5 opacity-50 transition-transform duration-200 ${isOpen ? "rotate-180" : ""}`}
                />
              </>
            )}
          </button>

          <AnimatePresence initial={false}>
            {isOpen && !collapsed && (
              <motion.div
                initial={{ height: 0, opacity: 0 }}
                animate={{ height: "auto", opacity: 1 }}
                exit={{ height: 0, opacity: 0 }}
                transition={{ duration: 0.18, ease: "easeInOut" }}
                className="overflow-hidden"
              >
                <div className="ml-[17px] mt-0.5 mb-0.5 pl-3.5 border-l border-sidebar-border/60 space-y-0.5">
                  {visibleChildren.map(child => (
                    <Link
                      key={child.path}
                      href={child.path}
                      onClick={onMobileClose}
                      className={`
                        block px-2.5 py-1.5 rounded-md text-[13px] transition-colors duration-100
                        ${pathname === child.path
                          ? "text-sidebar-primary font-semibold bg-sidebar-accent/60"
                          : "text-sidebar-foreground/60 hover:text-sidebar-foreground hover:bg-sidebar-accent/30"
                        }
                      `}
                    >
                      {child.label}
                    </Link>
                  ))}
                </div>
              </motion.div>
            )}
          </AnimatePresence>
        </div>
      );
    }

    // ── Leaf link ──
    return (
      <Link
        key={item.label}
        href={item.path!}
        onClick={onMobileClose}
        className={`
          flex items-center gap-3 px-3 py-2 rounded-lg text-sm
          transition-colors duration-150
          ${active
            ? "bg-sidebar-accent text-sidebar-primary font-semibold"
            : "text-sidebar-foreground/80 hover:bg-sidebar-accent/40 hover:text-sidebar-foreground"
          }
        `}
      >
        <Icon className="w-[17px] h-[17px] flex-shrink-0" />
        {!collapsed && <span className="font-medium">{item.label}</span>}
      </Link>
    );
  };

  // ── Render a section (optional label + items) ──
  const renderSection = (section: NavSection, index: number) => {
    const visibleItems = section.items.filter(isItemVisible);
    if (visibleItems.length === 0) return null;

    return (
      <div key={index} className="space-y-0.5">
        {/* Section label — hidden when sidebar is collapsed */}
        {section.title && !collapsed && (
          <p className="px-3 pt-3 pb-1 text-[10px] font-semibold tracking-widest uppercase text-sidebar-foreground/30 select-none">
            {section.title}
          </p>
        )}
        {/* Spacer rule when collapsed so sections don't blur together */}
        {section.title && collapsed && (
          <div className="mx-3 my-2 border-t border-sidebar-border/30" />
        )}
        {section.items.map(renderItem)}
      </div>
    );
  };

  // ── Sidebar inner content ──
  const sidebarContent = (
    <div className="flex flex-col h-full">
      {/* Brand header */}
      <div className="flex items-center gap-3 px-4 py-4 border-b border-sidebar-border/50">
        <div className="w-8 h-8 rounded-lg overflow-hidden flex items-center justify-center flex-shrink-0 bg-white shadow-sm">
          <img src="/app-logo.png" alt="App logo" className="app-logo" />
        </div>
        {!collapsed && (
          <motion.div
            initial={{ opacity: 0 }}
            animate={{ opacity: 1 }}
            transition={{ duration: 0.15 }}
            className="flex flex-col min-w-0"
          >
            <span className="font-display font-bold text-[13px] text-sidebar-foreground tracking-tight leading-tight truncate">
              {APP_SHORT_NAME}
            </span>
            <span className="text-[10px] text-sidebar-foreground/40 tracking-tight leading-tight truncate">
              Monitoring & Productivity
            </span>
          </motion.div>
        )}
      </div>

      {/* Navigation */}
      <nav className="flex-1 overflow-y-auto py-3 px-2.5 space-y-0 scrollbar-thin scrollbar-thumb-sidebar-border">
        {navSections.map(renderSection)}
      </nav>

      {/* Collapse toggle (desktop only) */}
      <div className="hidden lg:block border-t border-sidebar-border/50 p-2.5">
        <button
          onClick={onToggle}
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          className="w-full flex items-center justify-center py-1.5 rounded-lg hover:bg-sidebar-accent/40 text-sidebar-foreground/50 hover:text-sidebar-foreground transition-colors"
        >
          {collapsed ? <ChevronRight className="w-4 h-4" /> : <ChevronLeft className="w-4 h-4" />}
        </button>
      </div>
    </div>
  );

  return (
    <>
      {/* Desktop sidebar */}
      <aside
        className={`
          hidden lg:flex flex-col bg-sidebar shadow-sidebar
          transition-all duration-300 ease-in-out
          ${collapsed ? "w-[64px]" : "w-[252px]"}
          h-screen sticky top-0 z-30
        `}
      >
        {sidebarContent}
      </aside>

      {/* Mobile drawer */}
      <AnimatePresence>
        {mobileOpen && (
          <>
            {/* Backdrop */}
            <motion.div
              initial={{ opacity: 0 }}
              animate={{ opacity: 0.5 }}
              exit={{ opacity: 0 }}
              transition={{ duration: 0.2 }}
              className="fixed inset-0 bg-foreground/50 z-40 lg:hidden"
              onClick={onMobileClose}
            />
            {/* Drawer */}
            <motion.aside
              initial={{ x: -270 }}
              animate={{ x: 0 }}
              exit={{ x: -270 }}
              transition={{ type: "spring", damping: 28, stiffness: 300 }}
              className="fixed left-0 top-0 w-[252px] h-screen bg-sidebar z-50 lg:hidden"
            >
              <button
                onClick={onMobileClose}
                aria-label="Close menu"
                className="absolute top-3.5 right-3.5 p-1 rounded-md text-sidebar-foreground/50 hover:text-sidebar-foreground hover:bg-sidebar-accent/40 transition-colors"
              >
                <X className="w-4 h-4" />
              </button>
              {sidebarContent}
            </motion.aside>
          </>
        )}
      </AnimatePresence>
    </>
  );
}