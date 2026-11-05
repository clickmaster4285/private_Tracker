'use client';

import { format } from 'date-fns';
import type { ActivityFilter, DatePreset } from '@/components/journey/ActivityFilters';
import { presetRange } from '@/components/journey/ActivityFilters';
import { useUrlActivityFilter } from '@/hooks/use-url-activity-filter';

export type DashboardExtra = {
  departmentId: string;
};

/** URL-synced dashboard filters: activity preset + optional departmentId. */
export function useDashboardFilters() {
  const { filter, setFilter, extra, setExtra } = useUrlActivityFilter<DashboardExtra>(
    { departmentId: {} },
    { departmentId: '' },
  );

  const departmentId = extra.departmentId ? Number(extra.departmentId) : undefined;
  const setDepartmentId = (id: number | undefined) => {
    setExtra({ departmentId: id && id > 0 ? String(id) : '' });
  };

  /** Date-only bounds for summary API (server treats `to` as inclusive calendar day → exclusive end). */
  const summaryRange = (): { from: string; to: string } => {
    if (filter.preset === 'all' || !filter.dateFrom || !filter.dateTo) {
      return { from: '1970-01-01', to: format(new Date(), 'yyyy-MM-dd') };
    }
    return {
      from: format(new Date(filter.dateFrom), 'yyyy-MM-dd'),
      to: format(new Date(filter.dateTo), 'yyyy-MM-dd'),
    };
  };

  const setPreset = (preset: DatePreset) => {
    if (preset === 'custom') return;
    const range = presetRange(preset);
    setFilter({ ...filter, search: '', preset, dateFrom: range.dateFrom, dateTo: range.dateTo });
  };

  return {
    filter,
    setFilter,
    setPreset,
    departmentId: Number.isFinite(departmentId) && (departmentId as number) > 0 ? departmentId : undefined,
    setDepartmentId,
    summaryRange,
  };
}

export type { ActivityFilter, DatePreset };
