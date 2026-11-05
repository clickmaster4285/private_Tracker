'use client';

import { useEffect } from 'react';
import { Loader2, UserX, Laptop } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import EmployeeSelector from '@/components/EmployeeSelector';
import { useEmployeeDetail } from '@/hooks/use-employee-detail';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';
import { formatRelative } from '@/lib/format';
import { Select, SelectContent, SelectItem, SelectTrigger } from '@/components/ui/select';
import { employeesApi, type Employee, type EmployeeDevice, type EmployeeDetail } from '@/lib/api';
import TrackingModeBadge, { deviceEffectiveMode } from '@/components/employees/TrackingModeBadge';

/**
 * Device label used everywhere in Device Specs: "{employee name} ({hostname})".
 * A device whose login never sent a name still has the hostname the machine
 * reported with its hardware snapshot, so it is never rendered as "Unknown device".
 */
export function deviceLabel(device: EmployeeDevice, employee: Employee): string {
  const host = (device.hostname || device.deviceName || device.machineId || '').trim();
  return host ? `${employee.name} (${host})` : employee.name;
}

/**
 * Machine lifecycle badge for the Device Specs dropdown (R8, 2026-10-19):
 * "active" machines are currently tracked; "old" machines were removed (the admin
 * revoked the device and the client wiped itself) but keep their history.
 */
export function deviceStatusLabel(device: EmployeeDevice): string {
  return device.status === 'old' ? 'old · removed' : 'active';
}

export interface EmployeePageContext {
  /** Full employee record (UUID id + EMP-XXXXX code). */
  employee: Employee;
  /** Aggregate machine picture — only present when fetchDetail is enabled. */
  detail?: EmployeeDetail;
  detailLoading: boolean;
  /**
   * URL setter for the `?employeeId=` key. Bodies normally just read
   * `employee.employeeId` and leave the picker as the sole writer, but
   * this is exposed for bodies that need to round-trip the employee
   * (e.g. a "deep link to a different employee" button).
   */
  setEmployeeId?: (employeeId: string) => void;
  /**
   * URL-synced activity filter (search + date preset + dateFrom/dateTo).
   * The shell owns the underlying `useUrlActivityFilter` and SHARES its
   * state with the body via this render-prop — there is exactly one
   * `useUrlQueryState` instance per page, so writes from the picker
   * (changing `employeeId`) and writes from the body's filter chips
   * (changing `q/preset/from/to`) never race, never erase each other,
   * never produce a "previous filter got reset" surprise. Bodies that
   * also have their own URL state should merge their keys into the
   * same extra schema (see the shell call site for the canonical
   * `{employeeId}` extra).
   */
  filter: import('@/components/journey/ActivityFilters').ActivityFilter;
  setFilter: (next: import('@/components/journey/ActivityFilters').ActivityFilter) => void;
  /**
   * URL-synced body keys (anything the body wants to keep on the URL —
   * e.g. `tab` + `appSearch` + `pkgSearch` for the Installed Software
   * page). The shell forwards its `useUrlActivityFilter`'s `extraSchema`
   * / `extraInitial` into the same underlying `useUrlQueryState` so
   * body writes never erase the picker. Bodies that need to read/write
   * their own URL keys should destructure `body` and use it instead of
   * calling `useUrlQueryState` themselves.
   */
  body: Record<string, string>;
  setBody: (patch: Record<string, string> | ((prev: Record<string, string>) => Record<string, string>)) => void;
  /** Selected machine id from the device dropdown ('' = all devices / aggregate). */
  deviceId: string;
  /** Every registered device for this employee, newest activity first (may be empty). */
  devices: EmployeeDevice[];
}

interface EmployeePageProps {
  title: string;
  subtitle: string;
  icon: React.ElementType;
  /** Fetch the aggregate GET /employees/:id/detail payload for the selected employee. */
  fetchDetail?: boolean;
  /**
   * Optional body-specific URL keys (e.g. `{tab, appSearch, pkgSearch}`
   * for the Installed Software page). Forwarded into the shell's
   * `useUrlActivityFilter` so the body shares the single underlying
   * `useUrlQueryState` with the picker — no second hook instance, no
   * race, no "previous filter got reset" surprise.
   */
  bodySchema?: Record<string, { parse?: (raw: string) => string }>;
  bodyInitial?: Record<string, string>;
  /**
   * Render-prop that builds the page body once an employee is selected.
   * Receives the resolved employee + the shell's filter state + the
   * body's URL state + an optional `setEmployeeId` callback for bodies
   * that want to share this shell's URL state.
   */
  children: (ctx: EmployeePageContext) => React.ReactNode;
}

/**
 * Shared page shell for the Device Specs and Employee Journey modules:
 * page header, employee picker (deep-linkable via ?employeeId=), and
 * loading/error/no-selection states. The body is rendered by the caller
 * once an employee (and optionally the detail payload) is available.
 *
 * URL state
 * ---------
 * The shell owns the SINGLE `useUrlActivityFilter` instance for the
 * page, exposing BOTH the activity filter (search + date) and the
 * `?employeeId=<uuid>` picker key through the same hook — one
 * underlying `useUrlQueryState`, one `lastSerialized` ref, one
 * `latestWrittenSearch` chain slot. There is no second
 * `useUrlQueryState` instance to race against, so picking an employee
 * and then clicking a date preset (or vice versa) never resets the
 * sibling key.
 *
 * Any sibling page (the employees table action menu, an external link)
 * can deep-link to a specific journey or device-specs subpage by adding
 * `?employeeId=<uuid>`; a manual address-bar edit propagates back into
 * the picker on the next render. See the Web URL-State Rule in
 * AGENTS.md §6.
 */
export default function EmployeePage({
  title,
  subtitle,
  icon: Icon,
  fetchDetail = false,
  bodySchema,
  bodyInitial,
  children,
}: EmployeePageProps) {
  // The single URL hook for the whole page — owns `employeeId` plus the
  // four activity-filter keys (`q/preset/from/to`) plus any body-supplied
  // keys (e.g. `tab` + `appSearch` + `pkgSearch` for the Installed
  // Software page). Bodies receive `filter`/`setFilter` AND `body`/`setBody`
  // from the render-prop and use them directly (instead of calling
  // `useUrlActivityFilter` / `useUrlQueryState` themselves). This is the
  // structural fix for the "pick employee then change filter wipes the
  // picker" bug — there is now exactly one `useUrlQueryState` on the
  // page and no race window between two instances.
  const { filter, setFilter, extra: urlExtra, setExtra } = useUrlActivityFilter(
    { employeeId: {}, deviceId: {}, ...(bodySchema ?? {}) },
    { employeeId: '', deviceId: '', ...(bodyInitial ?? {}) },
  );
  const employeeId = urlExtra.employeeId;
  const deviceId = urlExtra.deviceId;
  // Body's own URL state — everything in `extra` except `employeeId` (the
  // shell's picker key). The body destructures this and calls `setBody`
  // instead of maintaining its own `useUrlQueryState`.
  const body: Record<string, string> = {};
  for (const key of Object.keys(bodySchema ?? {})) {
    body[key] = (urlExtra as unknown as Record<string, string>)[key] ?? '';
  }

  // Same query key as EmployeeSelector — one shared cache entry.
  const { data: employeesData } = useQuery({
    queryKey: ['employees', 'selector'],
    queryFn: () => employeesApi.list({ page: 1, perPage: 100 }),
  });

  const employee = employeesData?.data.find(e => e.id === employeeId) ?? null;

  const detailQuery = useEmployeeDetail(fetchDetail ? employeeId : '', deviceId);

  const handleChange = (emp: Employee | null) => {
    // A different employee means different devices — drop the stale selection.
    setExtra({ employeeId: emp?.id ?? '', deviceId: '' });
  };

  const devices = detailQuery.data?.devices ?? [];
  // `keepPreviousData` (use-employee-detail) holds the previous payload across
  // query re-keys (deviceId is part of the key), so `data` can briefly belong
  // to a DIFFERENT employee or a different machine scope. Only a settled,
  // non-placeholder payload for the CURRENT key is authoritative enough to
  // drive the URL mirror or the render branches below.
  const detailSettled = detailQuery.isSuccess && !detailQuery.isPlaceholderData;

  // "All devices" was removed from the dropdown (2026-10-19, user request): whenever
  // the employee has more than one machine the shell ALWAYS scopes to exactly one —
  // the machine in the URL while it is still registered, otherwise the first entry
  // (the server orders machines active-first, then most-recently-tracked, so that
  // default is the live machine). Single-machine employees keep the employee-wide
  // view they already got (no dropdown was ever rendered for them), and a stale
  // `?deviceId=` for a machine that no longer exists falls back the same way.
  const urlDeviceValid = deviceId !== '' && devices.some(d => d.machineId === deviceId);
  const effectiveDeviceId = urlDeviceValid
    ? deviceId
    : (devices.length > 1 ? (devices[0]?.machineId ?? '') : '');

  // The URL stays the single source of truth (URL-Synced Filters Rule): mirror the
  // resolved scope back into `?deviceId=` so back/forward, a shared link and the
  // rendered scope always agree. Empty is stripped from the URL by the hook.
  //
  // Gated on `detailSettled`: while the re-keyed query is in flight the kept
  // placeholder payload resolves `effectiveDeviceId` from stale data, and
  // writing that back would ping-pong `?deviceId=` forever ("Maximum update
  // depth exceeded"). Only the settled payload for the current key writes.
  useEffect(() => {
    if (employee && detailSettled && effectiveDeviceId !== deviceId) {
      setExtra({ deviceId: effectiveDeviceId });
    }
  }, [employee, detailSettled, deviceId, effectiveDeviceId, setExtra]);

  const handleDeviceChange = (value: string) => {
    setExtra({ deviceId: value });
  };

  const selectedHeaderDevice = devices.find((d) => d.machineId === effectiveDeviceId);

  return (
    <div className="space-y-5 animate-fade-in">
      {/* Header */}
      <div className="flex flex-col lg:flex-row lg:items-end gap-4 justify-between">
        <div className="flex items-start gap-3">
          <div className="w-11 h-11 rounded-xl gradient-primary flex items-center justify-center flex-shrink-0">
            <Icon className="w-5 h-5 text-primary-foreground" />
          </div>
          <div className="min-w-0">
            <h1 className="text-xl font-bold text-foreground font-display">{title}</h1>
            <p className="text-sm text-muted-foreground mt-0.5">{subtitle}</p>
          </div>
        </div>
        {/* Controls — wrap onto their own line instead of overflowing the
            content edge. The picker is capped so the device dropdown always
            keeps room (Root cause of the "device dropdown clipped by the
            screen width" bug: the picker was `shrink-0` up to 384px next to a
            fixed 280px trigger, so the pair outran the content area). */}
        <div className="flex flex-col sm:flex-row sm:flex-wrap sm:items-center gap-2 w-full min-w-0 lg:w-auto">
          <EmployeeSelector
            value={employeeId}
            onChange={handleChange}
            className="sm:w-[240px] sm:max-w-full"
          />
          {/* Device dropdown — when the employee has more than one registered
              machine (R8). The value is the machine id (stable across re-logins);
              each entry carries the machine lifecycle status from employee_machines. */}
          {employee && detailSettled && devices.length > 1 && (
            <Select value={effectiveDeviceId} onValueChange={handleDeviceChange}>
              {/* Trigger shows only the device name — status/mode badges belong in
                  the menu (and the page body). Mirroring rich SelectItem content
                  made the h-9 pill wrap into a cramped multi-line control. */}
              <SelectTrigger className="h-9 w-full sm:w-[280px] bg-card border border-border gap-2">
                <Laptop className="w-4 h-4 text-muted-foreground shrink-0" />
                <span className="truncate flex-1 text-left">
                  {selectedHeaderDevice
                    ? deviceLabel(selectedHeaderDevice, employee)
                    : 'Select device'}
                </span>
              </SelectTrigger>
              <SelectContent className="min-w-[var(--radix-select-trigger-width)] sm:min-w-[320px]">
                {devices.map(device => (
                  <SelectItem key={device.machineId} value={device.machineId} textValue={deviceLabel(device, employee)}>
                    <span className="flex items-center gap-2 min-w-0">
                      <span className="truncate">{deviceLabel(device, employee)}</span>
                      <span
                        className={
                          device.status === 'old'
                            ? 'shrink-0 text-muted-foreground'
                            : 'shrink-0 inline-flex items-center gap-1 text-muted-foreground'
                        }
                      >
                        {device.status !== 'old' && (
                          <span className="inline-block w-1.5 h-1.5 rounded-full bg-emerald-500" aria-hidden />
                        )}
                        {deviceStatusLabel(device)}
                      </span>
                      <TrackingModeBadge mode={deviceEffectiveMode(device)} className="shrink-0" />
                      {device.status === 'old' && device.lastTrackAt ? (
                        <span className="shrink-0 text-muted-foreground text-xs">
                          · last tracked {formatRelative(device.lastTrackAt)}
                        </span>
                      ) : null}
                    </span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        </div>
      </div>

      {/* Body */}
      {!employee ? (
        <div className="bg-card rounded-xl border border-border py-16 flex flex-col items-center gap-3 text-center px-6">
          <div className="w-12 h-12 rounded-xl bg-muted flex items-center justify-center">
            <UserX className="w-6 h-6 text-muted-foreground" />
          </div>
          <p className="text-sm text-muted-foreground">
            Select an employee to view their {fetchDetail ? 'device information' : 'activity'}.
          </p>
        </div>
      ) : fetchDetail && !detailSettled && !detailQuery.error ? (
        <div className="flex items-center justify-center min-h-[320px]">
          <div className="flex flex-col items-center gap-3">
            <Loader2 className="w-8 h-8 animate-spin text-primary" />
            <p className="text-sm text-muted-foreground">Loading device information…</p>
          </div>
        </div>
      ) : fetchDetail && (detailQuery.error || !detailQuery.data) ? (
        <div className="bg-card rounded-xl border border-border py-16 flex flex-col items-center gap-3 text-center px-6">
          <p className="text-sm text-destructive font-medium">Failed to load employee details</p>
          <p className="text-xs text-muted-foreground">
            {detailQuery.error ? (detailQuery.error as Error).message : 'No data found for this employee'}
          </p>
        </div>
      ) : (
        children({
          employee,
          detail: detailQuery.data,
          // `keepPreviousData` keeps `isLoading` false across query re-keys,
          // so derive "loading" from the settled-payload check instead.
          detailLoading: fetchDetail && !detailSettled && !detailQuery.error,
          setEmployeeId: (id: string) => setExtra({ employeeId: id }),
          filter,
          setFilter,
          deviceId: effectiveDeviceId,
          devices,
          body,
          // `setBody` only writes the body keys (never the picker's
          // `employeeId` or the activity-filter `q/preset/from/to`), so
          // it can never erase the sibling URL state.
          setBody: (patch) => {
            const next = typeof patch === 'function' ? patch(body) : { ...body, ...patch };
            setExtra(next as Partial<Record<string, string>>);
          },
        })
      )}
    </div>
  );
}