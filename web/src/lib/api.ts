// API client for communicating with the Go backend server.
// Uses httpOnly cookies for JWT authentication (set by server).

// Use relative URL through Next.js rewrites proxy. Set NEXT_PUBLIC_API_URL to override.
const API_BASE = process.env.NEXT_PUBLIC_API_URL || '/api/v1';

interface RequestOptions {
  method?: string;
  body?: unknown;
  params?: Record<string, string | number | boolean | undefined>;
  headers?: Record<string, string>;
  /** Skip JSON parse for blob/download responses */
  raw?: boolean;
  /** Internal: this call already went through one refresh-retry cycle */
  _retried?: boolean;
}

class ApiError extends Error {
  status: number;
  detail?: string;

  constructor(message: string, status: number, detail?: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
  }
}

// ──────────────────────────
// Access-token refresh (single-flight).
// The auth_token cookie lives ~15 minutes; POST /auth/refresh validates and
// rotates the 30-day refresh_token cookie and re-mints both. Concurrent 401s
// share one refresh; when it fails the session is dead → force back to /login.
// ──────────────────────────
let refreshInFlight: Promise<boolean> | null = null;

async function performRefresh(): Promise<boolean> {
  try {
    const res = await fetch(`${API_BASE}/auth/refresh`, {
      method: 'POST',
      credentials: 'include',
    });
    return res.ok;
  } catch {
    return false;
  }
}

function forceLoginRedirect() {
  if (typeof window !== 'undefined' && !window.location.pathname.startsWith('/login')) {
    window.location.replace('/login');
  }
}

async function request<T>(endpoint: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, params, headers = {}, raw = false } = options;

  // Build URL with query params
  let url = `${API_BASE}${endpoint}`;
  if (params) {
    const searchParams = new URLSearchParams();
    Object.entries(params).forEach(([key, value]) => {
      if (value !== undefined && value !== '' && value !== null) {
        searchParams.set(key, String(value));
      }
    });
    const qs = searchParams.toString();
    if (qs) url += `?${qs}`;
  }

  // Build fetch options
  const fetchOptions: RequestInit = {
    method,
    credentials: 'include', // Send cookies (httpOnly JWT cookie)
    headers: {
      'Content-Type': 'application/json',
      ...headers,
    },
  };

  if (body && method !== 'GET') {
    fetchOptions.body = JSON.stringify(body);
  }

  let response = await fetch(url, fetchOptions);

  // Access token expired → try one silent refresh, then replay the request once
  if (
    response.status === 401 &&
    !options._retried &&
    endpoint !== '/auth/login' &&
    endpoint !== '/auth/refresh'
  ) {
    if (!refreshInFlight) {
      refreshInFlight = performRefresh().finally(() => {
        refreshInFlight = null;
      });
    }
    const refreshed = await refreshInFlight;
    if (refreshed) {
      return request<T>(endpoint, { ...options, _retried: true });
    }
    forceLoginRedirect();
  }

  if (!response.ok) {
    let errorData: { message?: string; detail?: string } = {};
    try {
      errorData = await response.json();
    } catch {
      // ignore parse error
    }
    throw new ApiError(
      errorData.message || `Request failed with status ${response.status}`,
      response.status,
      errorData.detail,
    );
  }

  if (raw) return response as unknown as T;

  // 204 / empty body (e.g. DELETE allow-list rows)
  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }
  const text = await response.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

// ──────────────────────────
// Auth API
// ──────────────────────────

export interface AuthUser {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  role: string;
  roleId?: number;
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  /** Granted submodule keys for the user's role (server-driven RBAC). */
  permissions?: string[];
  createdAt: string;
  updatedAt: string;
}

export interface LoginResponse {
  user: AuthUser;
}

// ── Self-service profile (GET /api/v1/auth/profile) ────────────────────────

/** One navigation module surfaced on the profile page, with the count of
 *  granted submodules under it. No hardcoded module names — derived from
 *  the RBAC catalog joined with the user's granted permission keys. */
export interface ProfileModule {
  id: number;
  key: string;
  name: string;
  grantedCount: number;
  submoduleCount: number;
}

/** RBAC view attached to the profile. */
export interface ProfilePermissions {
  submoduleKeys: string[];
  modules: ProfileModule[];
  /** True when the user holds the system `company_admin` role. Drives the
   *  lock messaging in the profile UI. */
  isSystemAdmin: boolean;
}

/** Aggregate profile payload returned by GET /api/v1/auth/profile. The
 *  /settings/profile page renders User, Role, Permissions and Employee
 *  directly from this shape. */
export interface ProfileResponse {
  user: AuthUser;
  role?: {
    id: number;
    name: string;
    description: string;
    isSystem: boolean;
    userCount: number;
    submoduleIds: number[];
    permissions: string[];
  };
  permissions: ProfilePermissions;
  employee?: Employee;
}

export interface AuthCheckResponse {
  authenticated: boolean;
  user?: AuthUser;
}

export const authApi = {
  login: (email: string, password: string) =>
    request<LoginResponse>('/auth/login', {
      method: 'POST',
      body: { email, password },
    }),

  logout: () =>
    request<{ message: string }>('/auth/logout', {
      method: 'POST',
    }),

  me: () => request<AuthUser>('/auth/me'),

  /** Aggregate self-service profile: user + role + RBAC view + linked
   *  employee. Identity is resolved from the httpOnly cookie on the
   *  server. */
  profile: () => request<ProfileResponse>('/auth/profile'),

  check: () => request<AuthCheckResponse>('/auth/check'),

  /** Rotate the refresh_token cookie into a fresh access_token cookie.
   * Resolves true when the session was revived; false when it is unrecoverable. */
  refresh: (): Promise<boolean> => performRefresh(),
};

// ──────────────────────────
// Departments API (dynamic CRUD)
// ──────────────────────────

export interface Department {
  id: number;
  name: string;
  employeeCount: number;
}

export interface DepartmentListResponse {
  departments: Department[];
  total: number;
}

export const departmentsApi = {
  list: () => request<DepartmentListResponse>('/departments'),

  create: (name: string) =>
    request<Department>('/departments', { method: 'POST', body: { name } }),

  update: (id: number, name: string) =>
    request<Department>('/departments/' + id, { method: 'PUT', body: { name } }),

  delete: (id: number) =>
    request<{ message: string }>('/departments/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Employees API
// ──────────────────────────

export interface Employee {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  department: string;
  departmentId: number;
  /** FK to the shifts catalog (null when the employee has no shift). */
  shiftId: number | null;
  /** Resolved shift name (joined from shifts). Empty when shiftId is null. */
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  isOnline: boolean;
  clientVersion?: string;
  avatar: string;
  avatarColor: string;
  /** True when a row in the users table exists for this employee's employee_id.
   *  Projected server-side via an indexed EXISTS() so it costs O(1) per row. */
  hasUserLogin: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface EmployeeListResponse {
  data: Employee[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface CreateEmployeePayload {
  name: string;
  email: string;
  departmentId: number;
  /** FK to the shifts catalog. null/undefined → "no shift". */
  shiftId?: number | null;
}

export interface UpdateEmployeePayload {
  name?: string;
  email?: string;
  departmentId?: number;
  /** FK to the shifts catalog. 0 / null / undefined → no change; pass `0`
   *  explicitly to clear an existing assignment (the service maps it to
   *  NULL on the DB). */
  shiftId?: number | null;
  trackingEnabled?: boolean;
  trackingStatus?: string;
  isOnline?: boolean;
}

export interface GenerateSecretResponse {
  secret: string;
  expiresIn: number;
}

export interface ImportEmployeeRow {
  employeeId: string;
  name: string;
  email: string;
  department: string;
  /** Spreadsheet column header. The server resolves the name to a
   *  shifts.id at import time; a blank cell falls back to "Day Shift". */
  shift?: string;
}

export interface ImportRowResult {
  rowIndex: number;
  employeeId: string;
  name: string;
  status: 'imported' | 'updated' | 'skipped';
  reason?: string;
}

export interface ImportEmployeesResponse {
  imported: number;
  updated: number;
  skipped: number;
  results: ImportRowResult[];
}

export interface EmployeeExportRow {
  employeeId: string;
  name: string;
  email: string;
  department: string;
  shift: string;
}

export const employeesApi = {
  list: (params?: { page?: number; perPage?: number; search?: string; department?: string; role?: string; status?: string }) =>
    request<EmployeeListResponse>('/employees', { params: params as Record<string, string | number | undefined> }),

  get: (id: string) => request<Employee>('/employees/' + id),

  // Aggregate machine picture: latest hardware, storage, network, installed apps/packages,
  // peripherals, permission checks, app status and activity stats.
  // `deviceId` is the device's machine id — it scopes every device-level surface to that
  // machine; omitted means the employee-wide aggregate ("all machines").
  detail: (id: string, deviceId?: string) =>
    request<EmployeeDetail>('/employees/' + id + '/detail', {
      params: deviceId ? { deviceId } : undefined,
    }),

  // "Remove current device": revoke every active device, close open sessions/items and
  // flip the employee to untracked. History is kept; the next login starts tracking again.
  removeDevice: (id: string) =>
    request<RemoveEmployeeDeviceResponse>('/employees/' + id + '/devices/remove', { method: 'POST' }),

  // Temporarily pause DLP lockdown on one machine (default server-side duration when omitted).
  pauseLockdown: (employeeUuid: string, machineId: string, minutes?: number) =>
    request<PauseLockdownResponse>(
      '/employees/' + employeeUuid + '/machines/' + encodeURIComponent(machineId) + '/lockdown-pause',
      { method: 'POST', body: minutes != null ? { minutes } : {} },
    ),

  create: (data: CreateEmployeePayload) =>
    request<Employee>('/employees', { method: 'POST', body: data }),

  update: (id: string, data: UpdateEmployeePayload) =>
    request<Employee>('/employees/' + id, { method: 'PUT', body: data }),

  delete: (id: string) =>
    request<{ message: string }>('/employees/' + id, { method: 'DELETE' }),

  generateSecret: (id: string) =>
    request<GenerateSecretResponse>('/employees/' + id + '/generate-secret', { method: 'POST' }),

  // Bulk Excel import — the server get-or-creates departments by name and upserts
  // each row by its exact employee_id from the spreadsheet.
  import: (rows: ImportEmployeeRow[]) =>
    request<ImportEmployeesResponse>('/employees/import', { method: 'POST', body: { employees: rows } }),

  // All non-deleted employees as flat rows for the Excel download.
  export: () => request<EmployeeExportRow[]>('/employees/export'),
};

// ──────────────────────────
// Shifts API (relational CRUD)
// ──────────────────────────

export interface Shift {
  id: number;
  name: string;
  /** "HH:MM" 24-hour time. */
  startTime: string;
  endTime: string;
  /** Comma-separated weekday short names (e.g. "Mon,Tue,Wed,Thu,Fri"). */
  workingDays: string;
  /** IANA timezone used to interpret shift hours (e.g. "Asia/Karachi"). */
  timezone: string;
  graceMinutes: number;
  overtimeHours: number;
  description: string;
  employeeCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface ShiftListResponse {
  data: Shift[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface ShiftAllResponse {
  shifts: Shift[];
  total: number;
}

export interface CreateShiftPayload {
  name: string;
  startTime: string;
  endTime: string;
  workingDays: string;
  timezone: string;
  graceMinutes: number;
  overtimeHours: number;
  description?: string;
}

export const shiftsApi = {
  /** Paginated, searchable list (admin /shifts page). */
  list: (params?: { page?: number; perPage?: number; search?: string }) =>
    request<ShiftListResponse>('/shifts', { params: params as Record<string, string | number | undefined> }),

  /** Unpaged list of every non-deleted shift — used by dropdowns. */
  listAll: () => request<ShiftAllResponse>('/shifts/all'),

  create: (data: CreateShiftPayload) =>
    request<Shift>('/shifts', { method: 'POST', body: data }),

  update: (id: number, data: CreateShiftPayload) =>
    request<Shift>('/shifts/' + id, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>('/shifts/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Employee Detail API (machine picture for the user detail page)
// ──────────────────────────

export interface DeviceHardwareDetail {
  id: string;
  macAddress: string;
  hostname: string;
  osName: string;
  osVersion: string;
  cpuModel: string;
  cpuCores: number;
  ramTotalMb: number;
  storageDevices: string;
  gpuModel: string;
  gpuVramMb: number;
  collectedAt: string;
  syncedAt?: string;
}

/** One of an employee's registered desktop devices (employee_machines).
 *  Used by the device-specs page dropdown when an employee has multiple machines.
 *
 *  `status` / `firstTrackAt` / `lastTrackAt` come from the canonical
 *  `employee_machines` lifecycle registry (server migration 049): one row per
 *  physical machine, deduplicated even though the auth-token registry
 *  (`employee_devices`) may hold several revoked rows for the same machine.
 *  `isActiveDevice` mirrors `status === 'active'` (the lifecycle row is
 *  authoritative for "is this machine current"). */
export interface EmployeeDevice {
  id: string;
  employeeId: string;
  machineId: string;
  platform: string;
  clientVersion: string;
  deviceName: string;
  /** Most recent hostname the machine itself reported (may be empty for legacy rows). */
  hostname: string;
  /** ISO timestamp — when the device last checked in. */
  lastSeenAt: string;
  /** Whether this device registration has been revoked by an admin. */
  revokedAt: string | null;
  /** True while the machine's lifecycle status is 'active'. */
  isActiveDevice: boolean;
  /** When the device was revoked (null when still active). */
  removeAt: string | null;
  /** Machine lifecycle status from `employee_machines`: 'active' | 'old'. */
  status: 'active' | 'old';
  /** ISO timestamp — when this machine was first tracked. */
  firstTrackAt: string;
  /** ISO timestamp — when this machine last reported (advances on every sync). */
  lastTrackAt: string;
  /** Set when an admin removed this device and the client still owes a local wipe. */
  wipeRequestedAt?: string | null;
}

/** Result of POST /employees/:id/machines/:machineId/lockdown-pause. */
export interface PauseLockdownResponse {
  machineId: string;
  lockdownPausedUntil: string;
}

/** Result of POST /employees/:id/devices/remove ("Remove current device"). */
export interface RemoveEmployeeDeviceResponse {
  revokedDevices: number;
  closedSessions: number;
  closedItems: number;
  trackingStatus: string;
}

export interface StorageDeviceDetail {
  id: string;
  deviceType: string;
  model: string;
  capacityMb: number;
  createdAt: string;
}

export interface NetworkInfoDetail {
  id: string;
  employeeId: string;
  publicIp: string;
  privateIp: string;
  macAddress: string;
  networkInterfaceName: string;
  collectedAt: string;
  syncedAt?: string;
}

export interface InstalledApplicationDetail {
  id: string;
  appName: string;
  binaryName?: string;
  version: string;
  publisher: string;
  installPath: string;
  installDate?: string;
  isBrowser: boolean;
  categories?: string;
  desktopId?: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface InstalledPackageDetail {
  id: string;
  packageName: string;
  version: string;
  category: string;
  sourceManager: string;
  installPath: string;
  publisher: string;
  description: string;
  firstSeenAt: string;
  lastSeenAt: string;
}

export interface HardwareDeviceDetail {
  id: string;
  deviceClass: string;
  vendor: string;
  product: string;
  serial: string;
  busPath?: string;
  pluggedAt: string;
  unpluggedAt?: string;
}

export interface PermissionStatusDetail {
  checkId: string;
  sessionId: string;
  sessionType: string;
  platform: string;
  checkedAt: string;
  method: string;
  works: boolean;
  details: string;
}

export interface EmployeeActivityStats {
  totalSessions: number;
  openSessions: number;
  totalItems: number;
  lastActivityAt?: string;
}

export interface EmployeeDetail {
  employee: Employee;
  deviceHardware?: DeviceHardwareDetail;
  storageDevices: StorageDeviceDetail[];
  networkInfo?: NetworkInfoDetail;
  applications: InstalledApplicationDetail[];
  packages: InstalledPackageDetail[];
  hardwareDevices: HardwareDeviceDetail[];
  appStatus: Record<string, string>;
  permissions: PermissionStatusDetail[];
  stats: EmployeeActivityStats;
  /** All registered devices for this employee (employee_devices).
   *  Used by the device-specs page to offer a dropdown when multiple exist. */
  devices: EmployeeDevice[];
}

// ──────────────────────────
// Employee Auth API (for desktop client)
// ──────────────────────────

export interface EmployeeLoginRequest {
  employeeId: string;
  secretKey: string;
}

export interface EmployeeLoginResponse {
  employee: Employee;
  token: string;
}

export const employeeAuthApi = {
  login: (data: EmployeeLoginRequest) =>
    request<EmployeeLoginResponse>('/auth/employee-login', { method: 'POST', body: data }),
};

// ──────────────────────────
// App Sessions API (replaces Activity Logs)
// ──────────────────────────

export interface AppSession {
  id: string;
  employeeId: string;
  processName: string;
  appDisplayName: string;
  startedAt: string;
  endedAt?: string;
  machineId: string;
  sessionId: string;
  platform: string;
  processId?: number;
  parentProcessId?: number;
  groupedBy?: string;
  contextLabel?: string;
  foregroundSeconds?: number;
  backgroundSeconds?: number;
  syncedAt?: string;
  // 4-state lifecycle (2026-09-02 + OFFLINE 2026-09-02). The server
  // sweeper (server/internal/jobs/session_lifecycle_sweep.go) transitions
  // ACTIVE → OFFLINE → STALE → CLOSED per machine_id based on whether any
  // row for that machine has a recent lastSyncAt. Only CLOSED is terminal
  // — a live client re-uploading with no endedAt promotes OFFLINE/STALE
  // back to ACTIVE; a CLOSED row stays CLOSED. Status defaults to ACTIVE
  // when the field is absent (pre-031 rows).
  status?: 'ACTIVE' | 'OFFLINE' | 'STALE' | 'CLOSED' | string;
  lastActivityAt?: string;
  lastSyncAt?: string;
}

export interface AppSessionListResponse {
  data: AppSession[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface AppUsageRow {
  appDisplayName: string;
  processName: string;
  sessionCount: number;
  firstOpenedAt: string;
  lastClosedAt: string;
  totalDurationSeconds: number;
  hasOpenSession: boolean;
  lastActiveAt: string;
}

export interface AppUsageListResponse {
  data: AppUsageRow[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
  totalDurationSeconds: number;
  totalSessionCount: number;
  openSessionCount: number;
}

export const appSessionsApi = {
  list: (params?: {
    page?: number; perPage?: number; employeeId?: string; search?: string; platform?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<AppSessionListResponse>('/app-sessions', { params: params as Record<string, string | number | undefined> }),
  /**
   * Per-app aggregate for the "App Usage" page. The page renders
   * the server-calculated `totalDurationSeconds` for the Duration cell.
   * It is the sum of each effective session duration, so inactive gaps
   * between separate sessions are not counted.
   */
  usage: (params?: {
    page?: number; perPage?: number; employeeId?: string; search?: string; platform?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<AppUsageListResponse>('/app-sessions/usage', { params: params as Record<string, string | number | undefined> }),
  /**
   * Per-app session list — fired when the user expands a row on
   * /employee-journey/apps to see every individual session. The
   * (appDisplayName, processName) pair is the same GROUP BY key the
   * aggregate uses, so the result is consistent with the parent row's
   * sessionCount. Paginated server-side (page/perPage) so a heavy
   * user (e.g. 200 chrome opens/week) doesn't ship every row up
   * front. The page re-keys the call when the date filter changes
   * (dateFrom/dateTo are part of the React Query key).
   */
  usageSessions: (params: {
    appDisplayName: string;
    processName: string;
    page?: number;
    perPage?: number;
    employeeId?: string;
    dateFrom?: string;
    dateTo?: string;
  }) =>
    request<AppSessionListResponse>('/app-sessions/usage/sessions', {
      params: params as Record<string, string | number | undefined>,
    }),
};

export interface AppItem {
  id: string;
  employeeId: string;
  appSessionId: string;
  parentItemId?: string;
  itemType: string;
  title: string;
  identifier: string;
  url?: string;
  domain?: string;
  openedAt: string;
  closedAt?: string;
  processId?: number;
  objectType?: string;
  action?: string;
  journeyId?: string;
  sequence?: number;
  previousPath?: string;
  currentPath?: string;
  windowId?: number;
  tabId?: number;
  metadataJson?: string;
  browserName?: string;
  syncedAt?: string;
}

export interface AppItemListResponse {
  data: AppItem[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface WebUsageRow {
  domain: string;
  visitCount: number;
  totalDurationSeconds: number;
  lastVisitedAt: string;
  firstVisitedAt: string;
}

export interface WebUsageListResponse {
  data: WebUsageRow[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
  totalDurationSeconds: number;
  totalVisitCount: number;
}

export const appItemsApi = {
  list: (params?: {
    page?: number; perPage?: number; employeeId?: string; appSessionId?: string; itemType?: string;
    domain?: string; search?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<AppItemListResponse>('/app-items', { params: params as Record<string, string | number | undefined> }),
  /**
   * Per-domain aggregate for the "Web Activity" page. Visits and Duration
   * are computed server-side over the full filtered set so infinite-scroll
   * page loads never inflate the parent row totals.
   */
  usage: (params?: {
    page?: number; perPage?: number; employeeId?: string; itemType?: string; search?: string;
    dateFrom?: string; dateTo?: string;
  }) =>
    request<WebUsageListResponse>('/app-items/usage', { params: params as Record<string, string | number | undefined> }),
};

// ──────────────────────────
// Monitoring Configuration API
// (types, categories, and app/site classification)
// ──────────────────────────

export type MonitoringCategoryKind = 'application' | 'website' | 'both';

export interface MonitoringType {
  id: number;
  name: string;
  color: string;
  description: string;
}

export interface MonitoringCategory {
  id: number;
  name: string;
  kind: MonitoringCategoryKind;
}

export interface MonitoredApp {
  id: string;
  appName: string;
  binaryName: string;
  categories: string;
  isBrowser: boolean;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface MonitoredSite {
  id: number;
  domain: string;
  typeId?: number;
  typeName: string;
  typeColor: string;
  categoryId?: number;
  categoryName: string;
}

export interface MonitoredAppListResponse {
  data: MonitoredApp[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface MonitoredSiteListResponse {
  data: MonitoredSite[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface MonitoringTypePayload {
  name: string;
  color?: string;
  description?: string;
}

export interface MonitoringCategoryPayload {
  name: string;
  kind: MonitoringCategoryKind;
}

export interface ClassificationPayload {
  typeId?: number | null;
  categoryId?: number | null;
}

export const monitoringApi = {
  types: {
    list: () =>
      request<{ types: MonitoringType[]; total: number }>('/monitoring/types'),

    create: (data: MonitoringTypePayload) =>
      request<MonitoringType>('/monitoring/types', { method: 'POST', body: data }),

    update: (id: number, data: MonitoringTypePayload) =>
      request<MonitoringType>('/monitoring/types/' + id, { method: 'PUT', body: data }),

    delete: (id: number) =>
      request<{ message: string }>('/monitoring/types/' + id, { method: 'DELETE' }),
  },

  categories: {
    list: (kind?: MonitoringCategoryKind) =>
      request<{ categories: MonitoringCategory[]; total: number }>('/monitoring/categories', { params: { kind } }),

    create: (data: MonitoringCategoryPayload) =>
      request<MonitoringCategory>('/monitoring/categories', { method: 'POST', body: data }),

    update: (id: number, data: MonitoringCategoryPayload) =>
      request<MonitoringCategory>('/monitoring/categories/' + id, { method: 'PUT', body: data }),

    delete: (id: number) =>
      request<{ message: string }>('/monitoring/categories/' + id, { method: 'DELETE' }),
  },

  apps: {
    list: (params?: {
      search?: string; typeId?: number; categoryId?: number; unclassified?: boolean;
      page?: number; perPage?: number;
    }) =>
      request<MonitoredAppListResponse>('/monitoring/apps', { params: params as Record<string, string | number | boolean | undefined> }),

    classify: (id: string, data: ClassificationPayload) =>
      request<{ message: string }>('/monitoring/apps/' + id, { method: 'PATCH', body: data }),
  },

  websites: {
    list: (params?: {
      search?: string; typeId?: number; categoryId?: number; unclassified?: boolean;
      page?: number; perPage?: number;
    }) =>
      request<MonitoredSiteListResponse>('/monitoring/websites', { params: params as Record<string, string | number | boolean | undefined> }),

    classify: (id: number, data: ClassificationPayload) =>
      request<{ message: string }>('/monitoring/websites/' + id, { method: 'PATCH', body: data }),

    create: (data: { domain: string; typeId?: number | null; categoryId?: number | null }) =>
      request<MonitoredSite>('/monitoring/websites', { method: 'POST', body: data }),
  },
};

// ──────────────────────────
// RBAC API (roles + module/submodule catalog)
// ──────────────────────────

export interface SubmoduleNode {
  id: number;
  moduleId: number;
  key: string;
  name: string;
  routePath: string;
}

export interface ModuleNode {
  id: number;
  key: string;
  name: string;
  sortOrder: number;
  submodules: SubmoduleNode[];
}

export interface ModuleTreeResponse {
  modules: ModuleNode[];
  total: number;
}

export interface Role {
  id: number;
  name: string;
  description: string;
  isSystem: boolean;
  userCount: number;
  submoduleIds: number[];
  permissions: string[];
}

export interface RoleListResponse {
  roles: Role[];
  total: number;
}

export interface CreateRolePayload {
  name: string;
  description?: string;
  submoduleIds?: number[];
}

export interface UpdateRolePayload {
  name?: string;
  description?: string;
  submoduleIds?: number[];
}

export const modulesApi = {
  tree: () => request<ModuleTreeResponse>('/modules'),
};

export const rolesApi = {
  list: () => request<RoleListResponse>('/roles'),

  create: (data: CreateRolePayload) =>
    request<Role>('/roles', { method: 'POST', body: data }),

  update: (id: number, data: UpdateRolePayload) =>
    request<Role>('/roles/' + id, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>('/roles/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Users API (web-dashboard login accounts)
// ──────────────────────────

export interface User {
  id: string;
  employeeId: string;
  name: string;
  email: string;
  roleId: number;
  role: string;
  shift: string;
  trackingEnabled: boolean;
  trackingStatus: string;
  /** Account status: 'active' | 'suspended'. Suspended users cannot sign in. */
  status: string;
  isOnline: boolean;
  avatar: string;
  avatarColor: string;
  createdAt: string;
  updatedAt: string;
}

export interface UserListResponse {
  data: User[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface CreateUserPayload {
  name: string;
  email: string;
  password?: string;
  employeeId?: string;
  roleId: number;
  shift?: string;
}

export interface UpdateUserPayload {
  name?: string;
  email?: string;
  password?: string;
  roleId?: number;
  shift?: string;
  status?: 'active' | 'suspended';
}

export const usersApi = {
  list: (params?: {
    page?: number;
    perPage?: number;
    search?: string;
    roleId?: number;
    /** Filters users.tracking_status ("tracked" | "untracked"). */
    status?: string;
    /** Filters the ACCOUNT status ("active" | "suspended"). */
    accountStatus?: 'active' | 'suspended';
  }) =>
    request<UserListResponse>('/users', { params: params as Record<string, string | number | undefined> }),

  get: (id: string) => request<User>('/users/' + id),

  create: (data: CreateUserPayload) =>
    request<User>('/users', { method: 'POST', body: data }),

  update: (id: string, data: UpdateUserPayload) =>
    request<User>('/users/' + id, { method: 'PUT', body: data }),

  delete: (id: string) =>
    request<{ message: string }>('/users/' + id, { method: 'DELETE' }),
};

// ──────────────────────────
// Time & Attendance API
// ──────────────────────────

export type AttendanceStatus =
  | 'present'
  | 'late'
  | 'absent'
  | 'half_day'
  | 'off_shift'
  | 'unknown';

export interface AttendanceRecord {
  employeeId: string;
  workDate: string;
  timezone?: string;
  firstActiveAt?: string | null;
  lastActiveAt?: string | null;
  activeSeconds: number;
  idleSeconds: number;
  offShiftSeconds: number;
  status: AttendanceStatus;
  lateMinutes: number;
}

export interface AttendanceRangeResponse {
  data: AttendanceRecord[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export type HolidayStatus = 'pending' | 'approved' | 'rejected';

export interface Holiday {
  id: number;
  date: string;
  label: string;
  status: HolidayStatus;
}

export interface HolidayListResponse {
  data: Holiday[];
  total: number;
}

export interface HolidayInput {
  date: string;
  label: string;
  status: HolidayStatus;
}

export const attendanceApi = {
  today: (employeeId: string) =>
    request<AttendanceRecord>('/attendance/today', { params: { employeeId } }),

  range: (params: {
    employeeId: string;
    from: string;
    to: string;
    page?: number;
    perPage?: number;
  }) =>
    request<AttendanceRangeResponse>('/attendance/range', {
      params: params as Record<string, string | number | undefined>,
    }),

  /** Convenience wrapper: one employee, one calendar day. */
  day: (employeeId: string, date: string) =>
    request<AttendanceRangeResponse>('/attendance/range', {
      params: { employeeId, from: date, to: date, page: 1, perPage: 1 },
    }).then(r => r.data[0] ?? null),

  events: (employeeId: string, date: string) =>
    request<DaySessionEventsResponse>('/attendance/events', {
      params: { employeeId, date },
    }),
};

export interface SessionEventRow {
  id: string;
  employeeId: string;
  eventType: string;
  osUsername: string;
  eventAt: string;
  count: number;
  firstAt: string;
  lastAt: string;
  syncedAt?: string | null;
}

export interface DaySessionEventsResponse {
  employeeId: string;
  workDate: string;
  timezone: string;
  data: SessionEventRow[];
  total: number;
}

export const holidaysApi = {
  list: () => request<HolidayListResponse>('/holidays'),

  create: (data: HolidayInput) =>
    request<Holiday>('/holidays', { method: 'POST', body: data }),

  update: (id: number, data: HolidayInput) =>
    request<Holiday>(`/holidays/${id}`, { method: 'PUT', body: data }),

  delete: (id: number) =>
    request<{ message: string }>(`/holidays/${id}`, { method: 'DELETE' }),
};

// ──────────────────────────
// Location Samples API (Phase 3 GPS)
// ──────────────────────────

export type LocationSource = 'gps' | 'wifi' | 'ip' | 'manual';

export interface LocationSample {
  id: string;
  employeeId: string;
  employeeName?: string;
  latitude: number;
  longitude: number;
  accuracyM?: number;
  altitudeM?: number;
  source: LocationSource | string;
  address?: string;
  capturedAt: string;
  syncedAt?: string;
  geofenceStatus?: string;
}

export interface LocationSampleListResponse {
  data: LocationSample[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export const locationSamplesApi = {
  list: (params?: {
    page?: number;
    perPage?: number;
    employeeId?: string;
    dateFrom?: string;
    dateTo?: string;
  }) =>
    request<LocationSampleListResponse>('/location-samples', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ──────────────────────────
// Geofence Zones API (Phase 3 GPS B.8)
// ──────────────────────────

export interface GeofenceZone {
  id: number;
  name: string;
  latitude: number;
  longitude: number;
  radiusM: number;
  alertOnExit: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface GeofenceZoneListResponse {
  data: GeofenceZone[];
}

export const geofenceApi = {
  list: () => request<GeofenceZoneListResponse>('/geofence-zones'),
  create: (body: {
    name: string;
    latitude: number;
    longitude: number;
    radiusM: number;
    alertOnExit?: boolean;
  }) =>
    request<GeofenceZone>('/geofence-zones', {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  update: (
    id: number,
    body: Partial<{
      name: string;
      latitude: number;
      longitude: number;
      radiusM: number;
      alertOnExit: boolean;
    }>,
  ) =>
    request<GeofenceZone>(`/geofence-zones/${id}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),
  delete: (id: number) =>
    request<{ message: string }>(`/geofence-zones/${id}`, { method: 'DELETE' }),
};

// ──────────────────────────
// Health API
// ──────────────────────────

export const healthApi = {
  check: () => request<{ status: string; timestamp: string }>('/health'),
};

// ──────────────────────────
// Hours Insights API
// ──────────────────────────

export interface HoursInsightsEmployee {
  employeeId: string;
  name: string;
  department: string;
}

export interface HoursInsightsRange {
  from: string;
  to: string;
  label: string;
}

export interface HoursInsightsSummary {
  totalSeconds: number;
  productiveSeconds: number;
  unproductiveSeconds: number;
  neutralSeconds: number;
  focusScore: number;
  appCount: number;
  siteCount: number;
}

export interface HoursInsightsChartBucket {
  bucket: string;
  productive: number;
  unproductive: number;
  neutral: number;
}

export interface HoursInsightsTopItem {
  name: string;
  kind: 'app' | 'site';
  category: string;
  type: string;
  color: string;
  totalSeconds: number;
  focusScore: number;
  isBrowser: boolean;
}

export interface HoursInsightsAppBucket {
  bucket: string;
  apps: Record<string, number>;
}

export interface HoursInsightsAppMeta {
  name: string;
  totalSeconds: number;
  color: string;
  category: string;
  type: string;
  sessionCount: number;
}

export interface HoursInsightsResponse {
  employee: HoursInsightsEmployee;
  range: HoursInsightsRange;
  summary: HoursInsightsSummary;
  chart: HoursInsightsChartBucket[];
  appChart: HoursInsightsAppBucket[];
  topApps: HoursInsightsAppMeta[];
  topItems: HoursInsightsTopItem[];
}

export const hoursInsightsApi = {
  get: (params: {
    employeeId: string;
    dateFrom?: string;
    dateTo?: string;
    preset?: string;
  }) =>
    request<HoursInsightsResponse>('/hours-insights', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ──────────────────────────
// Live Stream API
// ──────────────────────────

export interface LiveStreamEmployee {
  employeeId: string;
  name: string;
  department: string;
  online: boolean;
  /** Presence control socket GET /api/v1/ws — independent of live-stream push. */
  wsConnected: boolean;
  streaming: boolean;
  streamAvailable: boolean;
  /** Live-stream WebRTC push signaling socket. */
  clientConnected: boolean;
}

export interface IceServerConfig {
  urls: string | string[];
  username?: string;
  credential?: string;
}

export const liveStreamApi = {
  employees: (params?: {
    onlineOnly?: boolean;
    limit?: number;
    departmentId?: number;
  }) =>
    request<{ data: LiveStreamEmployee[]; total: number }>('/live-stream/employees', {
      params: params as Record<string, string | number | boolean | undefined>,
    }),
  watchTicket: (employeeId: string) =>
    request<{
      ticket: string;
      expiresIn: number;
      iceServers?: IceServerConfig[];
      /** Phase 2: publisher SFU on another instance — open the watch WS here. */
      watchBaseUrl?: string;
    }>('/live-stream/watch-ticket', { params: { employeeId } }),
};

// ──────────────────────────
// Dashboard summary (home)
// ──────────────────────────

export interface DashboardVersionRow {
  version: string;
  count: number;
}

export interface DashboardTopApp {
  appDisplayName: string;
  processName: string;
  sessionCount: number;
  openNow: number;
}

export interface DashboardTopDomain {
  domain: string;
  visits: number;
}

export interface DashboardRecentSession {
  id: string;
  employeeId: string;
  employeeName: string;
  appDisplayName: string;
  processName: string;
  status: string;
  startedAt: string;
  endedAt?: string | null;
  lastSyncAt?: string | null;
}

export interface DashboardSummaryResponse {
  range: { from: string; to: string };
  employees: { total: number; tracked: number; untracked: number };
  activity: {
    sessions: number;
    webPages: number;
    openSessions: number;
    staleSessions: number;
  };
  monitoring: { unclassifiedApps: number; unclassifiedSites: number };
  devices: {
    active: number;
    seen15m: number;
    seen24h: number;
    stale7d: number;
    versions: DashboardVersionRow[];
  };
  attendance: {
    date: string;
    total: number;
    present: number;
    late: number;
    absent: number;
    halfDay: number;
    offShift: number;
    unknown: number;
  };
  live: {
    online: number;
    streaming: number;
    presenceAvailable: boolean;
  };
  topApps: DashboardTopApp[];
  topDomains: DashboardTopDomain[];
  recentSessions: DashboardRecentSession[];
}

export const dashboardApi = {
  summary: (params: {
    from: string;
    to: string;
    departmentId?: number;
    topN?: number;
    recentLimit?: number;
  }) =>
    request<DashboardSummaryResponse>('/dashboard/summary', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ──────────────────────────
// Log Insights (digest)
// ──────────────────────────

export interface LogInsightsHighlight {
  severity: 'info' | 'warning' | 'success';
  title: string;
  detail: string;
}

export interface LogInsightsOutlier {
  employeeId: string;
  employeeName: string;
  kind: 'idle' | 'sessions' | 'web' | string;
  value: number;
  label: string;
}

export interface LogInsightsResponse {
  range: { from: string; to: string };
  metrics: {
    sessions: number;
    webPages: number;
    activeEmployees: number;
    openSessions: number;
    staleSessions: number;
    idleEvents: number;
    employeesWithIdle: number;
  };
  productivity: {
    totalSeconds: number;
    productiveSeconds: number;
    unproductiveSeconds: number;
    neutralSeconds: number;
    productivePct: number;
    unproductivePct: number;
    neutralPct: number;
  };
  topApps: DashboardTopApp[];
  topDomains: DashboardTopDomain[];
  outliers: LogInsightsOutlier[];
  highlights: LogInsightsHighlight[];
}

export const logInsightsApi = {
  get: (params: {
    from: string;
    to: string;
    departmentId?: number;
    employeeId?: string;
    topN?: number;
  }) =>
    request<LogInsightsResponse>('/logs/insights', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ──────────────────────────
// Log Graphical (charts)
// ──────────────────────────

export interface LogGraphicalActivityPt {
  bucket: string;
  at: string;
  sessions: number;
  webPages: number;
  idleEvents: number;
}

export interface LogGraphicalProductivityPt {
  bucket: string;
  at: string;
  productiveSeconds: number;
  unproductiveSeconds: number;
  neutralSeconds: number;
}

export interface LogGraphicalResponse {
  range: { from: string; to: string };
  bucket: 'hour' | 'day' | string;
  summary: {
    sessions: number;
    webPages: number;
    idleEvents: number;
    totalSeconds: number;
    productiveSeconds: number;
    unproductiveSeconds: number;
    neutralSeconds: number;
  };
  activity: LogGraphicalActivityPt[];
  productivity: LogGraphicalProductivityPt[];
  topApps: DashboardTopApp[];
  topDomains: DashboardTopDomain[];
}

export const logGraphicalApi = {
  get: (params: {
    from: string;
    to: string;
    departmentId?: number;
    employeeId?: string;
    topN?: number;
  }) =>
    request<LogGraphicalResponse>('/logs/graphical', {
      params: params as Record<string, string | number | undefined>,
    }),
};

// ─── Security & DLP ───────────────────────────────────────────────────────────

export type DlpTrigger =
  | 'usb_storage'
  | 'usb_file'
  | 'web_navigate'
  | 'software_install'
  | 'software_uninstall'
  | 'cloud_sync_folder'
  | 'file_transfer'
  | 'email';

export type DlpAction = 'alert' | 'block' | 'alert_block';
export type DlpSeverity = 'critical' | 'high' | 'medium' | 'low';
export type DlpAlertStatus = 'open' | 'investigating' | 'resolved' | 'false_positive';

export interface DlpRule {
  id: string;
  name: string;
  enabled: boolean;
  trigger: DlpTrigger;
  action: DlpAction;
  severity: DlpSeverity;
  scopeAll: boolean;
  extensions: string[];
  patterns: string[];
  createdAt: string;
  updatedAt: string;
}

/** Snapshot taken before each classic DLP rule UPDATE. */
export interface DlpRuleRevision {
  id: number;
  ruleId: string;
  revision: number;
  name: string;
  enabled: boolean;
  trigger: DlpTrigger;
  action: DlpAction;
  severity: DlpSeverity;
  scopeAll: boolean;
  extensions: string[];
  patterns: string[];
  changedBy?: string;
  changedByName: string;
  createdAt: string;
}

export interface WebBlockerRule {
  id: string;
  name: string;
  enabled: boolean;
  action: DlpAction;
  severity: DlpSeverity;
  scopeAll: boolean;
  domains: string[];
  paths: string[];
  scheduleEnabled: boolean;
  scheduleStart: string;
  scheduleEnd: string;
  scheduleTimezone: string;
  scheduleDays: string[];
  createdAt: string;
  updatedAt: string;
}

/** Snapshot taken before each Web Blocker rule UPDATE. */
export interface WebBlockerRuleRevision {
  id: number;
  ruleId: string;
  revision: number;
  name: string;
  enabled: boolean;
  action: DlpAction;
  severity: DlpSeverity;
  scopeAll: boolean;
  domains: string[];
  paths: string[];
  scheduleEnabled: boolean;
  scheduleStart: string;
  scheduleEnd: string;
  scheduleTimezone: string;
  scheduleDays: string[];
  changedBy?: string;
  changedByName: string;
  createdAt: string;
}

export interface ExpandWebBlockerDomainsResponse {
  domains: string[];
  suggested: string[];
}

export interface DlpAlert {
  id: string;
  ruleId?: string;
  policyVersion: number;
  employeeId: string;
  employeeName?: string;
  machineId: string;
  type: string;
  severity: DlpSeverity;
  actionTaken: string;
  occurredAt: string;
  evidence: Record<string, string>;
  status: DlpAlertStatus;
  assignedTo: string;
  notes: string;
  syncedAt: string;
  createdAt: string;
  updatedAt: string;
}

/** One triage field change (status / assigned_to / notes). */
export interface DlpAlertHistoryEvent {
  id: number;
  alertId: string;
  changedBy?: string;
  changedByName: string;
  field: 'status' | 'assigned_to' | 'notes' | string;
  oldValue: string;
  newValue: string;
  createdAt: string;
}

export interface DlpAlertHistoryResponse {
  events: DlpAlertHistoryEvent[];
}

export interface DlpAlertListResponse {
  alerts: DlpAlert[];
  total: number;
  page: number;
  perPage: number;
  open: number;
  investigating: number;
  resolved: number;
  /** Alerts dismissed as a false positive. */
  falsePositive: number;
  /** Distinct alert types present under the current filters. */
  types: string[];
}

export interface ShadowItApp {
  id: number;
  name: string;
  nameNormalized: string;
  kind: string;
  authStatus: 'approved' | 'unauthorized' | 'under_review';
  risk: 'high' | 'medium' | 'low';
  firstDetectedAt: string;
  userCount: number;
  lastSeenAt: string;
  createdAt: string;
  updatedAt: string;
}

export interface ShadowItListResponse {
  apps: ShadowItApp[];
  total: number;
  page: number;
  perPage: number;
  approved: number;
  unauthorized: number;
  underReview: number;
}

export interface PublishDlpPolicyResponse {
  policyVersion: number;
  hash: string;
  publishedAt: string;
  ruleCount: number;
}

export interface IssueRemovalCodeResponse {
  code: string;
  expiresAt: string;
  purpose: string;
  machineId: string;
}

/** Draft allowlist / lockdown policy (GET/PUT /dlp/lockdown). Enforced only after Publish. */
export interface DlpLockdownAllowApp {
  publisher?: string;
  product?: string;
  sha256?: string;
}

export interface DlpLockdownConfig {
  lockdown: boolean;
  allowHosts: string[];
  allowApps: DlpLockdownAllowApp[];
  platformAllow: string[];
  maxAllowHosts: number;
  maxAllowApps: number;
  hostCount: number;
  appCount: number;
}

export interface AllowHostRow {
  id: number;
  host: string;
  note?: string;
  enabled: boolean;
  createdBy?: string;
  createdByName?: string;
  updatedBy?: string;
  updatedByName?: string;
  createdAt: string;
  updatedAt: string;
}

export interface AllowHostListResponse {
  hosts: AllowHostRow[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export interface AllowAppRow {
  id: number;
  publisher?: string;
  product?: string;
  sha256?: string;
  note?: string;
  enabled: boolean;
  createdBy?: string;
  createdByName?: string;
  updatedBy?: string;
  updatedByName?: string;
  createdAt: string;
  updatedAt: string;
}

export interface AllowAppListResponse {
  apps: AllowAppRow[];
  total: number;
  page: number;
  perPage: number;
  totalPages: number;
}

export const dlpApi = {
  lockdown: {
    get: () => request<DlpLockdownConfig>('/dlp/lockdown'),
    update: (data: {
      lockdown?: boolean;
      allowHosts?: string[];
      allowApps?: DlpLockdownAllowApp[];
    }) => request<DlpLockdownConfig>('/dlp/lockdown', { method: 'PUT', body: data }),
    hosts: {
      list: (params?: {
        q?: string;
        createdBy?: string;
        enabled?: string;
        page?: number;
        perPage?: number;
      }) => {
        const sp = new URLSearchParams();
        if (params?.q) sp.set('q', params.q);
        if (params?.createdBy) sp.set('createdBy', params.createdBy);
        if (params?.enabled) sp.set('enabled', params.enabled);
        if (params?.page) sp.set('page', String(params.page));
        if (params?.perPage) sp.set('perPage', String(params.perPage));
        const qs = sp.toString();
        return request<AllowHostListResponse>('/dlp/lockdown/hosts' + (qs ? `?${qs}` : ''));
      },
      create: (data: { host: string; note?: string; enabled?: boolean }) =>
        request<AllowHostRow>('/dlp/lockdown/hosts', { method: 'POST', body: data }),
      update: (id: number, data: { host?: string; note?: string; enabled?: boolean }) =>
        request<AllowHostRow>(`/dlp/lockdown/hosts/${id}`, { method: 'PATCH', body: data }),
      remove: (id: number) =>
        request<void>(`/dlp/lockdown/hosts/${id}`, { method: 'DELETE' }),
    },
    apps: {
      list: (params?: {
        q?: string;
        createdBy?: string;
        enabled?: string;
        page?: number;
        perPage?: number;
      }) => {
        const sp = new URLSearchParams();
        if (params?.q) sp.set('q', params.q);
        if (params?.createdBy) sp.set('createdBy', params.createdBy);
        if (params?.enabled) sp.set('enabled', params.enabled);
        if (params?.page) sp.set('page', String(params.page));
        if (params?.perPage) sp.set('perPage', String(params.perPage));
        const qs = sp.toString();
        return request<AllowAppListResponse>('/dlp/lockdown/apps' + (qs ? `?${qs}` : ''));
      },
      create: (data: {
        publisher?: string;
        product?: string;
        sha256?: string;
        note?: string;
        enabled?: boolean;
      }) => request<AllowAppRow>('/dlp/lockdown/apps', { method: 'POST', body: data }),
      update: (
        id: number,
        data: {
          publisher?: string;
          product?: string;
          sha256?: string;
          note?: string;
          enabled?: boolean;
        },
      ) => request<AllowAppRow>(`/dlp/lockdown/apps/${id}`, { method: 'PATCH', body: data }),
      remove: (id: number) =>
        request<void>(`/dlp/lockdown/apps/${id}`, { method: 'DELETE' }),
    },
  },

  issueRemovalCode: (data: {
    machineId: string;
    employeeId: string;
    purpose?: 'stop' | 'uninstall' | 'both';
    ttlMinutes?: number;
  }) =>
    request<IssueRemovalCodeResponse>('/devices/removal-code', {
      method: 'POST',
      body: data,
    }),

  rules: {
    list: () => request<{ rules: DlpRule[]; total: number }>('/dlp/rules'),
    get: (id: string) => request<DlpRule>(`/dlp/rules/${id}`),
    create: (data: {
      name: string;
      trigger: string;
      action?: string;
      severity?: string;
      scopeAll?: boolean;
      extensions?: string[];
      patterns?: string[];
      enabled?: boolean;
    }) => request<DlpRule>('/dlp/rules', { method: 'POST', body: data }),
    update: (id: string, data: Partial<{
      name: string; enabled: boolean; trigger: string;
      action: string; severity: string; scopeAll: boolean;
      extensions: string[]; patterns: string[];
    }>) => request<DlpRule>(`/dlp/rules/${id}`, { method: 'PATCH', body: data }),
    delete: (id: string) =>
      request<{ message: string }>(`/dlp/rules/${id}`, { method: 'DELETE' }),
    history: (id: string) =>
      request<{ revisions: DlpRuleRevision[]; total: number }>(
        `/dlp/rules/${id}/history`,
      ),
    publish: () =>
      request<PublishDlpPolicyResponse>('/dlp/rules/publish', { method: 'POST' }),
  },

  webBlocker: {
    list: () => request<{ rules: WebBlockerRule[]; total: number }>('/web-blocker/rules'),
    create: (data: {
      name: string;
      action?: string;
      severity?: string;
      scopeAll?: boolean;
      domains?: string[];
      paths?: string[];
      scheduleEnabled?: boolean;
      scheduleStart?: string;
      scheduleEnd?: string;
      scheduleTimezone?: string;
      scheduleDays?: string[];
      enabled?: boolean;
      expandRelated?: boolean;
    }) => request<WebBlockerRule>('/web-blocker/rules', { method: 'POST', body: data }),
    update: (id: string, data: Partial<{
      name: string; enabled: boolean; action: string; severity: string; scopeAll: boolean;
      domains: string[]; paths: string[];
      scheduleEnabled: boolean; scheduleStart: string; scheduleEnd: string;
      scheduleTimezone: string; scheduleDays: string[];
      expandRelated: boolean;
    }>) => request<WebBlockerRule>(`/web-blocker/rules/${id}`, { method: 'PATCH', body: data }),
    delete: (id: string) =>
      request<{ message: string }>(`/web-blocker/rules/${id}`, { method: 'DELETE' }),
    history: (id: string) =>
      request<{ revisions: WebBlockerRuleRevision[]; total: number }>(
        `/web-blocker/rules/${id}/history`,
      ),
    expandDomains: (domains: string[]) =>
      request<ExpandWebBlockerDomainsResponse>('/web-blocker/domains/expand', {
        method: 'POST',
        body: { domains },
      }),
  },

  alerts: {
    list: (params?: {
      q?: string; status?: string; severity?: string; type?: string; employeeId?: string;
      dateFrom?: string; dateTo?: string;
      page?: number; perPage?: number;
    }) =>
      request<DlpAlertListResponse>('/dlp/alerts', {
        params: params as Record<string, string | number | undefined>,
      }),
    update: (id: string, data: { status?: DlpAlertStatus; assignedTo?: string; notes?: string }) =>
      request<DlpAlert>(`/dlp/alerts/${id}`, { method: 'PATCH', body: data }),
    history: (id: string) =>
      request<DlpAlertHistoryResponse>(`/dlp/alerts/${id}/history`),
  },
};

export const shadowItApi = {
  list: (params?: {
    q?: string; authStatus?: string; page?: number; perPage?: number;
  }) =>
    request<ShadowItListResponse>('/shadow-it/apps', {
      params: params as Record<string, string | number | undefined>,
    }),
  update: (id: number, data: {
    authStatus?: string; risk?: string; action?: 'approve' | 'block' | 'flag';
  }) =>
    request<ShadowItApp>(`/shadow-it/apps/${id}`, { method: 'PATCH', body: data }),
};

export { ApiError };
