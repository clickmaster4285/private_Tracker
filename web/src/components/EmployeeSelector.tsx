'use client';

import { useMemo, useState } from 'react';
import { Check, ChevronDown, Loader2, Search, User, Users } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { Popover, PopoverContent, PopoverTrigger } from '@/components/ui/popover';
import { employeesApi, type Employee } from '@/lib/api';
import { cn } from '@/lib/utils';

interface EmployeeSelectorProps {
  value: string;
  onChange: (employee: Employee | null) => void;
  placeholder?: string;
  className?: string;
  /** Show an "All employees" row that calls onChange(null). */
  allowAll?: boolean;
  allLabel?: string;
  /** Restrict the list to this department name (client-side, on the shared cache). */
  department?: string;
}

function matchesValue(emp: Employee, value: string): boolean {
  return emp.id === value || emp.employeeId === value;
}

/**
 * Searchable employee picker. Fetches the employee list once (shared via
 * React Query cache) and exposes the selected employee object (UUID id +
 * EMP-XXXXX code) so callers can scope sync tables and detail endpoints.
 *
 * The menu is portaled (Radix Popover) so it is not clipped by parent
 * overflow — e.g. dialogs with max-height + overflow-y-auto.
 */
export default function EmployeeSelector({
  value,
  onChange,
  placeholder = 'Select an employee…',
  className = '',
  allowAll = false,
  allLabel = 'All employees',
  department,
}: EmployeeSelectorProps) {
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');

  const { data, isLoading } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
    staleTime: 5 * 60_000,
  });

  const employees = useMemo(() => {
    const rows = data?.data ?? [];
    if (!department) return rows;
    const d = department.toLowerCase();
    return rows.filter(e => (e.department || '').toLowerCase() === d);
  }, [data?.data, department]);

  const selected = value ? employees.find(e => matchesValue(e, value)) ?? null : null;

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return employees;
    return employees.filter(e =>
      e.name.toLowerCase().includes(q) ||
      e.employeeId.toLowerCase().includes(q) ||
      e.email.toLowerCase().includes(q),
    );
  }, [employees, search]);

  const showAllRow = allowAll && (!search.trim() || allLabel.toLowerCase().includes(search.trim().toLowerCase()));

  const select = (emp: Employee) => {
    onChange(emp);
    setOpen(false);
    setSearch('');
  };

  const clearAll = () => {
    onChange(null);
    setOpen(false);
    setSearch('');
  };

  return (
    <Popover
      // modal: required so the portaled menu stays interactive inside Dialogs
      // (otherwise the dialog focus/pointer layer treats the menu as "outside").
      modal
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) setSearch('');
      }}
    >
      <PopoverTrigger asChild>
        <button
          type="button"
          aria-label="Employee"
          className={cn(
            'w-full max-w-sm h-9 flex items-center gap-2 bg-card border border-border rounded-lg px-3 text-sm text-foreground hover:bg-muted/40 transition-colors shrink-0',
            className,
          )}
        >
          {selected ? (
            <User className="w-4 h-4 text-muted-foreground shrink-0" />
          ) : (
            <Users className="w-4 h-4 text-muted-foreground shrink-0" />
          )}
          <span className="flex-1 text-left truncate min-w-0">
            {selected ? (
              <>
                <span className="font-medium">{selected.name}</span>
                <span className="text-muted-foreground ml-1.5 font-mono text-xs">{selected.employeeId}</span>
              </>
            ) : (
              <span className={allowAll ? 'text-foreground' : 'text-muted-foreground'}>
                {allowAll ? allLabel : placeholder}
              </span>
            )}
          </span>
          <ChevronDown
            className={`w-4 h-4 text-muted-foreground transition-transform shrink-0 ${open ? 'rotate-180' : ''}`}
          />
        </button>
      </PopoverTrigger>

      <PopoverContent
        align="start"
        sideOffset={6}
        // Above dialogs (also z-50) so the menu is not trapped under the sheet.
        className="z-[60] p-0 w-[var(--radix-popover-trigger-width)] min-w-[16rem] bg-popover border-border rounded-xl shadow-card-hover"
        onOpenAutoFocus={(e) => {
          // Keep focus on the search field instead of the first list button.
          e.preventDefault();
          const root = e.currentTarget as HTMLElement;
          root.querySelector<HTMLInputElement>('input')?.focus();
        }}
      >
        <div className="flex items-center gap-2 px-3 py-2.5 border-b border-border">
          <Search className="w-4 h-4 text-muted-foreground shrink-0" />
          <input
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search by name, ID or email…"
            className="bg-transparent border-none outline-none text-sm flex-1 min-w-0 text-foreground placeholder:text-muted-foreground"
          />
        </div>
        <div className="max-h-72 overflow-y-auto overscroll-contain p-1.5">
          {isLoading ? (
            <div className="flex items-center justify-center gap-2 py-6 text-sm text-muted-foreground">
              <Loader2 className="w-4 h-4 animate-spin" /> Loading employees…
            </div>
          ) : filtered.length === 0 && !showAllRow ? (
            <p className="text-center py-6 text-sm text-muted-foreground">No employees found</p>
          ) : (
            <>
              {showAllRow && (
                <button
                  type="button"
                  onClick={clearAll}
                  className={`w-full flex items-center gap-3 rounded-lg px-2.5 py-2 text-left text-sm hover:bg-muted/40 transition-colors ${!selected ? 'bg-sidebar-accent/40' : ''}`}
                >
                  <div className="w-7 h-7 rounded-full flex items-center justify-center bg-muted shrink-0">
                    <Users className="w-3.5 h-3.5 text-muted-foreground" />
                  </div>
                  <span className="flex-1 min-w-0 font-medium text-foreground">{allLabel}</span>
                  {!selected && <Check className="w-4 h-4 text-primary shrink-0" />}
                </button>
              )}
              {filtered.map(emp => {
                const active = matchesValue(emp, value);
                return (
                  <button
                    key={emp.id}
                    type="button"
                    onClick={() => select(emp)}
                    className={`w-full flex items-center gap-3 rounded-lg px-2.5 py-2 text-left text-sm hover:bg-muted/40 transition-colors ${active ? 'bg-sidebar-accent/40' : ''}`}
                  >
                    <div
                      className="w-7 h-7 rounded-full flex items-center justify-center text-[10px] font-bold text-primary-foreground shrink-0"
                      style={{ backgroundColor: emp.avatarColor || '#7C3AED' }}
                    >
                      {emp.avatar || emp.name.split(' ').map(n => n[0]).join('').toUpperCase().slice(0, 2)}
                    </div>
                    <span className="flex-1 min-w-0">
                      <span className="block font-medium text-foreground truncate">{emp.name}</span>
                      <span className="block text-xs text-muted-foreground truncate">
                        {emp.employeeId} · {emp.department || '—'}
                      </span>
                    </span>
                    {active && <Check className="w-4 h-4 text-primary shrink-0" />}
                  </button>
                );
              })}
            </>
          )}
        </div>
      </PopoverContent>
    </Popover>
  );
}
