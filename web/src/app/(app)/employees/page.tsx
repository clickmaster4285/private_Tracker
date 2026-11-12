'use client';

import { Suspense, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { motion } from 'framer-motion';
import { Search, Plus, MoreVertical, Loader2, Key, Copy, Check, Eye, Monitor, MonitorOff, Upload, Download, Pencil, Trash2, AlertTriangle, UserPlus, Terminal, ShieldOff, PauseCircle, ScanFace } from 'lucide-react';
import { useQuery, useInfiniteQuery, useMutation, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import { toast } from 'sonner';
import * as XLSX from 'xlsx';
import { employeesApi, departmentsApi, usersApi, shiftsApi, dlpApi, type Employee, type CreateEmployeePayload, type UpdateEmployeePayload, type ImportEmployeeRow, type ImportEmployeesResponse, type Shift, type RemoveEmployeeDeviceResponse, type IssueRemovalCodeResponse, type EmployeeDevice } from '@/lib/api';
import { removeAccountCommands, protectedUninstallCommands } from '@/lib/client-commands';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { EnrollFaceDialog } from '@/components/employees/EnrollFaceDialog';
import { useUrlQueryState } from '@/hooks/use-url-query-state';
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuLabel,
} from '@/components/ui/dropdown-menu';
// Excel header aliases → canonical field. Headers are matched case-insensitively
// and whitespace-normalized, so "User ID", "user_id", "EMPLOYEEID", "employee name"
// and "username" all resolve. Only these columns are extracted from the sheet.
const HEADER_FIELD: Record<string, keyof ImportEmployeeRow> = {
  userid: 'employeeId',
  user_id: 'employeeId',
  'user id': 'employeeId',
  employeeid: 'employeeId',
  employee_id: 'employeeId',
  'employee id': 'employeeId',
  name: 'name',
  'employee name': 'name',
  username: 'name',
  email: 'email',
  department: 'department',
  shift: 'shift',
  schedule: 'shift',
};

const normalizeHeader = (raw: string) =>
  String(raw ?? '').trim().toLowerCase().replace(/\s+/g, ' ');

const PER_PAGE = 10;

export default function UsersList() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <UsersListInner />
    </Suspense>
  );
}

function UsersListInner() {
  const queryClient = useQueryClient();
  const fileInputRef = useRef<HTMLInputElement>(null);
  // URL-synced filter values. The local `searchInput` mirrors the URL's
  // debounced `search` so the input box stays responsive while we wait out
  // the 400 ms debounce before the URL (and the React Query key) catches up.
  const [filters, setFilters] = useUrlQueryState(
    { search: {}, department: {}, status: {} },
    { search: '', department: '', status: '' },
    { debounceMs: 0, history: 'replace' },
  );
  const [searchInput, setSearchInput] = useState(filters.search);
  // Debounced: push the input to the URL (and therefore to the React Query
  // key + server request) 400 ms after the user stops typing. A manual URL
  // edit propagates back through `filters.search` via the hook's read effect.
  useEffect(() => {
    const t = setTimeout(() => {
      if (searchInput !== filters.search) setFilters({ search: searchInput });
    }, 400);
    return () => clearTimeout(t);
  }, [searchInput, filters.search, setFilters]);
  useEffect(() => {
    setSearchInput(filters.search);
  }, [filters.search]);
  const [showAdd, setShowAdd] = useState(false);
  const [showEdit, setShowEdit] = useState<string | null>(null);
  const [showSecret, setShowSecret] = useState<Employee | null>(null);
  const [showEnrollFace, setShowEnrollFace] = useState<Employee | null>(null);
  const [showRemove, setShowRemove] = useState<Employee | null>(null);
  const [secretValue, setSecretValue] = useState('');
  const [copied, setCopied] = useState(false);
  // Which OS command row was last copied — the modal shows Linux and Windows side by side.
  const [copiedCommand, setCopiedCommand] = useState<string | null>(null);
  // Result of the last "Remove Current Device" in the open modal. While set, the
  // modal switches to its post-removal state, which is where the operator gets the
  // plain `--remove-account` command to finish the handover on that machine.
  const [removeResult, setRemoveResult] = useState<RemoveEmployeeDeviceResponse | null>(null);
  const [importResult, setImportResult] = useState<ImportEmployeesResponse | null>(null);
  const [showRemovalCode, setShowRemovalCode] = useState<Employee | null>(null);
  const [removalDevices, setRemovalDevices] = useState<EmployeeDevice[]>([]);
  const [removalMachineId, setRemovalMachineId] = useState('');
  const [removalIssued, setRemovalIssued] = useState<IssueRemovalCodeResponse | null>(null);
  const [removalLoading, setRemovalLoading] = useState(false);
  // Pause lockdown dialog: resolves the employee's active machine from detail, then POSTs.
  const [showPause, setShowPause] = useState<Employee | null>(null);
  const [pauseMachineId, setPauseMachineId] = useState('');
  const [pauseMinutes, setPauseMinutes] = useState(60);
  const [pauseLoading, setPauseLoading] = useState(false);
  const [pauseSubmitting, setPauseSubmitting] = useState(false);
  // Which OS protected-uninstall command was last copied (Linux | Windows).
  const [copiedRemovalCmd, setCopiedRemovalCmd] = useState<string | null>(null);
  // Form state
  const [newName, setNewName] = useState('');
  const [newEmail, setNewEmail] = useState('');
  const [newDeptId, setNewDeptId] = useState(1);
  const [newShiftId, setNewShiftId] = useState<number | null>(null);
  const [editName, setEditName] = useState('');
  const [editEmail, setEditEmail] = useState('');
  const [editDeptId, setEditDeptId] = useState(1);
  const [editShiftId, setEditShiftId] = useState<number | null>(null);

  // ── Queries ──
  // Server-side pagination with infinite scroll (same pattern as the Session
  // Timeline / Web Activity journey pages). The Next/Previous buttons are
  // banned — see AGENTS.md §6 (Web Infinite-Scroll Rule).
  const {
    data: employeesData,
    isLoading: employeesLoading,
    error: employeesError,
    isFetchingNextPage,
    fetchNextPage,
    hasNextPage,
  } = useInfiniteQuery({
    queryKey: ['employees', { search: filters.search, department: filters.department, status: filters.status, perPage: PER_PAGE }],
    queryFn: ({ pageParam }) => employeesApi.list({
      page: pageParam as number,
      perPage: PER_PAGE,
      search: filters.search || undefined,
      department: filters.department || undefined,
      status: filters.status || undefined,
    }),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page < last.totalPages ? last.page + 1 : undefined),
    placeholderData: keepPreviousData,
  });

  const employees = useMemo(() => employeesData?.pages.flatMap(p => p.data) ?? [], [employeesData]);
  const total = employeesData?.pages[0]?.total ?? 0;

  // Sentinel that triggers the next page fetch when scrolled into view.
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries[0]?.isIntersecting && hasNextPage && !isFetchingNextPage) {
          fetchNextPage();
        }
      },
      { rootMargin: '300px' },
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasNextPage, isFetchingNextPage, fetchNextPage]);

  const { data: deptResponse } = useQuery({
    queryKey: ['departments'],
    queryFn: () => departmentsApi.list(),
  });

  const departments = deptResponse?.departments || [];

  // Shifts dropdown source — fetched once, used by both Add and Edit dialogs.
  // Mirrors the departments query: listAll returns the full unpaged catalog.
  const { data: shiftsResponse } = useQuery({
    queryKey: ['shifts', 'all'],
    queryFn: () => shiftsApi.listAll(),
    staleTime: 60_000,
  });

  const shifts: Shift[] = shiftsResponse?.shifts || [];

  // hasUserLogin comes from the server (indexed EXISTS() per row) — no
  // client-side map or extra round-trips needed, and it scales with the
  // paginated list instead of growing with total user count.

  // ── Mutations ──
  const createMutation = useMutation({
    mutationFn: (data: CreateEmployeePayload) => employeesApi.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Employee created', { description: 'The employee has been added successfully.' });
      setShowAdd(false);
      resetForm();
    },
    onError: (err: Error) => {
      toast.error('Failed to create employee', { description: err.message });
    },
  });

  const updateMutation = useMutation({
    mutationFn: async ({ id, data }: { id: string; data: UpdateEmployeePayload }) => {
      const updated = await employeesApi.update(id, data);
      // If this employee has a linked login account, propagate name/email
      // changes to the user record so the two surfaces stay in sync. The
      // employeeId is immutable from both sides, so it never needs syncing.
      // The hasUserLogin flag is projected by the server's UPDATE…RETURNING
      // so this check is free (no extra round-trip) and always fresh.
      if (updated.hasUserLogin) {
        try {
          const userList = await usersApi.list({ search: updated.employeeId, perPage: 1 });
          const linkedUser = userList.data.find(u => u.employeeId === updated.employeeId);
          if (linkedUser) {
            const payload: { name?: string; email?: string } = {};
            if (data.name !== undefined) payload.name = updated.name;
            if (data.email !== undefined) payload.email = updated.email;
            if (Object.keys(payload).length > 0) {
              await usersApi.update(linkedUser.id, payload);
            }
          }
        } catch (e) {
          // Non-fatal: the employee update succeeded; the user sync failed.
          toast.warning('Attached login account not synced', {
            description: (e as Error).message,
          });
        }
      }
      return updated;
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      queryClient.invalidateQueries({ queryKey: ['users'] });
      toast.success('Employee updated', { description: 'The employee has been updated successfully.' });
      setShowEdit(null);
    },
    onError: (err: Error) => {
      toast.error('Failed to update employee', { description: err.message });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => employeesApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      toast.success('Employee deleted', { description: 'The employee has been removed.' });
    },
    onError: (err: Error) => {
      toast.error('Failed to delete employee', { description: err.message });
    },
  });

  // "Remove current device": revoke the employee's devices, freeze every open
  // session/item and flip the employee to untracked. Nothing is deleted — the old
  // machine stays visible as history under Device Specs — and the row switches back
  // to "Generate Login Secret" so a new machine can be onboarded.
  //
  // The dialog deliberately STAYS OPEN on success: revoking the device is only the
  // server half of a handover, and the post-removal state is where the admin gets the
  // `--remove-account` client command for that machine. (The menu item that opens this
  // modal disappears once the employee is untracked, so the command would otherwise
  // become unreachable.)
  const removeDeviceMutation = useMutation({
    mutationFn: (id: string) => employeesApi.removeDevice(id),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      queryClient.invalidateQueries({ queryKey: ['employee-detail'] });
      setRemoveResult(data);
      toast.success('Device removed', {
        description: `${data.revokedDevices} device${data.revokedDevices === 1 ? '' : 's'} revoked · ${data.closedSessions} session${data.closedSessions === 1 ? '' : 's'} closed. The employee is now untracked.`,
        duration: 9000,
      });
    },
    onError: (err: Error) => {
      toast.error('Failed to remove device', { description: err.message });
    },
  });

  const secretMutation = useMutation({
    mutationFn: (id: string) => employeesApi.generateSecret(id),
    onSuccess: (data) => {
      setSecretValue(data.secret);
      toast.success('Login secret generated!', {
        description: `Secret expires in ${data.expiresIn} seconds. Copy it now — it won't be shown again.`,
        duration: 8000,
      });
    },
    onError: (err: Error) => {
      toast.error('Failed to generate secret', { description: err.message });
    },
  });

  const importMutation = useMutation({
    mutationFn: (rows: ImportEmployeeRow[]) => employeesApi.import(rows),
    onSuccess: (data) => {
      queryClient.invalidateQueries({ queryKey: ['employees'] });
      queryClient.invalidateQueries({ queryKey: ['departments'] });
      setImportResult(data);
      toast.success('Import complete', {
        description: `${data.imported} imported, ${data.updated} updated, ${data.skipped} skipped.`,
      });
    },
    onError: (err: Error) => {
      toast.error('Import failed', { description: err.message });
    },
  });

  const exportMutation = useMutation({
    mutationFn: () => employeesApi.export(),
    onSuccess: (rows) => {
      const aoa: (string | number)[][] = [
        ['Employee ID', 'Name', 'Email', 'Department', 'Shift'],
        ...rows.map(r => [r.employeeId, r.name, r.email, r.department, r.shift]),
      ];
      const ws = XLSX.utils.aoa_to_sheet(aoa);
      const wb = XLSX.utils.book_new();
      XLSX.utils.book_append_sheet(wb, ws, 'Employees');
      XLSX.writeFile(wb, `employees-${new Date().toISOString().slice(0, 10)}.xlsx`);
      toast.success('Export complete', { description: `${rows.length} employees exported.` });
    },
    onError: (err: Error) => {
      toast.error('Export failed', { description: err.message });
    },
  });

  const handleImportFile = (file: File) => {
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (e) => {
      try {
        const wb = XLSX.read(e.target?.result, { type: 'array' });
        const ws = wb.Sheets[wb.SheetNames[0]];
        if (!ws) {
          toast.error('Import failed', { description: 'The workbook has no sheets.' });
          return;
        }
        const rawRows = XLSX.utils.sheet_to_json<Record<string, unknown>>(ws, { defval: '' });
        const rows = rawRows
          .map(raw => {
            const row: ImportEmployeeRow = { employeeId: '', name: '', email: '', department: '' };
            for (const [header, value] of Object.entries(raw)) {
              const field = HEADER_FIELD[normalizeHeader(header)];
              if (field) row[field] = String(value ?? '').trim();
            }
            return row;
          })
          .filter(row => row.employeeId || row.name);
        if (rows.length === 0) {
          toast.error('Import failed', {
            description: 'No recognized rows. Expected headers like User ID, Name, Email, Department.',
          });
          return;
        }
        importMutation.mutate(rows);
      } catch (err) {
        toast.error('Import failed', { description: (err as Error).message });
      }
    };
    reader.readAsArrayBuffer(file);
  };

  const resetForm = () => {
    setNewName('');
    setNewEmail('');
    setNewDeptId(1);
    setNewShiftId(null);
  };

  const handleAdd = () => {
    if (!newName) {
      toast.error('Validation error', { description: 'Name is required.' });
      return;
    }
    createMutation.mutate({
      name: newName,
      email: newEmail,
      departmentId: newDeptId,
      shiftId: newShiftId,
    });
  };

  const handleEdit = (emp: Employee) => {
    setShowEdit(emp.id);
    setEditName(emp.name);
    setEditEmail(emp.email);
    setEditDeptId(emp.departmentId);
    setEditShiftId(emp.shiftId);
  };

  const handleSaveEdit = (id: string) => {
    updateMutation.mutate({
      id,
      data: {
        name: editName,
        email: editEmail,
        departmentId: editDeptId,
        shiftId: editShiftId,
      },
    });
  };

  const handleDelete = (id: string) => {
    deleteMutation.mutate(id);
  };

  const handleGenerateSecret = (emp: Employee) => {
    setShowSecret(emp);
    setSecretValue('');
    setCopied(false);
    secretMutation.mutate(emp.id);
  };

  const copyToClipboard = async (text: string): Promise<boolean> => {
    if (navigator.clipboard?.writeText) {
      try {
        await navigator.clipboard.writeText(text);
        return true;
      } catch {
        // fall through to legacy fallback
      }
    }
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.top = '0';
    textarea.style.left = '0';
    textarea.style.opacity = '0';
    textarea.readOnly = true;
    document.body.appendChild(textarea);
    textarea.select();
    let ok = false;
    try {
      ok = document.execCommand('copy');
    } catch {
      ok = false;
    } finally {
      document.body.removeChild(textarea);
    }
    return ok;
  };

  const handleCopySecret = async () => {
    const ok = await copyToClipboard(secretValue);
    if (ok) {
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } else {
      toast.error('Copy failed', { description: 'Your browser blocked clipboard access. Copy the secret manually.' });
    }
  };

  // Copies one of the `--remove-account` client commands shown in the Remove Device
  // modal (Linux + Windows variants, see lib/client-commands.ts).
  const handleCopyCommand = async (os: string, command: string) => {
    const ok = await copyToClipboard(command);
    if (ok) {
      setCopiedCommand(os);
      setTimeout(() => setCopiedCommand(null), 2000);
    } else {
      toast.error('Copy failed', { description: 'Your browser blocked clipboard access. Copy the command manually.' });
    }
  };

  /**
   * Plain `--remove-account` commands for the Remove Device modal (no --code).
   * Fixed machines need Issue Removal Code separately. This modal is the only place
   * the admin can copy the command after revoke — the row menu item disappears once
   * the employee becomes untracked.
   */
  const renderRemoveAccountCommands = () => (
    <div className="border border-border rounded-xl overflow-hidden">
      <div className="px-3 py-2 bg-muted/40 border-b border-border flex items-center gap-2">
        <Terminal className="w-3.5 h-3.5 text-muted-foreground shrink-0" />
        <p className="text-xs font-semibold text-foreground">Remove the account on that machine</p>
      </div>
      <div className="p-3 space-y-2.5">
        {removeAccountCommands().map(({ os, shell, command }) => (
          <div key={os} className="space-y-1">
            <div className="flex items-baseline gap-2">
              <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-semibold">{os}</p>
              <p className="text-[10px] text-muted-foreground/70">{shell}</p>
            </div>
            <div className="bg-background border border-border rounded-lg p-1.5 flex items-center gap-1.5">
              <code className="flex-1 text-[11px] font-mono text-foreground select-all px-2 py-1.5 break-all">{command}</code>
              <button
                onClick={() => handleCopyCommand(os, command)}
                className={`px-2.5 py-1.5 rounded-lg text-[11px] font-semibold flex items-center gap-1.5 transition-colors shrink-0 ${
                  copiedCommand === os
                    ? 'bg-success/15 text-success'
                    : 'bg-primary text-primary-foreground hover:opacity-90'
                }`}
              >
                {copiedCommand === os ? (
                  <><Check className="w-3 h-3" /> Copied</>
                ) : (
                  <><Copy className="w-3 h-3" /> Copy</>
                )}
              </button>
            </div>
          </div>
        ))}
        <p className="text-[11px] text-muted-foreground">
          Syncs unsynced rows when online, then wipes the local DB and returns to sign-in. Offline: wipes immediately.
        </p>
      </div>
    </div>
  );

  // ── Loading state ──
  if (employeesLoading && !employeesData) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Loading employees...</p>
        </div>
      </div>
    );
  }

  // ── Error state ──
  if (employeesError) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="text-center">
          <p className="text-destructive font-medium mb-2">Failed to load employees</p>
          <p className="text-sm text-muted-foreground">{(employeesError as Error).message}</p>
          <button
            onClick={() => queryClient.invalidateQueries({ queryKey: ['employees'] })}
            className="mt-4 text-sm text-primary hover:underline"
          >
            Try again
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      {/* Filters */}
      <div className="flex justify-between">
        <div className="flex items-center bg-card border border-border rounded-lg px-3 py-2 gap-2 flex-1 max-w-sm">
          <Search className="w-4 h-4 text-muted-foreground" />
          <input
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Search by name, email, or ID"
            className="bg-transparent border-none outline-none text-sm flex-1 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <div className='flex space-x-3'>
          <select
            value={filters.department}
            onChange={e => setFilters({ department: e.target.value })}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Departments</option>
            {departments.map(d => <option key={d.id} value={d.name}>{d.name}</option>)}
          </select>
          <select
            value={filters.status}
            onChange={e => setFilters({ status: e.target.value })}
            className="bg-card border border-border rounded-lg px-3 py-2 text-sm text-foreground"
          >
            <option value="">All Statuses</option>
            <option value="tracked">Tracked</option>
            <option value="untracked">Untracked</option>
          </select>
          <input
            ref={fileInputRef}
            type="file"
            accept=".xlsx,.xls,.csv"
            className="hidden"
            onChange={(e) => {
              const file = e.target.files?.[0];
              if (file) handleImportFile(file);
              e.target.value = '';
            }}
          />
          <button
            onClick={() => fileInputRef.current?.click()}
            disabled={importMutation.isPending}
            className="bg-card border border-border text-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:bg-muted transition-colors disabled:opacity-50"
          >
            <Upload className="w-4 h-4" />
            {importMutation.isPending ? 'Importing...' : 'Import'}
          </button>
          <button
            onClick={() => exportMutation.mutate()}
            disabled={exportMutation.isPending}
            className="bg-card border border-border text-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:bg-muted transition-colors disabled:opacity-50"
          >
            <Download className="w-4 h-4" />
            {exportMutation.isPending ? 'Exporting...' : 'Export'}
          </button>
          <button
            onClick={() => setShowAdd(true)}
            className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity"
          >
            <Plus className="w-4 h-4" /> Add Employee
          </button>
        </div>
      </div>

      {/* Table */}
      <div className="bg-card rounded-xl border border-border overflow-x-auto">
        <table className="w-full min-w-[700px]">
          <thead>
            <tr className="border-b border-border">
              {['Name', 'Employee ID', 'Email', 'Department', 'Shift', 'Version', 'Status', 'Action'].map(h => (
                <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {employees.length === 0 ? (
              <tr>
                <td colSpan={8} className="text-center py-12 text-muted-foreground text-sm">
                  No employees found
                </td>
              </tr>
            ) : (
              employees.map((emp, i) => (
                <motion.tr
                  key={emp.id}
                  initial={{ opacity: 0 }}
                  animate={{ opacity: 1 }}
                  transition={{ delay: i * 0.03 }}
                  className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors"
                >
                  <td className="px-4 py-3">
                    <div className="flex items-center gap-3">
                      <div
                        className="w-8 h-8 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground"
                        style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                      >
                        {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                      </div>
                      <div
                        className="text-sm font-medium text-foreground hover:text-primary transition-colors"
                      >
                        {emp.name}
                      </div>
                    </div>
                  </td>
                  <td className="px-4 py-3 text-sm font-mono font-medium text-foreground">{emp.employeeId}</td>
                  <td className="px-4 py-3 text-sm text-muted-foreground">{emp.email}</td>
                  <td className="px-4 py-3 text-sm text-foreground">{emp.department}</td>
                  <td className="px-4 py-3 text-sm text-foreground">
                    {emp.shift || <span className="text-muted-foreground/50 italic">Unassigned</span>}
                  </td>
                  <td className="px-4 py-3 text-sm font-mono text-foreground">
                    {emp.clientVersion || <span className="text-muted-foreground/50 italic">Unknown</span>}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${
                      emp.trackingStatus === 'tracked'
                        ? 'bg-success/15 text-success'
                        : 'bg-warning/15 text-warning'
                    }`}>
                      {emp.trackingStatus === 'tracked' ? 'Tracked' : 'Untracked'}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <button
                          className="p-2 rounded-lg text-muted-foreground hover:text-foreground hover:bg-muted/70 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary/40"
                          aria-label={`Actions for ${emp.name}`}
                        >
                          <MoreVertical className="w-4 h-4" />
                        </button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end" sideOffset={8} className="w-56 rounded-xl bg-card border-border shadow-xl p-1.5">
                        <DropdownMenuLabel className="flex items-center gap-3 px-2.5 py-2.5">
                          <div
                            className="w-9 h-9 rounded-full flex items-center justify-center text-xs font-bold text-primary-foreground shrink-0"
                            style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                          >
                            {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                          </div>
                          <div className="min-w-0">
                            <p className="text-sm font-semibold text-foreground truncate">{emp.name}</p>
                            <p className="text-xs text-muted-foreground truncate">{emp.email}</p>
                          </div>
                        </DropdownMenuLabel>
                        <DropdownMenuSeparator className="my-1.5" />
                        <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Link href={`/employee-journey/timeline?employeeId=${emp.id}`}>
                            <Eye className="w-4 h-4 text-muted-foreground" />
                            View Journey
                          </Link>
                        </DropdownMenuItem>
                        <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Link href={`/device-specs?employeeId=${emp.id}`}>
                            <Monitor className="w-4 h-4 text-muted-foreground" />
                            Device Specs
                          </Link>
                        </DropdownMenuItem>
                        <DropdownMenuItem onSelect={() => handleEdit(emp)} className="cursor-pointer gap-2.5 px-2.5 py-2">
                          <Pencil className="w-4 h-4 text-muted-foreground" />
                          Edit Details
                        </DropdownMenuItem>
                        {!emp.faceEnrolled && (
                          <DropdownMenuItem
                            onSelect={() => setShowEnrollFace(emp)}
                            className="cursor-pointer gap-2.5 px-2.5 py-2"
                          >
                            <ScanFace className="w-4 h-4 text-muted-foreground" />
                            Enroll Face…
                          </DropdownMenuItem>
                        )}
                        {!emp.hasUserLogin && (
                          <DropdownMenuItem asChild className="cursor-pointer gap-2.5 px-2.5 py-2">
                            <Link href={`/settings/user-management?create=1&employeeId=${encodeURIComponent(emp.employeeId)}&name=${encodeURIComponent(emp.name)}&email=${encodeURIComponent(emp.email)}`}>
                              <UserPlus className="w-4 h-4 text-muted-foreground" />
                              Login Credential
                            </Link>
                          </DropdownMenuItem>
                        )}
                        {emp.trackingStatus === 'tracked' ? (
                          <>
                            <DropdownMenuItem
                              onSelect={() => {
                                setRemoveResult(null);
                                setCopiedCommand(null);
                                setShowRemove(emp);
                              }}
                              className="cursor-pointer gap-2.5 px-2.5 py-2 text-warning focus:text-warning focus:bg-warning/10"
                            >
                              <MonitorOff className="w-4 h-4" />
                              Remove Current Device
                            </DropdownMenuItem>
                            <DropdownMenuItem
                              onSelect={() => {
                                setRemovalIssued(null);
                                setCopiedRemovalCmd(null);
                                setRemovalMachineId('');
                                setRemovalDevices([]);
                                setShowRemovalCode(emp);
                                setRemovalLoading(true);
                                employeesApi.detail(emp.id)
                                  .then((d) => {
                                    const devices = (d.devices ?? []).filter((x) => x.status === 'active' || !x.revokedAt);
                                    const list = devices.length > 0 ? devices : (d.devices ?? []);
                                    setRemovalDevices(list);
                                    const preferred = list.find((x) => x.status === 'active') ?? list[0];
                                    if (preferred?.machineId) setRemovalMachineId(preferred.machineId);
                                  })
                                  .catch(() => toast.error('Could not load devices for this employee'))
                                  .finally(() => setRemovalLoading(false));
                              }}
                              className="cursor-pointer gap-2.5 px-2.5 py-2"
                            >
                              <ShieldOff className="w-4 h-4 text-muted-foreground" />
                              Issue Removal Code
                            </DropdownMenuItem>
                            <DropdownMenuItem
                              onSelect={() => {
                                setPauseMachineId('');
                                setPauseMinutes(60);
                                setShowPause(emp);
                                setPauseLoading(true);
                                employeesApi.detail(emp.id)
                                  .then((d) => {
                                    const active = (d.devices ?? []).find((x) => x.status === 'active');
                                    if (active?.machineId) setPauseMachineId(active.machineId);
                                  })
                                  .catch(() => toast.error('Could not load devices for this employee'))
                                  .finally(() => setPauseLoading(false));
                              }}
                              className="cursor-pointer gap-2.5 px-2.5 py-2"
                            >
                              <PauseCircle className="w-4 h-4 text-muted-foreground" />
                              Pause lockdown…
                            </DropdownMenuItem>
                          </>
                        ) : (
                          <DropdownMenuItem onSelect={() => handleGenerateSecret(emp)} className="cursor-pointer gap-2.5 px-2.5 py-2">
                            <Key className="w-4 h-4 text-muted-foreground" />
                            Generate Login Secret
                          </DropdownMenuItem>
                        )}
                        <DropdownMenuSeparator className="my-1.5" />
                        <DropdownMenuItem
                          onSelect={() => handleDelete(emp.id)}
                          disabled={deleteMutation.isPending}
                          className="cursor-pointer gap-2.5 px-2.5 py-2 text-destructive focus:text-destructive focus:bg-destructive/10"
                        >
                          <Trash2 className="w-4 h-4" />
                          {deleteMutation.isPending ? 'Deleting...' : 'Delete Employee'}
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </td>
                </motion.tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {/* Infinite scroll footer (server-side pagination; no Next/Previous buttons) */}
      {hasNextPage ? (
        <div ref={sentinelRef} className="h-12 flex items-center justify-center text-xs text-muted-foreground">
          {isFetchingNextPage ? (
            <span className="flex items-center gap-2 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading more…
            </span>
          ) : (
            'Scroll for more'
          )}
        </div>
      ) : (
        employees.length > 0 && (
          <p className="text-sm text-muted-foreground text-center">
            Showing all {total.toLocaleString()} employee{total === 1 ? '' : 's'}
          </p>
        )
      )}

      {/* Add Dialog */}
      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Add New Employee</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="Full Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <input
              value={newEmail}
              onChange={e => setNewEmail(e.target.value)}
              placeholder="Email"
              type="email"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={newDeptId}
              onChange={e => setNewDeptId(Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
            <select
              value={newShiftId ?? ''}
              onChange={e => setNewShiftId(e.target.value === '' ? null : Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">No shift assigned</option>
              {shifts.map(s => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.startTime}–{s.endTime})
                </option>
              ))}
            </select>
            <button
              onClick={handleAdd}
              disabled={createMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {createMutation.isPending ? 'Creating...' : 'Add Employee'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog open={!!showEdit} onOpenChange={(open) => { if (!open) setShowEdit(null); }}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader>
            <DialogTitle className="font-display">Edit Employee</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={editName}
              onChange={e => setEditName(e.target.value)}
              placeholder="Full Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <input
              value={editEmail}
              onChange={e => setEditEmail(e.target.value)}
              placeholder="Email"
              type="email"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={editDeptId}
              onChange={e => setEditDeptId(Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {departments.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
            </select>
            <select
              value={editShiftId ?? ''}
              onChange={e => setEditShiftId(e.target.value === '' ? null : Number(e.target.value))}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              <option value="">No shift assigned</option>
              {shifts.map(s => (
                <option key={s.id} value={s.id}>
                  {s.name} ({s.startTime}–{s.endTime})
                </option>
              ))}
            </select>
            <button
              onClick={() => showEdit && handleSaveEdit(showEdit)}
              disabled={updateMutation.isPending}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Remove Current Device — confirmation, then plain --remove-account handover */}
      <Dialog
        open={showRemove !== null}
        onOpenChange={(open) => {
          if (!open) {
            setShowRemove(null);
            setRemoveResult(null);
            setCopiedCommand(null);
          }
        }}
      >
        <DialogContent className="bg-card sm:max-w-[560px]">
          <DialogHeader>
            <DialogTitle className="font-display">
              {removeResult ? 'Device Removed' : 'Remove Current Device'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            {showRemove && (
              <div className="flex items-center gap-3 bg-background border border-border rounded-xl p-3.5">
                <div
                  className="w-11 h-11 rounded-full flex items-center justify-center text-sm font-bold text-primary-foreground shrink-0"
                  style={{ backgroundColor: showRemove.avatarColor || '#7C3AED' }}
                >
                  {showRemove.avatar || showRemove.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-foreground truncate">{showRemove.name}</p>
                  <p className="text-xs text-muted-foreground truncate">{showRemove.email}</p>
                </div>
                <div className="text-right shrink-0">
                  <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Employee ID</p>
                  <p className="text-sm font-mono font-medium text-foreground">{showRemove.employeeId}</p>
                </div>
              </div>
            )}

            {removeResult ? (
              <>
                <div className="flex items-start gap-2.5 bg-success/10 border border-success/30 rounded-xl p-3">
                  <Check className="w-4 h-4 text-success mt-0.5 shrink-0" />
                  <div className="text-xs text-foreground space-y-1.5">
                    <p>
                      <span className="font-semibold">{removeResult.revokedDevices}</span> device
                      {removeResult.revokedDevices === 1 ? '' : 's'} revoked ·{' '}
                      <span className="font-semibold">{removeResult.closedSessions}</span> session
                      {removeResult.closedSessions === 1 ? '' : 's'} closed ·{' '}
                      <span className="font-semibold">{removeResult.closedItems}</span> activity item
                      {removeResult.closedItems === 1 ? '' : 's'} closed.
                    </p>
                    <p className="text-muted-foreground">
                      The employee is now <span className="font-semibold text-foreground">Untracked</span>. Device Specs
                      and all history stay available.
                    </p>
                  </div>
                </div>

                <p className="text-xs text-muted-foreground">
                  To finish the handover, run this on that machine to wipe the local account:
                </p>
                {renderRemoveAccountCommands()}

                <p className="text-xs text-muted-foreground">
                  On the replacement machine, <span className="font-semibold text-foreground">Generate Login Secret</span>{' '}
                  (row menu) lets the employee sign in and tracking resumes.
                </p>
              </>
            ) : (
              <>
                <div className="flex items-start gap-2.5 bg-warning/10 border border-warning/30 rounded-xl p-3">
                  <AlertTriangle className="w-4 h-4 text-warning mt-0.5 shrink-0" />
                  <div className="text-xs text-foreground space-y-1.5">
                    <p>This detaches the machine the employee is currently tracked on:</p>
                    <ul className="list-disc pl-4 space-y-1 text-muted-foreground">
                      <li>The device registration is revoked — that machine can no longer sync.</li>
                      <li>Every running session and activity item is closed and the employee becomes <span className="font-semibold text-foreground">Untracked</span>.</li>
                      <li>Nothing is deleted. Device Specs and all history stay available.</li>
                    </ul>
                    <p className="pt-1">Afterwards, <span className="font-semibold">Generate Login Secret</span> lets them sign in on the replacement machine and tracking resumes.</p>
                  </div>
                </div>

                <p className="text-xs text-muted-foreground">
                  After remove, you will also need this command on the machine:
                </p>
                {renderRemoveAccountCommands()}
              </>
            )}

            {removeResult ? (
              <button
                onClick={() => {
                  setShowRemove(null);
                  setRemoveResult(null);
                  setCopiedCommand(null);
                }}
                className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
              >
                Close
              </button>
            ) : (
              <div className="flex gap-2">
                <button
                  onClick={() => setShowRemove(null)}
                  disabled={removeDeviceMutation.isPending}
                  className="flex-1 border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
                >
                  Cancel
                </button>
                <button
                  onClick={() => showRemove && removeDeviceMutation.mutate(showRemove.id)}
                  disabled={removeDeviceMutation.isPending}
                  className="flex-1 bg-destructive text-destructive-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50 flex items-center justify-center gap-2"
                >
                  {removeDeviceMutation.isPending ? (
                    <><Loader2 className="w-4 h-4 animate-spin" /> Removing…</>
                  ) : (
                    <><MonitorOff className="w-4 h-4" /> Remove Device</>
                  )}
                </button>
              </div>
            )}
          </div>
        </DialogContent>
      </Dialog>

      {/* Issue Removal Code — one-time code for --protected-uninstall (Linux + Windows) */}
      <Dialog
        open={showRemovalCode !== null}
        onOpenChange={(open) => {
          if (!open) {
            setShowRemovalCode(null);
            setRemovalIssued(null);
            setRemovalDevices([]);
            setRemovalMachineId('');
            setCopiedRemovalCmd(null);
          }
        }}
      >
        <DialogContent className="bg-card sm:max-w-[560px]">
          <DialogHeader>
            <DialogTitle className="font-display">
              {removalIssued ? 'Removal Code Issued' : 'Issue Removal Code'}
            </DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            {showRemovalCode && (
              <div className="flex items-center gap-3 bg-background border border-border rounded-xl p-3.5">
                <div
                  className="w-11 h-11 rounded-full flex items-center justify-center text-sm font-bold text-primary-foreground shrink-0"
                  style={{ backgroundColor: showRemovalCode.avatarColor || '#7C3AED' }}
                >
                  {showRemovalCode.avatar || showRemovalCode.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-foreground truncate">{showRemovalCode.name}</p>
                  <p className="text-xs text-muted-foreground truncate">{showRemovalCode.employeeId}</p>
                </div>
              </div>
            )}

            {removalIssued ? (
              <>
                <div className="flex items-start gap-2.5 bg-warning/10 border border-warning/30 rounded-xl p-3">
                  <AlertTriangle className="w-4 h-4 text-warning mt-0.5 shrink-0" />
                  <p className="text-xs text-foreground">
                    This code is shown <span className="font-semibold">once</span>, expires{' '}
                    <span className="font-mono">{new Date(removalIssued.expiresAt).toLocaleString()}</span>, and
                    authorizes protected uninstall on that machine.
                  </p>
                </div>
                <div className="bg-background border border-border rounded-xl p-1.5 flex items-center gap-1.5">
                  <code className="flex-1 text-sm font-mono font-bold text-foreground select-all px-2.5 py-2 break-all">
                    {removalIssued.code}
                  </code>
                  <button
                    onClick={() => {
                      navigator.clipboard.writeText(removalIssued.code);
                      toast.success('Code copied');
                    }}
                    className="px-3 py-2 rounded-lg text-xs font-semibold bg-primary text-primary-foreground shrink-0"
                  >
                    <Copy className="w-3.5 h-3.5" />
                  </button>
                </div>
                <div className="border border-border rounded-xl overflow-hidden">
                  <div className="px-3 py-2 bg-muted/40 border-b border-border flex items-center gap-2">
                    <Terminal className="w-3.5 h-3.5 text-muted-foreground shrink-0" />
                    <p className="text-xs font-semibold text-foreground">Run on the employee machine</p>
                  </div>
                  <div className="p-3 space-y-2.5">
                    {protectedUninstallCommands(removalIssued.code).map(({ os, shell, command }) => (
                      <div key={os} className="space-y-1">
                        <div className="flex items-baseline gap-2">
                          <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-semibold">{os}</p>
                          <p className="text-[10px] text-muted-foreground/70">{shell}</p>
                        </div>
                        <div className="bg-background border border-border rounded-lg p-1.5 flex items-start gap-1.5">
                          <code className="flex-1 text-[11px] font-mono text-foreground select-all px-2 py-1.5 break-all">{command}</code>
                          <button
                            onClick={async () => {
                              const ok = await copyToClipboard(command);
                              if (ok) {
                                setCopiedRemovalCmd(os);
                                setTimeout(() => setCopiedRemovalCmd(null), 2000);
                                toast.success('Command copied');
                              } else {
                                toast.error('Copy failed', { description: 'Your browser blocked clipboard access. Copy the command manually.' });
                              }
                            }}
                            className={`px-2.5 py-1.5 rounded-lg text-[11px] font-semibold flex items-center gap-1.5 transition-colors shrink-0 ${
                              copiedRemovalCmd === os
                                ? 'bg-success/15 text-success'
                                : 'bg-primary text-primary-foreground hover:opacity-90'
                            }`}
                          >
                            {copiedRemovalCmd === os ? (
                              <><Check className="w-3 h-3" /> Copied</>
                            ) : (
                              <><Copy className="w-3 h-3" /> Copy</>
                            )}
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
                <button
                  onClick={() => { setShowRemovalCode(null); setRemovalIssued(null); }}
                  className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted"
                >
                  Close
                </button>
              </>
            ) : (
              <>
                <p className="text-xs text-muted-foreground">
                  Issues a one-time code for <span className="font-mono">--protected-uninstall</span> on Linux or
                  Windows. On Windows the LocalSystem DLP service verifies it and unlocks its ACL; on both platforms
                  the tracker then wipes the local account.
                </p>
                {removalLoading ? (
                  <div className="flex justify-center py-6"><Loader2 className="w-6 h-6 animate-spin text-primary" /></div>
                ) : removalDevices.length === 0 ? (
                  <p className="text-sm text-destructive">No machines registered for this employee.</p>
                ) : (
                  <select
                    value={removalMachineId}
                    onChange={(e) => setRemovalMachineId(e.target.value)}
                    className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
                  >
                    {removalDevices.map((d) => (
                      <option key={d.machineId} value={d.machineId}>
                        {(d.hostname || d.deviceName || d.machineId)} · {d.status}
                        {d.machineId ? ` (${d.machineId.slice(0, 12)}…)` : ''}
                      </option>
                    ))}
                  </select>
                )}
                <div className="flex gap-2">
                  <button
                    onClick={() => setShowRemovalCode(null)}
                    className="flex-1 border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted"
                  >
                    Cancel
                  </button>
                  <button
                    disabled={!removalMachineId || !showRemovalCode || removalLoading}
                    onClick={async () => {
                      if (!showRemovalCode || !removalMachineId) return;
                      setRemovalLoading(true);
                      try {
                        const resp = await dlpApi.issueRemovalCode({
                          machineId: removalMachineId,
                          employeeId: showRemovalCode.employeeId,
                          purpose: 'both',
                        });
                        setRemovalIssued(resp);
                        toast.success('Removal code issued');
                      } catch (e) {
                        toast.error(e instanceof Error ? e.message : 'Failed to issue code');
                      } finally {
                        setRemovalLoading(false);
                      }
                    }}
                    className="flex-1 bg-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 disabled:opacity-50 flex items-center justify-center gap-2"
                  >
                    {removalLoading ? <Loader2 className="w-4 h-4 animate-spin" /> : <ShieldOff className="w-4 h-4" />}
                    Issue Code
                  </button>
                </div>
              </>
            )}
          </div>
        </DialogContent>
      </Dialog>

      {/* Pause lockdown — temporary lockdown bypass on the employee's active machine */}
      <Dialog
        open={showPause !== null}
        onOpenChange={(open) => {
          if (!open) {
            setShowPause(null);
            setPauseMachineId('');
          }
        }}
      >
        <DialogContent className="bg-card sm:max-w-[420px]">
          <DialogHeader>
            <DialogTitle className="font-display">Pause lockdown</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            {showPause && (
              <p className="text-sm text-foreground">
                Temporarily lift web lockdown on <span className="font-semibold">{showPause.name}</span>
                {' '}({showPause.employeeId})&apos;s active device.
              </p>
            )}
            {pauseLoading ? (
              <div className="flex justify-center py-4"><Loader2 className="w-5 h-5 animate-spin text-primary" /></div>
            ) : !pauseMachineId ? (
              <p className="text-sm text-destructive">No active machine found for this employee.</p>
            ) : (
              <select
                value={pauseMinutes}
                onChange={(e) => setPauseMinutes(Number(e.target.value))}
                className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
              >
                {[30, 60, 120, 240].map((m) => (
                  <option key={m} value={m}>
                    {m < 60 ? `${m} minutes` : `${m / 60} hour${m === 60 ? '' : 's'}`}
                  </option>
                ))}
              </select>
            )}
            <div className="flex gap-2">
              <button
                onClick={() => setShowPause(null)}
                className="flex-1 border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted"
              >
                Cancel
              </button>
              <button
                disabled={!pauseMachineId || !showPause || pauseLoading || pauseSubmitting}
                onClick={async () => {
                  if (!showPause || !pauseMachineId) return;
                  setPauseSubmitting(true);
                  try {
                    const res = await employeesApi.pauseLockdown(showPause.id, pauseMachineId, pauseMinutes);
                    toast.success('Lockdown paused', {
                      description: `Until ${new Date(res.lockdownPausedUntil).toLocaleString()}`,
                    });
                    setShowPause(null);
                  } catch (e) {
                    toast.error(e instanceof Error ? e.message : 'Failed to pause lockdown');
                  } finally {
                    setPauseSubmitting(false);
                  }
                }}
                className="flex-1 bg-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 disabled:opacity-50 flex items-center justify-center gap-2"
              >
                {pauseSubmitting ? <Loader2 className="w-4 h-4 animate-spin" /> : <PauseCircle className="w-4 h-4" />}
                Pause
              </button>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      {/* Secret Dialog */}
      <EnrollFaceDialog
        employee={showEnrollFace}
        open={showEnrollFace !== null}
        onOpenChange={(open) => { if (!open) setShowEnrollFace(null); }}
      />

      <Dialog open={showSecret !== null} onOpenChange={(open) => { if (!open) { setShowSecret(null); setSecretValue(''); setCopied(false); } }}>
        <DialogContent className="bg-card sm:max-w-[460px]">
          <DialogHeader>
            <DialogTitle className="font-display">Login Secret Generated</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            {showSecret && (
              <div className="flex items-center gap-3 bg-background border border-border rounded-xl p-3.5">
                <div
                  className="w-11 h-11 rounded-full flex items-center justify-center text-sm font-bold text-primary-foreground shrink-0"
                  style={{ backgroundColor: showSecret.avatarColor || '#7C3AED' }}
                >
                  {showSecret.avatar || showSecret.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                </div>
                <div className="min-w-0 flex-1">
                  <p className="text-sm font-semibold text-foreground truncate">{showSecret.name}</p>
                  <p className="text-xs text-muted-foreground truncate">{showSecret.email}</p>
                  <p className="text-xs text-muted-foreground mt-0.5">{showSecret.department}</p>
                </div>
                <div className="text-right shrink-0">
                  <p className="text-[11px] uppercase tracking-wide text-muted-foreground font-medium">Employee ID</p>
                  <p className="text-sm font-mono font-medium text-foreground">{showSecret.employeeId}</p>
                </div>
              </div>
            )}

            <div className="flex items-start gap-2.5 bg-warning/10 border border-warning/30 rounded-xl p-3">
              <AlertTriangle className="w-4 h-4 text-warning mt-0.5 shrink-0" />
              <p className="text-xs text-foreground">
                This secret is <span className="font-semibold text-warning">valid for 5 minutes</span> and can only be
                used once. It won't be shown again after you close this dialog — copy it now.
              </p>
            </div>

            {secretMutation.isPending ? (
              <div className="flex items-center justify-center py-6">
                <Loader2 className="w-6 h-6 animate-spin text-primary" />
              </div>
            ) : secretValue ? (
              <div className="bg-background border border-border rounded-xl p-1.5 flex items-center gap-1.5">
                <code className="flex-1 text-sm font-mono font-bold text-foreground select-all px-2.5 py-2 truncate">{secretValue}</code>
                <button
                  onClick={handleCopySecret}
                  className={`px-3 py-2 rounded-lg text-xs font-semibold flex items-center gap-1.5 transition-colors shrink-0 ${
                    copied
                      ? 'bg-success/15 text-success'
                      : 'bg-primary text-primary-foreground hover:opacity-90'
                  }`}
                >
                  {copied ? (
                    <><Check className="w-3.5 h-3.5" /> Copied</>
                  ) : (
                    <><Copy className="w-3.5 h-3.5" /> Copy</>
                  )}
                </button>
              </div>
            ) : (
              <p className="text-sm text-destructive">Failed to generate secret. Try again.</p>
            )}
            <button
              onClick={() => { setShowSecret(null); setSecretValue(''); setCopied(false); }}
              className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
            >
              Close
            </button>
          </div>
        </DialogContent>
      </Dialog>
      {/* Import Result Dialog */}
      <Dialog open={importResult !== null} onOpenChange={(open) => { if (!open) setImportResult(null); }}>
        <DialogContent className="bg-card sm:max-w-[640px]">
          <DialogHeader>
            <DialogTitle className="font-display">Import Complete</DialogTitle>
          </DialogHeader>
          <div className="space-y-4 mt-2">
            <div className="flex gap-4">
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-success">{importResult?.imported ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Imported</div>
              </div>
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-foreground">{importResult?.updated ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Updated</div>
              </div>
              <div className="flex-1 bg-background border border-border rounded-lg p-4 text-center">
                <div className="text-2xl font-bold text-warning">{importResult?.skipped ?? 0}</div>
                <div className="text-xs text-muted-foreground mt-1">Skipped</div>
              </div>
            </div>
            {importResult && importResult.skipped > 0 && (
              <div className="max-h-64 overflow-y-auto border border-border rounded-lg">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b border-border text-left text-muted-foreground text-xs uppercase">
                      <th className="px-3 py-2 font-semibold">Row</th>
                      <th className="px-3 py-2 font-semibold">Employee ID</th>
                      <th className="px-3 py-2 font-semibold">Name</th>
                      <th className="px-3 py-2 font-semibold">Reason</th>
                    </tr>
                  </thead>
                  <tbody>
                    {importResult.results.filter(r => r.status === 'skipped').map(r => (
                      <tr key={r.rowIndex} className="border-b border-border last:border-0">
                        <td className="px-3 py-2 text-muted-foreground">{r.rowIndex}</td>
                        <td className="px-3 py-2 font-mono">{r.employeeId || '-'}</td>
                        <td className="px-3 py-2">{r.name || '-'}</td>
                        <td className="px-3 py-2 text-destructive">{r.reason}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <button
              onClick={() => setImportResult(null)}
              className="w-full border border-border text-foreground py-2.5 rounded-lg text-sm font-medium hover:bg-muted transition-colors"
            >
              Close
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
