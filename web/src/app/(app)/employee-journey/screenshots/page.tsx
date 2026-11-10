'use client';

import { Suspense } from 'react';
import { Camera, Loader2 } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import { ScreenshotGrid } from '@/components/screenshots/ScreenshotGrid';

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
      >
        {({ employee, filter }) => (
          <ScreenshotGrid
            employeeId={employee.employeeId}
            from={filter.dateFrom}
            to={filter.dateTo}
          />
        )}
      </EmployeePage>
    </Suspense>
  );
}
