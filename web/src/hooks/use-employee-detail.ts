'use client';

import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { employeesApi } from '@/lib/api';

/**
 * Fetches the aggregate machine picture for one employee (UUID id).
 * Disabled until an employee is selected. `deviceId` (a machine id) scopes every
 * device-level surface to that machine; empty means the employee-wide aggregate.
 *
 * `deviceId` is part of the query key, so scoping to a machine RE-KEYS the
 * query. `keepPreviousData` holds the previous payload (the server returns the
 * SAME full `devices[]` machine list on every scoped response — only
 * hardware/storage/network/etc. are scoped) while the re-keyed request is in
 * flight. Without it, `devices[]` would be `[]` for a render, the shell's
 * mirror effect would resolve `effectiveDeviceId` back to `''`, rewrite the
 * URL, re-key the query again — the `'' ⇄ machine-id` loop that surfaced as
 * React's "Maximum update depth exceeded" on /device-specs.
 */
export function useEmployeeDetail(employeeId: string, deviceId = '') {
  return useQuery({
    queryKey: ['employee-detail', employeeId, deviceId],
    queryFn: () => employeesApi.detail(employeeId, deviceId || undefined),
    enabled: !!employeeId,
    placeholderData: keepPreviousData,
  });
}
