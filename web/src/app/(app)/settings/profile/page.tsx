'use client';

import { useEffect, useMemo, useState } from 'react';
import { motion, AnimatePresence } from 'framer-motion';
import {
  Loader2,
  Eye,
  EyeOff,
  ShieldCheck,
  Briefcase,
  Building2,
  KeyRound,
  ListChecks,
  Lock,
  Mail,
  User as UserIcon,
  CircleDot,
  Check,
  X as XIcon,
  RefreshCw,
  Save,
  AlertCircle,
  Sparkles,
  Link2,
  ChevronDown,
  ShieldAlert,
} from 'lucide-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { authApi, usersApi, employeesApi, type UpdateUserPayload, type ProfileResponse, type ProfileModule } from '@/lib/api';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Separator } from '@/components/ui/separator';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';

const PASSWORD_MIN = 6;

// ─── Avatar (initials + brand color) ─────────────────────────────────────────

function ProfileAvatar({ name, avatar, color, size = 72 }: { name: string; avatar?: string; color?: string; size?: number }) {
  const initials = (avatar && avatar.trim())
    ? avatar.trim().slice(0, 2).toUpperCase()
    : name
        .split(/\s+/)
        .filter(Boolean)
        .slice(0, 2)
        .map(w => w[0]!.toUpperCase())
        .join('') || '?';
  const bg = color || '#7C3AED';
  return (
    <div
      className="relative shrink-0 rounded-full ring-4 ring-background shadow-lg overflow-hidden"
      style={{ width: size, height: size, backgroundColor: bg }}
    >
      <div className="absolute inset-0 flex items-center justify-center text-primary-foreground font-display font-bold" style={{ fontSize: size * 0.4 }}>
        {initials}
      </div>
    </div>
  );
}

// ─── InfoTile (compact read-only field) ──────────────────────────────────────

function InfoTile({
  icon: Icon,
  label,
  value,
  emptyText = '—',
}: {
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  value?: string | null;
  emptyText?: string;
}) {
  const isEmpty = !value;
  return (
    <div className="flex items-start gap-3 p-3.5 rounded-xl border border-border bg-background/60 hover:bg-background transition-colors">
      <div className="w-9 h-9 rounded-lg bg-primary/10 text-primary flex items-center justify-center shrink-0">
        <Icon className="w-4 h-4" />
      </div>
      <div className="min-w-0 flex-1">
        <p className="text-[11px] uppercase tracking-wide font-medium text-muted-foreground">{label}</p>
        <p className={`text-sm font-medium mt-0.5 truncate ${isEmpty ? 'text-muted-foreground/50 italic' : 'text-foreground'}`}>
          {isEmpty ? emptyText : value}
        </p>
      </div>
    </div>
  );
}

// ─── Password strength meter (purely visual) ─────────────────────────────────

function passwordStrength(pw: string): { score: 0 | 1 | 2 | 3 | 4; label: string; color: string } {
  if (!pw) return { score: 0, label: '', color: '' };
  let score = 0;
  if (pw.length >= PASSWORD_MIN) score++;
  if (pw.length >= 10) score++;
  if (/[A-Z]/.test(pw) && /[a-z]/.test(pw)) score++;
  if (/\d/.test(pw) && /[^A-Za-z0-9]/.test(pw)) score++;
  const meta = [
    { label: 'Too short', color: 'bg-destructive' },
    { label: 'Weak', color: 'bg-destructive' },
    { label: 'Fair', color: 'bg-warning' },
    { label: 'Good', color: 'bg-primary' },
    { label: 'Strong', color: 'bg-success' },
  ][score]!;
  return { score: score as 0 | 1 | 2 | 3 | 4, label: meta.label, color: meta.color };
}

// ─── Skeleton (matches the final layout) ──────────────────────────────────────

function ProfileSkeleton() {
  return (
    <div className="space-y-5 max-w-5xl animate-fade-in">
      <div className="h-32 rounded-2xl bg-muted/40 animate-pulse" />
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-5">
        <div className="lg:col-span-1 h-80 rounded-2xl bg-muted/40 animate-pulse" />
        <div className="lg:col-span-2 h-80 rounded-2xl bg-muted/40 animate-pulse" />
      </div>
      <div className="h-48 rounded-2xl bg-muted/40 animate-pulse" />
    </div>
  );
}

// ─── Page ────────────────────────────────────────────────────────────────────

export default function ProfilePage() {
  const queryClient = useQueryClient();

  const profileQuery = useQuery({
    queryKey: ['auth', 'profile'],
    queryFn: () => authApi.profile(),
    staleTime: 60_000,
  });

  // Editable state
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [touched, setTouched] = useState<Record<string, boolean>>({});

  // Initialize from the profile payload (idempotent — only the first time).
  useEffect(() => {
    if (profileQuery.data && !touched.name && !touched.email) {
      const u = profileQuery.data.user;
      setName(u.name ?? '');
      setEmail(u.email ?? '');
    }
  }, [profileQuery.data, touched.email, touched.name]);

  // Derived state: dirty + valid
  const me = profileQuery.data?.user;
  const isNameDirty = !!me && name.trim() !== me.name;
  const isEmailDirty = !!me && email.trim() !== me.email;
  const isPasswordDirty = password.length > 0 || confirmPassword.length > 0;

  const nameError = useMemo(() => {
    if (touched.name && !name.trim()) return 'Name is required.';
    return null;
  }, [touched.name, name]);

  const emailError = useMemo(() => {
    if (!touched.email) return null;
    if (!email.trim()) return 'Email is required.';
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email.trim())) return 'Enter a valid email address.';
    return null;
  }, [touched.email, email]);

  const passwordsProvided = password.length > 0 || confirmPassword.length > 0;
  const passwordsMatch = password === confirmPassword;
  const passwordStrongEnough = password.length === 0 || password.length >= PASSWORD_MIN;
  const passwordError = useMemo(() => {
    if (!touched.password) return null;
    if (!passwordsProvided) return null;
    if (!passwordsMatch) return 'Passwords do not match.';
    if (!passwordStrongEnough) return `Password must be at least ${PASSWORD_MIN} characters.`;
    return null;
  }, [touched.password, passwordsProvided, passwordsMatch, passwordStrongEnough]);

  const confirmError = useMemo(() => {
    if (!touched.confirmPassword) return null;
    if (confirmPassword.length === 0) return 'Please re-enter the new password.';
    if (!passwordsMatch) return 'Passwords do not match.';
    return null;
  }, [touched.confirmPassword, confirmPassword, passwordsMatch]);

  const isDirty = isNameDirty || isEmailDirty || isPasswordDirty;
  const isValid = !nameError && !emailError && !passwordError && !confirmError;
  const canSave = isDirty && isValid && !!me;

  const strength = passwordStrength(password);

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (!me) throw new Error('Not signed in.');
      const data: UpdateUserPayload = {};
      const nameChanged = name.trim() !== me.name;
      const emailChanged = email.trim() !== me.email;
      if (nameChanged) data.name = name.trim();
      if (emailChanged) data.email = email.trim();
      if (password) data.password = password;

      const updated = await usersApi.update(me.id, data);

      if (me.employeeId && (nameChanged || emailChanged)) {
        try {
          const list = await employeesApi.list({ search: me.employeeId, perPage: 1 });
          const emp = list.data.find(e => e.employeeId === me.employeeId);
          if (emp) {
            await employeesApi.update(emp.id, {
              name: nameChanged ? name.trim() : undefined,
              email: emailChanged ? email.trim() : undefined,
            });
          }
        } catch (e) {
          toast.warning('Attached employee profile not synced', {
            description: (e as Error).message,
          });
        }
      }
      return updated;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['auth', 'profile'] });
      queryClient.invalidateQueries({ queryKey: ['auth', 'me'] });
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Profile updated', {
        description: 'Your changes have been saved.',
        icon: <Check className="w-4 h-4 text-success" />,
      });
      setPassword('');
      setConfirmPassword('');
      setShowPassword(false);
      setShowConfirmPassword(false);
      setTouched({});
    },
    onError: (err: Error) => {
      toast.error('Failed to update profile', {
        description: err.message,
        icon: <XIcon className="w-4 h-4" />,
      });
    },
  });

  const handleSave = () => {
    if (!me) return;
    // Mark everything touched on submit so errors surface if any.
    setTouched({ name: true, email: true, password: true, confirmPassword: true });
    if (!canSave) return;
    saveMutation.mutate();
  };

  const handleReset = () => {
    if (!me) return;
    setName(me.name);
    setEmail(me.email);
    setPassword('');
    setConfirmPassword('');
    setShowPassword(false);
    setShowConfirmPassword(false);
    setTouched({});
  };

  // ── Loading / error states ───────────────────────────────────────────────
  if (profileQuery.isLoading) return <ProfileSkeleton />;

  if (profileQuery.error) {
    return (
      <div className="flex items-center justify-center min-h-[400px] animate-fade-in">
        <div className="text-center max-w-sm">
          <div className="w-12 h-12 rounded-full bg-destructive/10 text-destructive flex items-center justify-center mx-auto mb-3">
            <ShieldAlert className="w-6 h-6" />
          </div>
          <p className="text-destructive font-semibold mb-1">Failed to load profile</p>
          <p className="text-sm text-muted-foreground mb-4">{(profileQuery.error as Error).message}</p>
          <Button onClick={() => profileQuery.refetch()} variant="outline" size="sm">
            <RefreshCw className="w-4 h-4 mr-2" /> Try again
          </Button>
        </div>
      </div>
    );
  }

  const profile: ProfileResponse = profileQuery.data!;
  const role = profile.role;
  const emp = profile.employee;
  const perms = profile.permissions;

  // The /settings/profile page is the employee-self-service surface: when the
  // logged-in admin user is linked to an employee record (`profile.employee`),
  // the shift is the employee's resolved shift name (joined from the shifts
  // catalog by the server). When the admin has no employee link, we fall back
  // to the admin user's own `shift` field (a separate `users.shift` column —
  // out of scope for this task).
  const displayShift = emp?.shift;

  const grantedModuleCount = perms.modules.filter(m => perms.isSystemAdmin || m.grantedCount > 0).length;
  const totalModuleCount = perms.modules.length;

  return (
    <div className="space-y-5  pb-24 animate-fade-in">
      {/* ─── Hero / identity header ──────────────────────────────────────── */}
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35 }}
        className="relative overflow-hidden rounded-2xl border border-border bg-card"
      >
        <div
          className="absolute inset-0 opacity-30 pointer-events-none"
          style={{
            background: 'radial-gradient(circle at 20% 20%, hsl(262 70% 60% / 0.18), transparent 60%), radial-gradient(circle at 80% 80%, hsl(280 70% 55% / 0.14), transparent 60%)',
          }}
        />
        <div className="relative flex flex-col sm:flex-row sm:items-center gap-4 sm:gap-6 p-5 sm:p-6">
          <ProfileAvatar
            name={me.name}
            avatar={me.avatar}
            color={me.avatarColor}
            size={84}
          />
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 flex-wrap">
              <h1 className="font-display text-2xl font-bold text-foreground truncate">{me.name || 'Unnamed user'}</h1>
              {role?.isSystem && (
                <Badge variant="default" className="bg-success/15 text-success border-success/30 hover:bg-success/20">
                  <Lock className="w-3 h-3 mr-1" /> System
                </Badge>
              )}
              <Badge
                variant="secondary"
                className={me.isOnline ? 'bg-success/10 text-success border-success/30' : 'bg-muted text-muted-foreground border-border'}
              >
                <CircleDot className={`w-3 h-3 mr-1 ${me.isOnline ? 'text-success' : 'text-muted-foreground'}`} />
                {me.isOnline ? 'Online' : 'Offline'}
              </Badge>
            </div>
            <p className="text-sm text-muted-foreground mt-1 truncate">{me.email}</p>
            <div className="flex items-center gap-2 mt-2.5 flex-wrap">
              <Badge variant="outline" className="font-normal">
                <ShieldCheck className="w-3 h-3 mr-1.5" />
                {role?.name ?? me.role}
              </Badge>
              {me.employeeId && (
                <Badge variant="outline" className="font-mono font-normal text-xs">
                  <Link2 className="w-3 h-3 mr-1.5" />
                  {me.employeeId}
                </Badge>
              )}
              {emp?.department && (
                <Badge variant="outline" className="font-normal">
                  <Building2 className="w-3 h-3 mr-1.5" />
                  {emp.department}
                </Badge>
              )}
              {displayShift && (
                <Badge variant="outline" className="font-normal">
                  <Briefcase className="w-3 h-3 mr-1.5" />
                  Shift: {displayShift}
                </Badge>
              )}
            </div>
          </div>
        </div>
      </motion.section>

      {/* ─── Employee link banner (when linked) ──────────────────────────── */}
      <AnimatePresence>
        {me.employeeId && (
          <motion.div
            initial={{ opacity: 0, y: -4 }}
            animate={{ opacity: 1, y: 0 }}
            exit={{ opacity: 0, y: -4 }}
            transition={{ duration: 0.25 }}
            className="flex items-center gap-3 px-4 py-3 rounded-xl border border-primary/30 bg-primary/5"
          >
            <div className="w-8 h-8 rounded-lg bg-primary/15 text-primary flex items-center justify-center shrink-0">
              <Sparkles className="w-4 h-4" />
            </div>
            <div className="min-w-0 flex-1">
              <p className="text-sm font-medium text-foreground">Linked to employee {me.employeeId}</p>
              <p className="text-xs text-muted-foreground">
                Name and email changes are automatically synced to the employee record.
              </p>
            </div>
            <Badge variant="secondary" className="bg-primary/10 text-primary border-primary/20 shrink-0">
              Auto-sync on
            </Badge>
          </motion.div>
        )}
      </AnimatePresence>

      {/* ─── Two-column: identity (read-only) + edit form ──────────────── */}
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-5">
        {/* Left column: identity details */}
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.35, delay: 0.05 }}
          className="lg:col-span-1 bg-card rounded-2xl border border-border p-5 space-y-4 self-start"
        >
          <header className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
              <UserIcon className="w-4 h-4 text-muted-foreground" /> Account
            </h2>
            <Badge variant="outline" className="text-[10px] uppercase tracking-wide font-medium">Read-only</Badge>
          </header>
          <div className="space-y-2.5">
            <InfoTile icon={Link2} label="Employee Link" value={me.employeeId} emptyText="Not linked" />
            <InfoTile icon={ShieldCheck} label="Role" value={role?.name ?? me.role} />
            <InfoTile icon={Building2} label="Department" value={emp?.department} emptyText="No department" />
            <InfoTile icon={Briefcase} label="Shift" value={displayShift} emptyText="Unassigned" />
          </div>
        </motion.section>

        {/* Right column: edit form with tabs */}
        <motion.section
          initial={{ opacity: 0, y: 8 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.35, delay: 0.1 }}
          className="lg:col-span-2 bg-card rounded-2xl border border-border overflow-hidden"
        >
          <header className="px-5 pt-5 pb-3 flex items-center justify-between">
            <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
              <KeyRound className="w-4 h-4 text-muted-foreground" /> Update details
            </h2>
            {isDirty && (
              <Badge variant="secondary" className="bg-warning/10 text-warning border-warning/30 text-[10px]">
                Unsaved changes
              </Badge>
            )}
          </header>

          <Tabs defaultValue="personal" className="w-full">
            <div className="px-5">
              <TabsList className="w-full sm:w-auto">
                <TabsTrigger value="personal" className="flex-1 sm:flex-none">
                  <UserIcon className="w-3.5 h-3.5 mr-2" />
                  Personal info
                </TabsTrigger>
                <TabsTrigger value="password" className="flex-1 sm:flex-none">
                  <KeyRound className="w-3.5 h-3.5 mr-2" />
                  Password
                </TabsTrigger>
              </TabsList>
            </div>

            <Separator />

            <TabsContent value="personal" className="p-5 space-y-4 mt-0">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div className="space-y-1.5">
                  <Label htmlFor="profile-name">Full Name</Label>
                  <div className="relative">
                    <UserIcon className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none" />
                    <Input
                      id="profile-name"
                      value={name}
                      onChange={e => setName(e.target.value)}
                      onBlur={() => setTouched(t => ({ ...t, name: true }))}
                      placeholder="Your full name"
                      className="pl-9"
                      aria-invalid={!!nameError}
                    />
                  </div>
                  {nameError && (
                    <p className="text-xs text-destructive flex items-center gap-1">
                      <AlertCircle className="w-3 h-3" /> {nameError}
                    </p>
                  )}
                </div>
                <div className="space-y-1.5">
                  <Label htmlFor="profile-email">Email</Label>
                  <div className="relative">
                    <Mail className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none" />
                    <Input
                      id="profile-email"
                      type="email"
                      value={email}
                      onChange={e => setEmail(e.target.value)}
                      onBlur={() => setTouched(t => ({ ...t, email: true }))}
                      placeholder="you@example.com"
                      className="pl-9"
                      aria-invalid={!!emailError}
                    />
                  </div>
                  {emailError && (
                    <p className="text-xs text-destructive flex items-center gap-1">
                      <AlertCircle className="w-3 h-3" /> {emailError}
                    </p>
                  )}
                </div>
              </div>
            </TabsContent>

            <TabsContent value="password" className="p-5 space-y-4 mt-0">
              <div className="space-y-4">
                <div className="space-y-1.5">
                  <Label htmlFor="profile-password">New password</Label>
                  <div className="relative">
                    <KeyRound className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none" />
                    <Input
                      id="profile-password"
                      type={showPassword ? 'text' : 'password'}
                      value={password}
                      onChange={e => setPassword(e.target.value)}
                      onBlur={() => setTouched(t => ({ ...t, password: true }))}
                      placeholder="Leave empty to keep current"
                      className="pl-9 pr-10"
                      autoComplete="new-password"
                      aria-invalid={!!passwordError}
                    />
                    <button
                      type="button"
                      onClick={() => setShowPassword(s => !s)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 text-muted-foreground hover:text-foreground rounded transition-colors"
                      aria-label={showPassword ? 'Hide password' : 'Show password'}
                      tabIndex={-1}
                    >
                      {showPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                    </button>
                  </div>
                  {password && (
                    <div className="space-y-1.5 pt-1">
                      <div className="flex gap-1">
                        {[1, 2, 3, 4].map(i => (
                          <div
                            key={i}
                            className={`h-1 flex-1 rounded-full transition-colors ${
                              i <= strength.score ? strength.color : 'bg-muted'
                            }`}
                          />
                        ))}
                      </div>
                      <p className="text-[11px] text-muted-foreground">
                        Strength: <span className="font-medium text-foreground">{strength.label}</span>
                      </p>
                    </div>
                  )}
                  {passwordError && (
                    <p className="text-xs text-destructive flex items-center gap-1">
                      <AlertCircle className="w-3 h-3" /> {passwordError}
                    </p>
                  )}
                </div>
                <div className="space-y-1.5">
                  <Label htmlFor="profile-confirm">Confirm new password</Label>
                  <div className="relative">
                    <KeyRound className="w-4 h-4 absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground pointer-events-none" />
                    <Input
                      id="profile-confirm"
                      type={showConfirmPassword ? 'text' : 'password'}
                      value={confirmPassword}
                      onChange={e => setConfirmPassword(e.target.value)}
                      onBlur={() => setTouched(t => ({ ...t, confirmPassword: true }))}
                      placeholder="Re-enter the new password"
                      className="pl-9 pr-10"
                      autoComplete="new-password"
                      aria-invalid={!!confirmError}
                    />
                    <button
                      type="button"
                      onClick={() => setShowConfirmPassword(s => !s)}
                      className="absolute right-2 top-1/2 -translate-y-1/2 p-1.5 text-muted-foreground hover:text-foreground rounded transition-colors"
                      aria-label={showConfirmPassword ? 'Hide confirm password' : 'Show confirm password'}
                      tabIndex={-1}
                    >
                      {showConfirmPassword ? <EyeOff className="w-4 h-4" /> : <Eye className="w-4 h-4" />}
                    </button>
                  </div>
                  {confirmError && (
                    <p className="text-xs text-destructive flex items-center gap-1">
                      <AlertCircle className="w-3 h-3" /> {confirmError}
                    </p>
                  )}
                </div>
              </div>
            </TabsContent>
          </Tabs>

          {/* Sticky-style footer for the form (still flows inline; position
              sticky is left for a follow-up if a tall viewport is needed). */}
          <div className="px-5 py-4 border-t border-border bg-muted/20 flex items-center justify-between gap-3">
            <p className="text-xs text-muted-foreground">
              {isDirty ? 'You have unsaved changes.' : 'All changes saved.'}
            </p>
            <div className="flex items-center gap-2">
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={handleReset}
                disabled={!isDirty || saveMutation.isPending}
              >
                <RefreshCw className="w-3.5 h-3.5 mr-1.5" /> Discard
              </Button>
              <Button
                type="button"
                size="sm"
                onClick={handleSave}
                disabled={!canSave || saveMutation.isPending}
                className="gradient-primary text-primary-foreground hover:opacity-90"
              >
                {saveMutation.isPending ? (
                  <>
                    <Loader2 className="w-3.5 h-3.5 mr-1.5 animate-spin" />
                    Saving…
                  </>
                ) : (
                  <>
                    <Save className="w-3.5 h-3.5 mr-1.5" />
                    Save changes
                  </>
                )}
              </Button>
            </div>
          </div>
        </motion.section>
      </div>

      {/* ─── Module access ──────────────────────────────────────────────── */}
      <motion.section
        initial={{ opacity: 0, y: 8 }}
        animate={{ opacity: 1, y: 0 }}
        transition={{ duration: 0.35, delay: 0.15 }}
        className="bg-card rounded-2xl border border-border p-5 space-y-4"
      >
        <header className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
          <div>
            <h2 className="text-sm font-semibold text-foreground flex items-center gap-2">
              <ListChecks className="w-4 h-4 text-muted-foreground" /> Modules you can access
            </h2>
            <p className="text-xs text-muted-foreground mt-1">
              {perms.isSystemAdmin
                ? 'You hold the system role and have full access to every module.'
                : `Granted ${perms.submoduleKeys.length} submodule key${perms.submoduleKeys.length === 1 ? '' : 's'} across ${grantedModuleCount} of ${totalModuleCount} module${totalModuleCount === 1 ? '' : 's'}.`}
            </p>
          </div>
          {perms.isSystemAdmin && (
            <Badge variant="secondary" className="bg-success/10 text-success border-success/30 self-start sm:self-auto">
              <Sparkles className="w-3 h-3 mr-1" /> Full access
            </Badge>
          )}
        </header>

        {perms.modules.length === 0 ? (
          <p className="text-xs text-muted-foreground">No modules are defined in the RBAC catalog yet.</p>
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 gap-2.5">
            {perms.modules.map(m => (
              <ModuleAccessCard
                key={m.id}
                module={m}
                grantedKeys={perms.submoduleKeys}
                isSystemAdmin={perms.isSystemAdmin}
              />
            ))}
          </div>
        )}
      </motion.section>
    </div>
  );
}

// ─── Module access card (collapsible to show submodule detail) ───────────────

function ModuleAccessCard({
  module,
  grantedKeys,
  isSystemAdmin,
}: {
  module: ProfileModule;
  grantedKeys: string[];
  isSystemAdmin: boolean;
}) {
  const granted = isSystemAdmin || module.grantedCount > 0;
  const ratio = module.submoduleCount > 0 ? module.grantedCount / module.submoduleCount : 0;

  // The server only returns counts per module (no submodule list in
  // ProfileResponse). We can still show which submodules within the module
  // the user has — the granted keys look like "settings/user-management"
  // and any key with the module's prefix is one of its children.
  const modulePrefix = `${module.key}/`;
  const childKeys = grantedKeys.filter(k => k.startsWith(modulePrefix));

  return (
    <Collapsible>
      <div
        className={`rounded-xl border transition-colors overflow-hidden ${
          granted
            ? isSystemAdmin
              ? 'border-success/30 bg-success/5'
              : 'border-primary/30 bg-primary/5'
            : 'border-border bg-muted/20 opacity-75'
        }`}
      >
        <CollapsibleTrigger asChild>
          <button
            type="button"
            className="w-full flex items-center gap-3 p-3.5 text-left hover:bg-background/40 transition-colors"
          >
            <div className={`w-9 h-9 rounded-lg flex items-center justify-center shrink-0 ${
              granted
                ? isSystemAdmin
                  ? 'bg-success/15 text-success'
                  : 'bg-primary/15 text-primary'
                : 'bg-muted text-muted-foreground'
            }`}>
              {granted ? <Check className="w-4 h-4" /> : <XIcon className="w-4 h-4" />}
            </div>
            <div className="min-w-0 flex-1">
              <p className="text-sm font-semibold text-foreground truncate">{module.name}</p>
              <p className="text-[11px] font-mono text-muted-foreground truncate">{module.key}</p>
            </div>
            <Badge
              variant="secondary"
              className={
                isSystemAdmin
                  ? 'bg-success/10 text-success border-success/30 shrink-0'
                  : granted
                  ? 'bg-primary/10 text-primary border-primary/30 shrink-0'
                  : 'bg-muted text-muted-foreground border-border shrink-0'
              }
            >
              {isSystemAdmin ? 'all' : `${module.grantedCount}/${module.submoduleCount}`}
            </Badge>
            <ChevronDown className="w-4 h-4 text-muted-foreground shrink-0 transition-transform group-data-[state=open]:rotate-180" />
          </button>
        </CollapsibleTrigger>
        <CollapsibleContent>
          <div className="border-t border-border/60 px-3.5 py-3 bg-background/40 space-y-2">
            <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
              <div className={`h-1 flex-1 rounded-full bg-muted overflow-hidden`}>
                <div
                  className={`h-full ${isSystemAdmin ? 'bg-success' : 'bg-primary'} transition-all`}
                  style={{ width: `${isSystemAdmin ? 100 : ratio * 100}%` }}
                />
              </div>
              <span className="font-mono">
                {isSystemAdmin ? '100%' : `${Math.round(ratio * 100)}%`}
              </span>
            </div>
            {childKeys.length > 0 ? (
              <ul className="space-y-1">
                {childKeys.map(k => {
                  const leaf = k.slice(modulePrefix.length);
                  return (
                    <li key={k} className="flex items-center gap-2 text-xs">
                      <Check className="w-3.5 h-3.5 text-success shrink-0" />
                      <span className="font-mono text-muted-foreground truncate">{leaf}</span>
                    </li>
                  );
                })}
              </ul>
            ) : isSystemAdmin ? (
              <p className="text-xs text-muted-foreground italic">
                System role grants every submodule in this module.
              </p>
            ) : (
              <p className="text-xs text-muted-foreground italic">
                No granted submodules in this module.
              </p>
            )}
          </div>
        </CollapsibleContent>
      </div>
    </Collapsible>
  );
}
