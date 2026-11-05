'use client';

import { Suspense, useEffect, useState } from 'react';
import { Globe, Plus, Loader2, X, AlertCircle } from 'lucide-react';
import { toast } from 'sonner';
import { useQueryClient } from '@tanstack/react-query';
import ClassifiedItemsTable, { type ClassifiedItemRow } from '@/components/configuration/ClassifiedItemsTable';
import { monitoringApi, type MonitoredSite, type MonitoringType, type MonitoringCategory } from '@/lib/api';

function AddWebsiteDialog({ open, onOpenChange, onCreated }: { open: boolean; onOpenChange: (v: boolean) => void; onCreated: () => void }) {
  const [domain, setDomain] = useState('');
  const [typeId, setTypeId] = useState('');
  const [categoryId, setCategoryId] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [checking, setChecking] = useState(false);
  const [exists, setExists] = useState(false);
  const [types, setTypes] = useState<MonitoringType[]>([]);
  const [categories, setCategories] = useState<MonitoringCategory[]>([]);
  const queryClient = useQueryClient();

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    const load = async () => {
      try {
        const [t, c] = await Promise.all([
          monitoringApi.types.list(),
          monitoringApi.categories.list(),
        ]);
        if (!cancelled) {
          setTypes(t.types);
          setCategories(c.categories);
        }
      } catch { /* ignore */ }
    };
    load();
    return () => { cancelled = true; };
  }, [open]);

  // Auto-normalize domain as user types: strip protocol, path, lowercase.
  const handleDomainChange = (value: string) => {
    let normalized = value.trim().toLowerCase();
    normalized = normalized.replace(/^https?:\/\//, '').replace(/^\/\//, '');
    const idx = normalized.indexOf('/');
    if (idx >= 0) {
      normalized = normalized.slice(0, idx);
    }
    normalized = normalized.replace(/\?.*$/, '').replace(/#.*$/, '');
    normalized = normalized.replace(/^www\./, '');
    setDomain(normalized);
    setExists(false);
  };

  // Check if domain already exists in the list.
  const checkExists = async (value: string) => {
    const trimmed = value.trim().toLowerCase().replace(/^www\./, '');
    if (trimmed.length < 3) { setExists(false); return; }
    setChecking(true);
    try {
      const result = await monitoringApi.websites.list({ search: trimmed, perPage: 1 });
      const match = result.data.find(s => s.domain.toLowerCase() === trimmed);
      setExists(!!match);
    } catch { setExists(false); } finally { setChecking(false); }
  };

  const handleBlur = () => { if (domain) checkExists(domain); };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    const trimmed = domain.trim().replace(/^www\./, '');
    if (!trimmed) {
      toast.error('Please enter a valid domain');
      return;
    }
    if (exists) {
      toast.error('This website already exists in the registry');
      return;
    }
    setSubmitting(true);
    try {
      await monitoringApi.websites.create({
        domain: trimmed,
        typeId: typeId ? Number(typeId) : null,
        categoryId: categoryId ? Number(categoryId) : null,
      });
      toast.success('Website added successfully');
      setDomain('');
      setTypeId('');
      setCategoryId('');
      setExists(false);
      onOpenChange(false);
      onCreated();
    } catch (err) {
      toast.error('Failed to add website', { description: err instanceof Error ? err.message : 'Unknown error' });
    } finally {
      setSubmitting(false);
    }
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
      <div className="fixed inset-0 bg-black/50 backdrop-blur-sm" onClick={() => onOpenChange(false)} />
      <div className="bg-card rounded-xl border border-border shadow-xl w-full max-w-md animate-fade-in relative">
        <div className="px-6 py-5 border-b border-border flex items-center justify-between">
          <div>
            <h2 className="font-display font-bold text-lg text-foreground">Add Website</h2>
            <p className="text-sm text-muted-foreground mt-1">Manually add a website to the monitoring registry.</p>
          </div>
          <button onClick={() => onOpenChange(false)} className="text-muted-foreground hover:text-foreground transition-colors">
            <X className="w-5 h-5" />
          </button>
        </div>
        <form onSubmit={handleSubmit} className="p-6 space-y-4">
          <div>
            <label className="block text-sm font-medium text-foreground mb-1.5">Domain</label>
            <div className="relative">
              <input
                type="text"
                value={domain}
                onChange={e => handleDomainChange(e.target.value)}
                onBlur={handleBlur}
                placeholder="e.g. github.com"
                className={`w-full bg-background border rounded-lg px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground focus:ring-1 transition-colors ${exists ? 'border-destructive focus:border-destructive focus:ring-destructive/20' : 'border-border focus:border-primary focus:ring-primary/20'}`}
                autoFocus
              />
              {checking && <Loader2 className="absolute right-3 top-2.5 w-4 h-4 animate-spin text-muted-foreground" />}
            </div>
            <div className="flex items-center justify-between mt-1.5">
              <p className="text-xs text-muted-foreground">Protocol and paths are removed automatically.</p>
              {exists && (
                <span className="flex items-center gap-1 text-xs text-destructive">
                  <AlertCircle className="w-3 h-3" />
                  Already exists
                </span>
              )}
            </div>
          </div>
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-foreground mb-1.5">Type</label>
              <select
                value={typeId}
                onChange={e => setTypeId(e.target.value)}
                className="w-full bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground"
              >
                <option value="">None</option>
                {types.map(t => (
                  <option key={t.id} value={t.id}>
                    <span className="inline-flex items-center gap-1.5">
                      <span className="w-2 h-2 rounded-full inline-block" style={{ backgroundColor: t.color || '#888' }} />
                      {t.name}
                    </span>
                  </option>
                ))}
              </select>
            </div>
            <div>
              <label className="block text-sm font-medium text-foreground mb-1.5">Category</label>
              <select
                value={categoryId}
                onChange={e => setCategoryId(e.target.value)}
                className="w-full bg-background border border-border rounded-lg px-3 py-2 text-sm text-foreground"
              >
                <option value="">None</option>
                {categories.map(c => (
                  <option key={c.id} value={c.id}>{c.name}</option>
                ))}
              </select>
            </div>
          </div>
          <div className="flex justify-end gap-3 pt-2">
            <button
              type="button"
              onClick={() => onOpenChange(false)}
              className="px-4 py-2 text-sm font-medium text-muted-foreground hover:text-foreground transition-colors"
            >
              Cancel
            </button>
            <button
              type="submit"
              disabled={submitting || exists}
              className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-lg text-sm font-medium hover:bg-primary/90 disabled:opacity-50 transition-colors"
            >
              {submitting && <Loader2 className="w-4 h-4 animate-spin" />}
              Add Website
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

export default function WebsitesClassification() {
  const [dialogOpen, setDialogOpen] = useState(false);
  const queryClient = useQueryClient();

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['monitoring-websites'] });
  };

  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <ClassifiedItemsTable<MonitoredSite & ClassifiedItemRow>
        title="Websites"
        description="Classify the websites employees visit. Domains are auto-discovered from observed browsing activity — nothing here is static."
        queryKeyPrefix="monitoring-websites"
        scope="website"
        nameHeader="Domain"
        nameOf={(item) => item.domain}
        listFn={(params) => monitoringApi.websites.list(params)}
        classifyFn={(id, payload) => monitoringApi.websites.classify(Number(id), payload)}
        createButton={
          <button
            onClick={() => setDialogOpen(true)}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-lg text-sm font-medium hover:bg-primary/90 transition-colors shadow-sm"
          >
            <Plus className="w-4 h-4" />
            Add Website
          </button>
        }
      />
      <AddWebsiteDialog open={dialogOpen} onOpenChange={setDialogOpen} onCreated={refresh} />
    </Suspense>
  );
}
