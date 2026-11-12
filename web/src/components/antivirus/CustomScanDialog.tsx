'use client';

import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { FolderSearch, Loader2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { antivirusApi, ApiError, type AntivirusStatusRow } from '@/lib/api';
import { isValidScanPath } from './labels';

const EXAMPLES = ['C:\\Users\\Public\\Downloads', 'D:\\', 'C:\\Users\\Public\\Desktop\\file.zip'];

/** Ask Defender on one PC to scan a single folder or file. */
export function CustomScanDialog({
  row,
  open,
  onOpenChange,
}: {
  row: AntivirusStatusRow;
  open: boolean;
  onOpenChange: (v: boolean) => void;
}) {
  const qc = useQueryClient();
  const [path, setPath] = useState('');
  const [message, setMessage] = useState<string | null>(null);
  const valid = isValidScanPath(path);

  const mutation = useMutation({
    mutationFn: () =>
      antivirusApi.queueCommand({
        employeeId: row.employeeId,
        machineId: row.machineId,
        type: 'scan_custom',
        arg: path.trim(),
      }),
    onSuccess: (res) => {
      setMessage(
        res.reused
          ? 'This exact scan is already waiting for the PC — no duplicate was added.'
          : 'Scan queued. The PC starts it on its next check-in (about 30 seconds). Follow progress in History.',
      );
      qc.invalidateQueries({ queryKey: ['antivirus-commands', row.employeeId, row.machineId] });
    },
  });

  const close = (v: boolean) => {
    onOpenChange(v);
    if (!v) {
      setPath('');
      setMessage(null);
      mutation.reset();
    }
  };

  const pc = `${row.employeeName || row.employeeId} (${row.hostname || row.machineId || 'unknown machine'})`;

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <FolderSearch className="w-4 h-4" /> Custom scan
          </DialogTitle>
        </DialogHeader>
        <div className="space-y-3 text-sm">
          <p className="text-muted-foreground">
            Microsoft Defender on <span className="font-medium text-foreground">{pc}</span> will scan
            only the folder or file you enter. Anything malicious is quarantined automatically and
            shows up as a malware alert.
          </p>
          <label className="block">
            <span className="text-xs font-medium text-muted-foreground">Folder or file path on that PC</span>
            <input
              value={path}
              onChange={(e) => {
                setPath(e.target.value);
                setMessage(null);
              }}
              placeholder="C:\Users\Public\Downloads"
              className="mt-1 w-full bg-background border border-border rounded-lg px-3 py-2 font-mono text-sm outline-none focus:border-primary"
              autoFocus
            />
          </label>
          {path.trim() !== '' && !valid && (
            <p className="text-xs text-destructive">
              Enter a full Windows path that starts with a drive letter (C:\…) or a network share (\\server\share\…).
            </p>
          )}
          <div className="text-xs text-muted-foreground">
            Examples:{' '}
            {EXAMPLES.map((ex) => (
              <button
                key={ex}
                type="button"
                onClick={() => setPath(ex)}
                className="font-mono mr-2 underline decoration-dotted hover:text-foreground"
              >
                {ex}
              </button>
            ))}
          </div>
          {message && <p className="text-xs text-success">{message}</p>}
          {mutation.isError && (
            <p className="text-xs text-destructive">
              {mutation.error instanceof ApiError ? mutation.error.message : 'Could not queue the scan.'}
            </p>
          )}
          <div className="flex justify-end gap-2 pt-1">
            <button
              type="button"
              onClick={() => close(false)}
              className="px-3 py-1.5 rounded-lg border border-border text-sm hover:bg-muted/40"
            >
              {message ? 'Close' : 'Cancel'}
            </button>
            <button
              type="button"
              disabled={!valid || mutation.isPending}
              onClick={() => mutation.mutate()}
              className="px-3 py-1.5 rounded-lg bg-primary text-primary-foreground text-sm font-medium disabled:opacity-40 flex items-center gap-1"
            >
              {mutation.isPending && <Loader2 className="w-3 h-3 animate-spin" />} Start scan
            </button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
