'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams, useRouter } from 'next/navigation';
import { Loader2, Search, ShieldCheck, Eye, EyeOff, Pencil, Ban, CircleCheck } from 'lucide-react';
import { useInfiniteQuery, useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { usersApi, rolesApi, employeesApi, type Role, type User, type UpdateUserPayload } from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { useUrlQueryState } from '@/hooks/use-url-query-state';

const PAGE_SIZE = 10;

// ─── Users table (server-side infinite scroll) ───────────────────────────────

function UsersTable({
  search,
  onEdit,
}: {
  search: string;
  onEdit: (user: User) => void;
}) {
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  const queryClient = useQueryClient();
  // Id of the row whose status change is in flight (spins just that button).
  const [pendingStatusId, setPendingStatusId] = useState<string | null>(null);

  // Suspend / reactivate an account. Reuses PUT /users/:id — the server rejects
  // `status` values other than active|suspended and refuses to suspend the
  // system company_admin (403), which this surface already hides.
  const statusMutation = useMutation({
    mutationFn: ({ id, status }: { id: string; status: 'active' | 'suspended' }) =>
      usersApi.update(id, { status }),
    onSuccess: updated => {
      const suspended = updated.status === 'suspended';
      toast.success(suspended ? 'User suspended' : 'User activated', {
        description: suspended
          ? `${updated.name} can no longer sign in.`
          : `${updated.name} can sign in again.`,
      });
      queryClient.invalidateQueries({ queryKey: ['users'] });
    },
    onError: (err: unknown) => {
      toast.error('Could not change status', {
        description: err instanceof Error ? err.message : 'Please try again.',
      });
    },
    onSettled: () => setPendingStatusId(null),
  });

  const query = useInfiniteQuery({
    queryKey: ['users', { search }],
    initialPageParam: 1,
    queryFn: ({ pageParam }) => usersApi.list({ page: pageParam as number, perPage: PAGE_SIZE, search }),
    getNextPageParam: last => (last.page < last.totalPages ? last.page + 1 : undefined),
  });

  const users = useMemo(() => query.data?.pages.flatMap(p => p.data) ?? [], [query.data]);

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries[0].isIntersecting && query.hasNextPage && !query.isFetchingNextPage) {
          query.fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [query.hasNextPage, query.isFetchingNextPage, query.fetchNextPage]);

  if (query.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[300px]">
        <Loader2 className="w-7 h-7 animate-spin text-primary" />
      </div>
    );
  }

  return (
    <>
      <div className="rounded-xl border border-border bg-card overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-border bg-muted/30 text-left">
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">User</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Email</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Employee Link</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Role</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Status</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground">Created</th>
                <th className="px-4 py-3 text-xs font-semibold uppercase tracking-wide text-muted-foreground text-right">Action</th>
              </tr>
            </thead>
            <tbody>
              {users.map(u => (
                <tr key={u.id} className="border-b border-border/60 last:border-0 hover:bg-muted/20 transition-colors">
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <span className={`w-8 h-8 rounded-full flex items-center justify-center text-[11px] font-bold shrink-0 ${u.avatarColor || 'bg-primary/15 text-primary'}`}>
                        {initialsOf(u.name)}
                      </span>
                      <span className="font-medium text-foreground">{u.name}</span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-muted-foreground">{u.email}</td>
                  <td className="px-4 py-3">
                    {u.employeeId ? (
                      <span className="px-2 py-0.5 rounded-md bg-accent text-accent-foreground text-xs font-medium">{u.employeeId}</span>
                    ) : (
                      <span className="text-muted-foreground/50 text-xs">—</span>
                    )}
                  </td>
                  <td className="px-4 py-3">
                    <span
                      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${
                        u.role === 'company_admin' ? 'bg-success/10 text-success' : 'bg-primary/10 text-primary'
                      }`}
                    >
                      {u.role === 'company_admin' && <ShieldCheck className="w-3 h-3" />}
                      {roleLabel(u.role)}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-2">
                      <span
                        className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${
                          u.status === 'suspended'
                            ? 'bg-destructive/15 text-destructive'
                            : 'bg-success/10 text-success'
                        }`}
                      >
                        {u.status === 'suspended' ? 'Suspended' : 'Active'}
                      </span>
                      <span
                        className={`inline-flex items-center gap-1.5 text-xs font-medium ${u.isOnline ? 'text-success' : 'text-muted-foreground'}`}
                      >
                        <span className={`w-1.5 h-1.5 rounded-full ${u.isOnline ? 'bg-success' : 'bg-muted-foreground/40'}`} />
                        {u.isOnline ? 'Online' : 'Offline'}
                      </span>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-xs text-muted-foreground">{formatDate(u.createdAt)}</td>
                  <td className="px-4 py-3 text-right">
                    {/* The system `company_admin` is a bootstrap-only account: it is
                        neither editable nor suspendable from this surface (the
                        server refuses both with 403). Locked. */}
                    {u.role !== 'company_admin' && (
                      <div className="inline-flex items-center gap-1">
                        <button
                          onClick={() => onEdit(u)}
                          className="inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-xs font-medium text-foreground hover:bg-muted transition-colors"
                          aria-label={`Edit ${u.name}`}
                        >
                          <Pencil className="w-3.5 h-3.5" /> Edit
                        </button>
                        <button
                          onClick={() => {
                            setPendingStatusId(u.id);
                            statusMutation.mutate({
                              id: u.id,
                              status: u.status === 'suspended' ? 'active' : 'suspended',
                            });
                          }}
                          disabled={pendingStatusId === u.id}
                          className={`inline-flex items-center gap-1.5 px-2.5 py-1.5 rounded-md text-xs font-medium transition-colors disabled:opacity-50 ${
                            u.status === 'suspended'
                              ? 'text-success hover:bg-success/10'
                              : 'text-destructive hover:bg-destructive/10'
                          }`}
                          aria-label={u.status === 'suspended' ? `Activate ${u.name}` : `Suspend ${u.name}`}
                        >
                          {pendingStatusId === u.id ? (
                            <Loader2 className="w-3.5 h-3.5 animate-spin" />
                          ) : u.status === 'suspended' ? (
                            <CircleCheck className="w-3.5 h-3.5" />
                          ) : (
                            <Ban className="w-3.5 h-3.5" />
                          )}
                          {u.status === 'suspended' ? 'Activate' : 'Suspend'}
                        </button>
                      </div>
                    )}
                  </td>
                </tr>
              ))}
              {users.length === 0 && (
                <tr>
                  <td colSpan={7} className="px-4 py-12 text-center text-sm text-muted-foreground">
                    No users found{search ? ` for "${search}"` : ''}.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {query.isFetchingNextPage && (
        <p className="text-center text-xs text-muted-foreground py-3">Loading more…</p>
      )}
      {!query.hasNextPage && users.length > 0 && (
        <p className="text-center text-xs text-muted-foreground py-3">Showing all {users.length}</p>
      )}
      <div ref={sentinelRef} />
    </>
  );
}

// ─── Helpers ──────────────────────────────────────────────────────────────────

function initialsOf(name: string): string {
  return (
    name
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map(w => w[0]!.toUpperCase())
      .join('') || '?'
  );
}

function roleLabel(role: string): string {
  if (role === 'company_admin') return 'Company Admin';
  return role
    .replace(/_/g, ' ')
    .replace(/\b\w/g, c => c.toUpperCase());
}

function formatDate(value?: string): string {
  if (!value) return '—';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}

// ─── Page ─────────────────────────────────────────────────────────────────────

function UserManagementInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const queryClient = useQueryClient();

  const [searchUrl, setSearchUrl] = useUrlQueryState({ q: {} }, { q: '' });
  // Local debounced mirror of the URL-backed `q` so the server query
  // is only re-fired on the 400ms quiet window.
  const [searchInput, setSearchInput] = useState(searchUrl.q);
  useEffect(() => { setSearchInput(searchUrl.q); }, [searchUrl.q]);
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== searchUrl.q) setSearchUrl({ q: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, searchUrl.q, setSearchUrl]);
  const search = searchUrl.q;
  const [showDialog, setShowDialog] = useState(false);
  const [editingUserId, setEditingUserId] = useState<string | null>(null);
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showConfirmPassword, setShowConfirmPassword] = useState(false);
  const [employeeId, setEmployeeId] = useState('');
  const [roleId, setRoleId] = useState<string>('');
  // When arriving from the employees page, fields are locked because they mirror
  // the employee record; the same lock applies in edit mode (identity is fixed).
  const [fieldsLocked, setFieldsLocked] = useState(false);
  // Original user fetched in edit mode (for sync-on-save to the attached employee).
  const [editingOriginalUser, setEditingOriginalUser] = useState<User | null>(null);

  const { data: rolesData } = useQuery({
    queryKey: ['roles'],
    queryFn: () => rolesApi.list(),
  });
  const roles: Role[] = rolesData?.roles ?? [];

  // Filter out the system "company_admin" role from the dropdown — it is a
  // reserved role for the bootstrap admin and must not be assignable from this UI.
  const assignableRoles = useMemo(
    () => roles.filter(r => r.name !== 'company_admin'),
    [roles],
  );

  // Deep-link prefill: two modes
  //   ?create=1&employeeId=&name=&email=  → create user pre-filled from employee
  //   ?edit=1&userId=                     → edit an existing user
  useEffect(() => {
    if (searchParams.get('create') === '1') {
      setName(searchParams.get('name') ?? '');
      setEmail(searchParams.get('email') ?? '');
      setEmployeeId(searchParams.get('employeeId') ?? '');
      setPassword('');
      setConfirmPassword('');
      setShowPassword(false);
      setShowConfirmPassword(false);
      setRoleId('');
      setEditingUserId(null);
      setEditingOriginalUser(null);
      setFieldsLocked(true); // name/email/employeeId are mirrors of the employee
      setShowDialog(true);
      router.replace('/settings/user-management');
      return;
    }
    if (searchParams.get('edit') === '1') {
      const userId = searchParams.get('userId');
      if (userId) {
        openEditById(userId);
        router.replace('/settings/user-management');
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams, router]);

  // Fetch the user being edited so we have the original record to compare for sync.
  const editTargetQuery = useQuery({
    queryKey: ['user', editingUserId],
    queryFn: () => usersApi.get(editingUserId!),
    enabled: !!editingUserId,
  });

  useEffect(() => {
    if (editingUserId && editTargetQuery.data) {
      const u = editTargetQuery.data;
      // Defense in depth: the table hides the Edit button for company_admin
      // and the role dropdown excludes it, but a crafted ?edit=1&userId=… URL
      // could still target the bootstrap admin. Refuse the open here.
      if (u.role === 'company_admin') {
        toast.error('Company Admin cannot be edited', {
          description: 'The system admin account is locked from this surface.',
        });
        setEditingUserId(null);
        setEditingOriginalUser(null);
        setShowDialog(false);
        return;
      }
      setName(u.name);
      setEmail(u.email);
      setEmployeeId(u.employeeId || '');
      setRoleId(String(u.roleId));
      setPassword('');
      setConfirmPassword('');
      setShowPassword(false);
      setShowConfirmPassword(false);
      setEditingOriginalUser(u);
      setFieldsLocked(true); // identity is immutable on edit
      setShowDialog(true);
    }
  }, [editingUserId, editTargetQuery.data]);

  const openEditById = (userId: string) => {
    setEditingUserId(userId);
  };

  const openEdit = (user: User) => {
    openEditById(user.id);
  };

  const createMutation = useMutation({
    mutationFn: () =>
      usersApi.create({
        name,
        email,
        password: password || undefined,
        employeeId: employeeId || undefined,
        roleId: Number(roleId),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('User created', { description: `${name} can now sign in to the dashboard.` });
      closeDialog();
    },
    onError: (err: Error) => {
      toast.error('Failed to create user', { description: err.message });
    },
  });

  const updateMutation = useMutation({
    mutationFn: async (payload: { id: string; data: UpdateUserPayload }) => {
      const updated = await usersApi.update(payload.id, payload.data);
      // Sync identity changes to the attached employee record so the two
      // surfaces never drift apart. EmployeeId is immutable (the lock prevents
      // sending it), so we update by employeeId and only forward name/email.
      if (editingOriginalUser?.employeeId && employeeId) {
        const nameChanged = editingOriginalUser.name !== name;
        const emailChanged = editingOriginalUser.email !== email;
        if (nameChanged || emailChanged) {
          try {
            // Resolve the employee by their public employeeId (EMP-XXXXX) to
            // get the UUID the employees API expects in the path.
            const list = await employeesApi.list({ search: employeeId, perPage: 1 });
            const emp = list.data.find(e => e.employeeId === employeeId);
            if (emp) {
              await employeesApi.update(emp.id, {
                name: nameChanged ? name : undefined,
                email: emailChanged ? email : undefined,
              });
            }
          } catch (e) {
            // Non-fatal: the user update succeeded; the employee sync failed.
            toast.warning('Attached employee not synced', {
              description: (e as Error).message,
            });
          }
        }
      }
      return updated;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('User updated', { description: 'Changes saved.' });
      closeDialog();
    },
    onError: (err: Error) => {
      toast.error('Failed to update user', { description: err.message });
    },
  });

  const closeDialog = () => {
    setShowDialog(false);
    setName('');
    setEmail('');
    setPassword('');
    setConfirmPassword('');
    setShowPassword(false);
    setShowConfirmPassword(false);
    setEmployeeId('');
    setRoleId('');
    setEditingUserId(null);
    setEditingOriginalUser(null);
    setFieldsLocked(false);
  };

  const handleSave = () => {
    if (!name.trim() || !email.trim()) {
      toast.error('Validation error', { description: 'Name and email are required.' });
      return;
    }
    if (!roleId) {
      toast.error('Validation error', { description: 'Please select a role.' });
      return;
    }
    if (editingUserId) {
      // Edit mode: password is optional; confirm only if provided.
      if (password || confirmPassword) {
        if (password !== confirmPassword) {
          toast.error('Validation error', { description: 'Passwords do not match.' });
          return;
        }
        if (password.length < 6) {
          toast.error('Validation error', { description: 'Password must be at least 6 characters.' });
          return;
        }
      }
      const data: UpdateUserPayload = { roleId: Number(roleId) };
      if (password) data.password = password;
      updateMutation.mutate({ id: editingUserId, data });
    } else {
      // Create mode: password is required (server allows default if empty, but
      // we require a real password + confirm here for the employee-onboarding flow).
      if (!password) {
        toast.error('Validation error', { description: 'Password is required.' });
        return;
      }
      if (password !== confirmPassword) {
        toast.error('Validation error', { description: 'Passwords do not match.' });
        return;
      }
      if (password.length < 6) {
        toast.error('Validation error', { description: 'Password must be at least 6 characters.' });
        return;
      }
      createMutation.mutate();
    }
  };

  const dialogTitle = editingUserId ? 'Edit User' : 'Add User';

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3">
        <div className="relative w-full sm:max-w-xs">
          <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-muted-foreground" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search users by name or email..."
            className="w-full border border-border rounded-lg pl-9 pr-3 py-2 text-sm bg-card text-foreground placeholder:text-muted-foreground"
          />
        </div>
        {/* user cann't create mannully, user create only through employee navigation */}
        {/* <button
          onClick={() => setShowDialog(true)}
          className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 self-start sm:self-auto"
        >
          <Plus className="w-4 h-4" /> Add User
        </button> */}
      </div>

      <UsersTable search={search} onEdit={openEdit} />

      {/* Create / Edit user dialog */}
      <Dialog open={showDialog} onOpenChange={open => { if (!open) closeDialog(); }}>
        <DialogContent className="bg-card max-w-lg">
          <DialogHeader>
            <DialogTitle className="font-display">{dialogTitle}</DialogTitle>
          </DialogHeader>

          {fieldsLocked && (
            <div className="mt-1 flex items-start gap-2 bg-muted/50 border border-border rounded-lg px-3 py-2 text-xs text-muted-foreground">
              <ShieldCheck className="w-3.5 h-3.5 mt-0.5 shrink-0" />
              <span>
                Name, email and employee ID are linked to the employee record and cannot be changed here.
              </span>
            </div>
          )}

          <div className="space-y-3 mt-1">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <input
                value={name}
                onChange={e => setName(e.target.value)}
                placeholder="Full Name"
                readOnly={fieldsLocked}
                disabled={fieldsLocked}
                className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground read-only:bg-muted read-only:text-muted-foreground read-only:cursor-not-allowed disabled:opacity-70"
              />
              <input
                value={email}
                onChange={e => setEmail(e.target.value)}
                type="email"
                placeholder="Email"
                readOnly={fieldsLocked}
                disabled={fieldsLocked}
                className="border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground read-only:bg-muted read-only:text-muted-foreground read-only:cursor-not-allowed disabled:opacity-70"
              />
            </div>

            <div className="relative">
              <input
                value={password}
                onChange={e => setPassword(e.target.value)}
                type={showPassword ? 'text' : 'password'}
                placeholder={editingUserId ? 'New password (leave empty to keep current)' : 'Password'}
                className="w-full border border-border rounded-lg pl-3 pr-10 py-2 text-sm bg-background text-foreground"
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

            <div className="relative">
              <input
                value={confirmPassword}
                onChange={e => setConfirmPassword(e.target.value)}
                type={showConfirmPassword ? 'text' : 'password'}
                placeholder="Confirm password"
                className="w-full border border-border rounded-lg pl-3 pr-10 py-2 text-sm bg-background text-foreground"
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

            <input
              value={employeeId}
              onChange={e => setEmployeeId(e.target.value)}
              placeholder="Employee ID link (e.g. EMP-00001)"
              readOnly={fieldsLocked}
              disabled={fieldsLocked}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground font-mono read-only:bg-muted read-only:text-muted-foreground read-only:cursor-not-allowed disabled:opacity-70"
            />

            <select
              value={roleId}
              onChange={e => setRoleId(e.target.value)}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">Select role...</option>
              {assignableRoles.map(r => (
                <option key={r.id} value={r.id}>
                  {roleLabel(r.name)}
                  {r.isSystem ? ' (full access)' : ''}
                </option>
              ))}
            </select>

            <button
              onClick={handleSave}
              disabled={createMutation.isPending || updateMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 disabled:opacity-50"
            >
              {createMutation.isPending || updateMutation.isPending
                ? editingUserId
                  ? 'Saving...'
                  : 'Creating...'
                : editingUserId
                ? 'Save Changes'
                : 'Create User'}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}

export default function UserManagementPage() {
  return (
    <Suspense fallback={<Loader2 className="w-7 h-7 animate-spin text-primary" />}>
      <UserManagementInner />
    </Suspense>
  );
}
