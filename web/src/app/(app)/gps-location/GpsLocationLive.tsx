'use client';

import { useEffect, useMemo, useRef, useState } from 'react';
import { motion } from 'framer-motion';
import { Loader2, MapPin, Navigation, Pencil, Plus, Trash2 } from 'lucide-react';
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { geofenceApi, locationSamplesApi, type GeofenceZone } from '@/lib/api';
import { formatDateTime } from '@/lib/format';
import EmptyState from '@/components/employees/EmptyState';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';

const SOURCE_LABEL: Record<string, string> = {
  gps: 'GPS',
  wifi: 'WiFi',
  ip: 'IP (approx)',
  manual: 'Manual',
};

function sourceBadgeClass(source: string): string {
  if (source === 'gps' || source === 'wifi') return 'bg-success/15 text-success';
  if (source === 'ip') return 'bg-warning/15 text-warning';
  return 'bg-muted text-muted-foreground';
}

function geofenceBadgeClass(status?: string): string {
  if (status?.startsWith('Inside')) return 'bg-success/15 text-success';
  return 'bg-muted text-muted-foreground';
}

type ZoneForm = {
  name: string;
  latitude: string;
  longitude: string;
  radiusM: string;
  alertOnExit: boolean;
};

const emptyForm = (): ZoneForm => ({
  name: '',
  latitude: '',
  longitude: '',
  radiusM: '200',
  alertOnExit: true,
});

function zoneToForm(z: GeofenceZone): ZoneForm {
  return {
    name: z.name,
    latitude: String(z.latitude),
    longitude: String(z.longitude),
    radiusM: String(z.radiusM),
    alertOnExit: z.alertOnExit,
  };
}

export default function GpsLocationLivePage() {
  const sentinelRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();
  const [showAdd, setShowAdd] = useState(false);
  const [editZone, setEditZone] = useState<GeofenceZone | null>(null);
  const [form, setForm] = useState<ZoneForm>(emptyForm());

  const {
    data,
    isLoading,
    isError,
    error,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ['location-samples'],
    queryFn: ({ pageParam }) =>
      locationSamplesApi.list({ page: pageParam, perPage: 30 }),
    initialPageParam: 1,
    getNextPageParam: (last) =>
      last.page < last.totalPages ? last.page + 1 : undefined,
  });

  const { data: zonesData, isLoading: zonesLoading } = useQuery({
    queryKey: ['geofence-zones'],
    queryFn: () => geofenceApi.list(),
  });

  const createMutation = useMutation({
    mutationFn: geofenceApi.create,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['geofence-zones'] });
      queryClient.invalidateQueries({ queryKey: ['location-samples'] });
      setShowAdd(false);
      setForm(emptyForm());
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, body }: { id: number; body: Parameters<typeof geofenceApi.update>[1] }) =>
      geofenceApi.update(id, body),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['geofence-zones'] });
      queryClient.invalidateQueries({ queryKey: ['location-samples'] });
      setEditZone(null);
    },
  });

  const deleteMutation = useMutation({
    mutationFn: geofenceApi.delete,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['geofence-zones'] });
      queryClient.invalidateQueries({ queryKey: ['location-samples'] });
    },
  });

  const rows = useMemo(
    () => data?.pages.flatMap(p => p.data) ?? [],
    [data],
  );

  const zones = zonesData?.data ?? [];
  const total = data?.pages[0]?.total ?? 0;

  useEffect(() => {
    const el = sentinelRef.current;
    if (!el || !hasNextPage || isFetchingNextPage) return;
    const obs = new IntersectionObserver(
      entries => {
        if (entries[0]?.isIntersecting) fetchNextPage();
      },
      { rootMargin: '300px' },
    );
    obs.observe(el);
    return () => obs.disconnect();
  }, [fetchNextPage, hasNextPage, isFetchingNextPage]);

  const parseForm = () => {
    const latitude = parseFloat(form.latitude);
    const longitude = parseFloat(form.longitude);
    const radiusM = parseFloat(form.radiusM);
    if (!form.name.trim() || Number.isNaN(latitude) || Number.isNaN(longitude) || Number.isNaN(radiusM)) {
      return null;
    }
    return {
      name: form.name.trim(),
      latitude,
      longitude,
      radiusM,
      alertOnExit: form.alertOnExit,
    };
  };

  const openEdit = (zone: GeofenceZone) => {
    setEditZone(zone);
    setForm(zoneToForm(zone));
  };

  const ZoneFormFields = () => (
    <div className="space-y-4">
      <div>
        <Label htmlFor="zone-name">Name</Label>
        <Input
          id="zone-name"
          value={form.name}
          onChange={e => setForm(f => ({ ...f, name: e.target.value }))}
          placeholder="Office HQ"
        />
      </div>
      <div className="grid grid-cols-2 gap-3">
        <div>
          <Label htmlFor="zone-lat">Latitude</Label>
          <Input
            id="zone-lat"
            value={form.latitude}
            onChange={e => setForm(f => ({ ...f, latitude: e.target.value }))}
            placeholder="24.8607"
          />
        </div>
        <div>
          <Label htmlFor="zone-lon">Longitude</Label>
          <Input
            id="zone-lon"
            value={form.longitude}
            onChange={e => setForm(f => ({ ...f, longitude: e.target.value }))}
            placeholder="67.0011"
          />
        </div>
      </div>
      <div>
        <Label htmlFor="zone-radius">Radius (metres)</Label>
        <Input
          id="zone-radius"
          value={form.radiusM}
          onChange={e => setForm(f => ({ ...f, radiusM: e.target.value }))}
          placeholder="200"
        />
      </div>
      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox"
          checked={form.alertOnExit}
          onChange={e => setForm(f => ({ ...f, alertOnExit: e.target.checked }))}
        />
        Alert on exit
      </label>
    </div>
  );

  return (
    <div className="space-y-6 animate-fade-in">
      <div>
        <h3 className="font-display font-bold text-lg text-foreground mb-4">Location Log</h3>
        {isLoading ? (
          <div className="flex items-center justify-center py-16 text-muted-foreground gap-2">
            <Loader2 className="w-5 h-5 animate-spin" />
            Loading location samples…
          </div>
        ) : isError ? (
          <EmptyState
            icon={MapPin}
            text={`Failed to load location data: ${error instanceof Error ? error.message : 'Unknown error'}`}
          />
        ) : rows.length === 0 ? (
          <EmptyState
            icon={MapPin}
            text="No employees have shared location yet. Location appears here after an employee grants location permission on their desktop client."
          />
        ) : (
          <div className="bg-card rounded-xl border border-border overflow-x-auto">
            <table className="w-full min-w-[800px]">
              <thead>
                <tr className="border-b border-border">
                  {['Employee', 'Timestamp', 'Location', 'Coordinates', 'Source', 'Geofence'].map(h => (
                    <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {rows.map((l, i) => (
                  <motion.tr
                    key={l.id}
                    initial={{ opacity: 0 }}
                    animate={{ opacity: 1 }}
                    transition={{ delay: Math.min(i * 0.02, 0.3) }}
                    className="border-b border-border last:border-0 hover:bg-muted/30"
                  >
                    <td className="px-4 py-3 text-sm font-medium text-foreground">
                      {l.employeeName || l.employeeId}
                    </td>
                    <td className="px-4 py-3 text-sm text-muted-foreground">
                      {formatDateTime(l.capturedAt)}
                    </td>
                    <td className="px-4 py-3 text-sm text-foreground">
                      <span className="inline-flex items-center gap-1">
                        <MapPin className="w-3.5 h-3.5 text-primary shrink-0" />
                        {l.address || '—'}
                      </span>
                    </td>
                    <td className="px-4 py-3 text-xs font-mono text-muted-foreground">
                      {l.latitude.toFixed(5)}, {l.longitude.toFixed(5)}
                      {l.accuracyM != null ? ` (±${Math.round(l.accuracyM)}m)` : ''}
                    </td>
                    <td className="px-4 py-3">
                      <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${sourceBadgeClass(l.source)}`}>
                        {SOURCE_LABEL[l.source] ?? l.source}
                      </span>
                    </td>
                    <td className="px-4 py-3">
                      <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${geofenceBadgeClass(l.geofenceStatus)}`}>
                        {l.geofenceStatus || 'Outside'}
                      </span>
                    </td>
                  </motion.tr>
                ))}
              </tbody>
            </table>
            <div ref={sentinelRef} className="h-1" />
            {isFetchingNextPage && (
              <p className="text-center text-sm text-muted-foreground py-3">Loading more…</p>
            )}
            {!hasNextPage && rows.length > 0 && (
              <p className="text-center text-sm text-muted-foreground py-3">Showing all {total}</p>
            )}
          </div>
        )}
      </div>

      <div>
        <div className="flex items-center justify-between mb-4">
          <h3 className="font-display font-bold text-lg text-foreground">Geofence Zones</h3>
          <Button size="sm" onClick={() => { setForm(emptyForm()); setShowAdd(true); }}>
            <Plus className="w-4 h-4 mr-1" />
            Add Zone
          </Button>
        </div>
        {zonesLoading ? (
          <div className="flex items-center justify-center py-12 text-muted-foreground gap-2">
            <Loader2 className="w-5 h-5 animate-spin" />
            Loading zones…
          </div>
        ) : zones.length === 0 ? (
          <div className="bg-card rounded-xl border border-border p-8">
            <EmptyState
              icon={Navigation}
              text="No geofence zones yet. Add a zone to evaluate inside/outside status when location samples arrive."
            />
          </div>
        ) : (
          <div className="bg-card rounded-xl border border-border overflow-x-auto">
            <table className="w-full min-w-[640px]">
              <thead>
                <tr className="border-b border-border">
                  {['Name', 'Center', 'Radius', 'Exit alerts', 'Actions'].map(h => (
                    <th key={h} className="text-left px-4 py-3 text-sm font-semibold text-muted-foreground">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {zones.map(z => (
                  <tr key={z.id} className="border-b border-border last:border-0 hover:bg-muted/30">
                    <td className="px-4 py-3 text-sm font-medium">{z.name}</td>
                    <td className="px-4 py-3 text-xs font-mono text-muted-foreground">
                      {z.latitude.toFixed(5)}, {z.longitude.toFixed(5)}
                    </td>
                    <td className="px-4 py-3 text-sm">{Math.round(z.radiusM)} m</td>
                    <td className="px-4 py-3 text-sm">{z.alertOnExit ? 'Yes' : 'No'}</td>
                    <td className="px-4 py-3">
                      <div className="flex gap-2">
                        <Button variant="ghost" size="sm" onClick={() => openEdit(z)}>
                          <Pencil className="w-4 h-4" />
                        </Button>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="text-destructive"
                          onClick={() => deleteMutation.mutate(z.id)}
                          disabled={deleteMutation.isPending}
                        >
                          <Trash2 className="w-4 h-4" />
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader>
            <DialogTitle className="font-display">Add Geofence Zone</DialogTitle>
          </DialogHeader>
          <ZoneFormFields />
          <Button
            className="mt-2"
            disabled={createMutation.isPending}
            onClick={() => {
              const body = parseForm();
              if (body) createMutation.mutate(body);
            }}
          >
            {createMutation.isPending ? 'Saving…' : 'Create Zone'}
          </Button>
        </DialogContent>
      </Dialog>

      <Dialog open={!!editZone} onOpenChange={open => { if (!open) setEditZone(null); }}>
        <DialogContent className="bg-card">
          <DialogHeader>
            <DialogTitle className="font-display">Edit Geofence Zone</DialogTitle>
          </DialogHeader>
          <ZoneFormFields />
          <Button
            className="mt-2"
            disabled={updateMutation.isPending}
            onClick={() => {
              const body = parseForm();
              if (body && editZone) updateMutation.mutate({ id: editZone.id, body });
            }}
          >
            {updateMutation.isPending ? 'Saving…' : 'Save Changes'}
          </Button>
        </DialogContent>
      </Dialog>
    </div>
  );
}
