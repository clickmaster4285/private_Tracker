'use client';

import { Suspense } from 'react';
import { Camera, Loader2 } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import ActivityFilters, { type ActivityFilter } from '@/components/journey/ActivityFilters';
import { ScreenshotGrid } from '@/components/screenshots/ScreenshotGrid';
import {
  ScreenshotTimeRangeFilter,
  browserTimeZone,
  isoToYmd,
  parseTimeParam,
} from '@/components/screenshots/ScreenshotTimeRangeFilter';

export default function EmployeeJourneyScreenshots() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <EmployeePage
        title="Screenshots"
        subtitle="Periodic screen captures of the employee's machine during the workday."
        icon={Camera}
        bodySchema={{
          timeFrom: { parse: parseTimeParam },
          timeTo: { parse: parseTimeParam },
        }}
        bodyInitial={{ timeFrom: '', timeTo: '' }}
      >
        {({ employee, filter, setFilter, body, setBody }) => (
          <JourneyScreenshotsBody
            employeeId={employee.employeeId}
            filter={filter}
            setFilter={setFilter}
            timeFrom={body.timeFrom ?? ''}
            timeTo={body.timeTo ?? ''}
            onTimeChange={(next) => setBody(next)}
          />
        )}
      </EmployeePage>
    </Suspense>
  );
}

function JourneyScreenshotsBody({
  employeeId,
  filter,
  setFilter,
  timeFrom,
  timeTo,
  onTimeChange,
}: {
  employeeId: string;
  filter: ActivityFilter;
  setFilter: (next: ActivityFilter) => void;
  timeFrom: string;
  timeTo: string;
  onTimeChange: (next: { timeFrom: string; timeTo: string }) => void;
}) {
  const from = filter.preset === 'all' ? undefined : isoToYmd(filter.dateFrom);
  const to = filter.preset === 'all' ? undefined : isoToYmd(filter.dateTo);

  return (
    <div className="bg-card rounded-xl border border-border p-5 space-y-4">
      <ActivityFilters value={filter} onChange={setFilter} searchShow={false} />
      <ScreenshotTimeRangeFilter
        timeFrom={timeFrom}
        timeTo={timeTo}
        onChange={onTimeChange}
      />
      <ScreenshotGrid
        employeeId={employeeId}
        from={from}
        to={to}
        timeFrom={timeFrom || undefined}
        timeTo={timeTo || undefined}
        timeZone={browserTimeZone()}
      />
    </div>
  );
}
