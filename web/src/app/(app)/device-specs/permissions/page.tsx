'use client';

import { Suspense } from 'react';
import { ShieldCheck, ShieldX, Loader2 } from 'lucide-react';
import EmployeePage from '@/components/employees/EmployeePage';
import InventoryTable from '@/components/employees/InventoryTable';
import { formatDateTime } from '@/lib/format';

export default function DeviceSpecsPermissions() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <EmployeePage
        title="Permissions"
      subtitle="OS permission checks the desktop client ran on the employee's machine."
      icon={ShieldCheck}
      fetchDetail
    >
      {({ detail }) => {
        const { permissions } = detail!;

        return (
          <InventoryTable
            headers={['Permission Method', 'Platform', 'Status', 'Last Checked', 'Details']}
            empty={permissions.length === 0}
            emptyText="No permission checks recorded yet"
          >
            {permissions.map(perm => (
              <tr key={perm.checkId} className="border-b border-border last:border-0 hover:bg-muted/30 transition-colors">
                <td className="px-4 py-3">
                  <div className="flex items-center gap-2">
                    {perm.works ? <ShieldCheck className="w-4 h-4 text-success" /> : <ShieldX className="w-4 h-4 text-destructive" />}
                    <span className="text-sm font-medium text-foreground">{perm.method || perm.checkId}</span>
                  </div>
                </td>
                <td className="px-4 py-3">
                  <span className="px-2 py-0.5 rounded-md text-xs font-mono bg-muted text-muted-foreground capitalize">{perm.platform || '—'}</span>
                </td>
                <td className="px-4 py-3">
                  <span className={`px-2.5 py-0.5 rounded-full text-xs font-medium ${perm.works ? 'bg-success/15 text-success' : 'bg-destructive/15 text-destructive'}`}>
                    {perm.works ? 'Granted' : 'Blocked'}
                  </span>
                </td>
                <td className="px-4 py-3 text-sm text-muted-foreground whitespace-nowrap">{formatDateTime(perm.checkedAt)}</td>
                <td className="px-4 py-3 text-sm text-muted-foreground truncate max-w-[260px]">{perm.details || '—'}</td>
              </tr>
            ))}
          </InventoryTable>
        );
      }}
    </EmployeePage>
    </Suspense>
  );
}
