'use client';

import { Suspense, useState } from 'react';
import { Loader2, ScanFace, Maximize2 } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import EmployeePage from '@/components/employees/EmployeePage';
import EmptyState from '@/components/employees/EmptyState';
import { EnrollFaceDialog } from '@/components/employees/EnrollFaceDialog';
import { Dialog, DialogContent } from '@/components/ui/dialog';
import { employeesApi, type Employee, type FaceEnrollmentSample } from '@/lib/api';
import { poseShortLabel } from '@/lib/face-enrollment';

export default function EmployeeFaceGalleryPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[400px]">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
        </div>
      }
    >
      <EmployeePage
        title="Face Enrollment"
        subtitle="Browse enrollment images for an employee. Re-enroll from here when angles need updating."
        icon={ScanFace}
      >
        {({ employee }) => <FaceGalleryBody employee={employee} />}
      </EmployeePage>
    </Suspense>
  );
}

function statusChip(sample: FaceEnrollmentSample): { label: string; className: string } {
  switch (sample.processStatus) {
    case 'validated':
    case 'embedded':
      return { label: 'Verified', className: 'bg-success/15 text-success border-success/30' };
    case 'rejected':
      return { label: 'Rejected', className: 'bg-destructive/15 text-destructive border-destructive/30' };
    case 'processing':
      return { label: 'Checking', className: 'bg-primary/15 text-primary border-primary/30' };
    default:
      return { label: sample.processStatus || 'Uploaded', className: 'bg-muted text-muted-foreground border-border' };
  }
}

function FaceGalleryBody({ employee }: { employee: Employee }) {
  const [enrollOpen, setEnrollOpen] = useState(false);
  const [enlarge, setEnlarge] = useState<FaceEnrollmentSample | null>(null);

  const query = useQuery({
    queryKey: ['face-profile-samples', employee.id],
    queryFn: () => employeesApi.getFaceProfileSamples(employee.id),
  });

  const samples = query.data?.samples ?? [];
  const job = query.data?.job;
  const empKey = employee.employeeId || employee.id;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="text-sm text-muted-foreground">
          {job ? (
            <>
              Latest job <span className="text-foreground font-medium">{job.status}</span>
              {job.errorCode ? ` · ${job.errorCode}` : ''}
              {samples.length ? ` · ${samples.length} image${samples.length === 1 ? '' : 's'}` : ''}
            </>
          ) : query.isLoading ? (
            'Loading…'
          ) : (
            'No enrollment job yet'
          )}
        </div>
        <button
          type="button"
          onClick={() => setEnrollOpen(true)}
          className="inline-flex items-center gap-2 px-3 py-2 rounded-lg bg-primary text-primary-foreground text-sm font-medium"
        >
          <ScanFace className="w-4 h-4" />
          {samples.length ? 'Enroll / Re-enroll' : 'Enroll Face'}
        </button>
      </div>

      {query.isLoading ? (
        <div className="flex justify-center py-16">
          <Loader2 className="w-7 h-7 animate-spin text-primary" />
        </div>
      ) : samples.length === 0 ? (
        <EmptyState
          icon={ScanFace}
          text="No face images yet — use Enroll Face to capture multi-angle photos. The server FaceWorker verifies each image."
        />
      ) : (
        <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 gap-3">
          {samples.map((s) => {
            const chip = statusChip(s);
            const src = s.imageUrl || employeesApi.faceSampleImagePath(empKey, s.id);
            return (
              <button
                key={s.id}
                type="button"
                onClick={() => setEnlarge(s)}
                className="group relative rounded-xl overflow-hidden border border-border aspect-square bg-muted/20 text-left"
              >
                {/* eslint-disable-next-line @next/next/no-img-element */}
                <img
                  src={src}
                  alt={poseShortLabel(s.poseId)}
                  className="w-full h-full object-cover scale-x-[-1]"
                />
                <span
                  className={`absolute top-2 left-2 text-[10px] font-medium px-1.5 py-0.5 rounded border ${chip.className}`}
                >
                  {chip.label}
                </span>
                <span className="absolute top-2 right-2 p-1 rounded bg-black/50 text-white opacity-0 group-hover:opacity-100 transition-opacity">
                  <Maximize2 className="w-3.5 h-3.5" />
                </span>
                <div className="absolute inset-x-0 bottom-0 bg-black/70 text-white px-2 py-1.5">
                  <div className="text-xs font-medium truncate">{poseShortLabel(s.poseId)}</div>
                  {s.processStatus === 'rejected' && s.rejectReason ? (
                    <div className="text-[10px] text-red-200 truncate">{s.rejectReason.replace(/_/g, ' ')}</div>
                  ) : null}
                </div>
              </button>
            );
          })}
        </div>
      )}

      <EnrollFaceDialog
        employee={employee}
        open={enrollOpen}
        onOpenChange={setEnrollOpen}
        onCompleted={() => {
          void query.refetch();
        }}
      />

      <Dialog open={enlarge !== null} onOpenChange={(o) => { if (!o) setEnlarge(null); }}>
        <DialogContent className="bg-card sm:max-w-lg p-0 overflow-hidden">
          {enlarge ? (
            <div className="space-y-0">
              {/* eslint-disable-next-line @next/next/no-img-element */}
              <img
                src={
                  enlarge.imageUrl ||
                  employeesApi.faceSampleImagePath(empKey, enlarge.id)
                }
                alt={poseShortLabel(enlarge.poseId)}
                className="w-full max-h-[70vh] object-contain bg-black scale-x-[-1]"
              />
              <div className="px-4 py-3 text-sm flex items-center justify-between gap-2">
                <span className="font-medium">{poseShortLabel(enlarge.poseId)}</span>
                <span className={`text-xs px-2 py-0.5 rounded border ${statusChip(enlarge).className}`}>
                  {statusChip(enlarge).label}
                </span>
              </div>
            </div>
          ) : null}
        </DialogContent>
      </Dialog>
    </div>
  );
}
