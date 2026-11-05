'use client';

export default function InventoryTable({
  headers,
  children,
  empty,
  emptyText,
  minWidth = 'min-w-[640px]',
}: {
  headers: string[];
  children: React.ReactNode;
  empty: boolean;
  emptyText: string;
  minWidth?: string;
}) {
  return (
    <div className="bg-card rounded-xl border border-border shadow-card overflow-x-auto">
      {empty ? (
        <div className="text-center py-12 text-muted-foreground text-sm">{emptyText}</div>
      ) : (
        <table className={`w-full ${minWidth}`}>
          <thead>
            <tr className="border-b border-border">
              {headers.map(h => <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>)}
            </tr>
          </thead>
          <tbody>{children}</tbody>
        </table>
      )}
    </div>
  );
}
