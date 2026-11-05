'use client';

import { Camera } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';

export default function EmployeeJourneyScreenshots() {
  return (
    <EmployeePage
      title="Screenshots"
      subtitle="Periodic screen captures of the employee's machine during the workday."
      icon={Camera}
    >
      {() => (
        <EmptyState
          icon={Camera}
          text="Screenshot capture is not enabled yet — the desktop client does not currently collect screen images."
        />
      )}
    </EmployeePage>
  );
}
