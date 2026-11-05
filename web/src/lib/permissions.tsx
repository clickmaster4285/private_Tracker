import React, { createContext, useContext, useCallback, ReactNode } from 'react';
import { UserRole } from './auth';
import { useAppSelector } from './store/hooks';

export type Permission = 'full' | 'view' | 'self' | 'config' | 'none';

interface PermissionsContextType {
  /** Submodule keys granted by the logged-in user's role (server-driven RBAC). */
  allowedModules: Set<string> | null;
  hasPermission: (role: UserRole | string, module: string) => Permission;
  canAccess: (role: UserRole | string, module: string) => boolean;
}

const PermissionsContext = createContext<PermissionsContextType | null>(null);

export function PermissionsProvider({ children }: { children: ReactNode }) {
  const authUser = useAppSelector((state) => state.auth.user);
  // Server-driven grant set for the CURRENT user. Null while auth state is
  // loading or the session predates role permissions (fail-open so an existing
  // session is never locked out of every page); once present, access is strict.
  const allowedModules =
    authUser?.permissions && Array.isArray(authUser.permissions)
      ? new Set(authUser.permissions)
      : null;

  const hasPermission = useCallback((role: UserRole | string, module: string): Permission => {
    if (role === 'company_admin') return 'full';
    if (!allowedModules) return 'full';
    return allowedModules.has(module) ? 'full' : 'none';
  }, [allowedModules]);

  const canAccess = useCallback((role: UserRole | string, module: string): boolean => {
    return hasPermission(role, module) !== 'none';
  }, [hasPermission]);

  return (
    <PermissionsContext.Provider value={{ allowedModules, hasPermission, canAccess }}>
      {children}
    </PermissionsContext.Provider>
  );
}

export function usePermissions() {
  const ctx = useContext(PermissionsContext);
  if (!ctx) throw new Error('usePermissions must be inside PermissionsProvider');
  return ctx;
}
