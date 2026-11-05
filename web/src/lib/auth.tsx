'use client';

import React, { createContext, useContext, useEffect, ReactNode } from 'react';
import { useRouter } from 'next/navigation';
import { useAppDispatch, useAppSelector } from './store/hooks';
import { checkAuth, loginUser, logoutUser, clearError } from './store/redux';
import { useQueryClient } from '@/components/providers';

export type UserRole = 'super_admin' | 'org_admin' | 'hr_admin' | 'manager' | 'employee' | 'security_analyst' | 'it_admin' | 'auditor' | 'company_admin';

export interface AuthUser {
  id: string;
  name: string;
  email: string;
  role: UserRole | string;
  roleId?: number;
  /** Granted submodule keys for the user's role (server-driven RBAC). */
  permissions?: string[];
  employeeId: string;
  department?: string;
  avatar: string;
  avatarColor: string;
}

function mapAuthUser(user: import('./api').AuthUser): AuthUser {
  return {
    id: user.id,
    name: user.name,
    email: user.email,
    role: user.role,
    roleId: user.roleId,
    permissions: user.permissions,
    employeeId: user.employeeId,
    department: (user as { department?: string }).department,
    avatar: user.avatar || user.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2),
    avatarColor: user.avatarColor || '#7C3AED',
  };
}

export function getRoleName(role: UserRole | string): string {
  const names: Record<string, string> = {
    super_admin: 'Super Admin',
    org_admin: 'Org Admin',
    hr_admin: 'HR Admin',
    manager: 'Manager',
    employee: 'Employee',
    security_analyst: 'Security Analyst',
    it_admin: 'IT Admin',
    auditor: 'Auditor',
    company_admin: 'Company Admin',
  };
  return names[role] || role;
}

const FALLBACK_USERS: AuthUser[] = [
  { id: 'sa1', name: 'Super Admin', email: 'superadmin@alphai.com', role: 'super_admin', avatar: 'SA', avatarColor: '#7C3AED', department: 'Executive', employeeId: 'SA-0001' },
  { id: 'oa1', name: 'Org Admin', email: 'orgadmin@alphai.com', role: 'org_admin', avatar: 'OA', avatarColor: '#3B82F6', department: 'Executive', employeeId: 'OA-0001' },
  { id: 'ha1', name: 'HR Admin', email: 'hradmin@alphai.com', role: 'hr_admin', avatar: 'HA', avatarColor: '#EC4899', department: 'HR', employeeId: 'HA-0001' },
  { id: 'mg1', name: 'Manager User', email: 'manager@alphai.com', role: 'manager', avatar: 'MU', avatarColor: '#F59E0B', department: 'Engineering', employeeId: 'MG-0001' },
  { id: 'em1', name: 'Employee User', email: 'employee@alphai.com', role: 'employee', avatar: 'EU', avatarColor: '#10B981', department: 'Engineering', employeeId: 'EM-0001' },
  { id: 'sc1', name: 'Security Analyst', email: 'security@alphai.com', role: 'security_analyst', avatar: 'SC', avatarColor: '#EF4444', department: 'Security', employeeId: 'SC-0001' },
  { id: 'it1', name: 'IT Admin', email: 'itadmin@alphai.com', role: 'it_admin', avatar: 'IT', avatarColor: '#06B6D4', department: 'IT', employeeId: 'IT-0001' },
  { id: 'au1', name: 'Auditor', email: 'auditor@alphai.com', role: 'auditor', avatar: 'AU', avatarColor: '#8B5CF6', department: 'Compliance', employeeId: 'AU-0001' },
];

interface AuthContextType {
  user: AuthUser | null;
  login: (email: string, password: string) => Promise<{ success: boolean; error?: string }>;
  logout: () => void;
  isLoading: boolean;
  isAuthenticated: boolean;
  allUsers: AuthUser[];
}

const AuthContext = createContext<AuthContextType | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const dispatch = useAppDispatch();
  const { user: rawUser, isLoading, isAuthenticated } = useAppSelector((state) => state.auth);
  const router = useRouter();

  // Check auth status on mount (cookie-based)
  useEffect(() => {
    dispatch(checkAuth());
  }, [dispatch]);

  const login = async (email: string, password: string) => {
    try {
      const result = await dispatch(loginUser({ email, password })).unwrap();
      return { success: true };
    } catch (err) {
      return { success: false, error: typeof err === 'string' ? err : 'Login failed' };
    }
  };

  const queryClient = useQueryClient();

  const logout = () => {
    queryClient.clear();
    dispatch(logoutUser());
    router.replace('/login');
  };

  const mappedUser: AuthUser | null = rawUser ? mapAuthUser(rawUser) : null;

  return (
    <AuthContext.Provider
      value={{
        user: mappedUser,
        login,
        logout,
        isLoading,
        isAuthenticated,
        allUsers: FALLBACK_USERS,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be inside AuthProvider');
  return ctx;
}
