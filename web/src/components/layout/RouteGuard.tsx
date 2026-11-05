"use client";

import { useEffect } from "react";
import { useRouter, usePathname } from "next/navigation";
import { useAuth } from "@/lib/auth";
import { usePermissions } from "@/lib/permissions";
import { findModuleForPath } from "./AppSidebar";

// RouteGuard enforces the logged-in user's role permissions on every (app)
// route: if the pathname's module is not granted by the role's submodule set,
// the user is redirected to /unauthorized. Unguarded paths pass through.
//
// Two paths are ungated:
//   1. findModuleForPath returns undefined (no sidebar entry guards the path).
//   2. The pathname is listed in ALWAYS_ALLOWED below — a single source of
//      truth for routes that must be reachable by every authenticated user
//      (e.g. /settings/profile — the self-service profile page omits the
//      sidebar's `module` key because it is open to all roles, but the
//      parent /settings entry's `settings` module is a prefix-match and
//      would otherwise block the route).
const ALWAYS_ALLOWED: string[] = ["/settings/profile"];

function isAlwaysAllowed(pathname: string): boolean {
  for (const allowed of ALWAYS_ALLOWED) {
    if (pathname === allowed) return true;
    if (pathname.startsWith(allowed + "/")) return true;
  }
  return false;
}

export default function RouteGuard({ children }: { children: React.ReactNode }) {
  const { user, isLoading } = useAuth();
  const { canAccess } = usePermissions();
  const router = useRouter();
  const pathname = usePathname();

  useEffect(() => {
    if (isLoading || !user || !pathname) return;
    if (isAlwaysAllowed(pathname)) return;
    const module = findModuleForPath(pathname);
    if (module && !canAccess(user.role, module)) {
      router.replace("/unauthorized");
    }
  }, [isLoading, user, pathname, canAccess, router]);

  return <>{children}</>;
}
