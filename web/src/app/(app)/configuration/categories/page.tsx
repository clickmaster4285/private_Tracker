'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Loader2, Edit2, Trash2, FolderTree, Tag, Palette } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { toast } from 'sonner';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { monitoringApi, type MonitoringType, type MonitoringCategory, type MonitoringCategoryKind } from '@/lib/api';

const KINDS: { value: MonitoringCategoryKind; label: string }[] = [
  { value: 'application', label: 'Applications' },
  { value: 'website', label: 'Websites' },
  { value: 'both', label: 'Both' },
];

const KIND_LABEL: Record<MonitoringCategoryKind, string> = {
  application: 'Applications',
  website: 'Websites',
  both: 'Both',
};

export default function CategoriesTypes() {
  const queryClient = useQueryClient();

  const categoriesQuery = useQuery({
    queryKey: ['monitoring-categories'],
    queryFn: () => monitoringApi.categories.list(),
  });
  const typesQuery = useQuery({
    queryKey: ['monitoring-types'],
    queryFn: () => monitoringApi.types.list(),
  });

  const categories = categoriesQuery.data?.categories ?? [];
  const types = typesQuery.data?.types ?? [];

  const [showAddCat, setShowAddCat] = useState(false);
  const [editCat, setEditCat] = useState<MonitoringCategory | null>(null);
  const [catName, setCatName] = useState('');
  const [catKind, setCatKind] = useState<MonitoringCategoryKind>('both');

  const createCat = useMutation({
    mutationFn: (data: { name: string; kind: MonitoringCategoryKind }) => monitoringApi.categories.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['monitoring-categories'] });
      toast.success('Category created');
      setShowAddCat(false);
      resetCatForm();
    },
    onError: (err: Error) => toast.error('Failed to create category', { description: err.message }),
  });

  const updateCat = useMutation({
    mutationFn: ({ id, data }: { id: number; data: { name: string; kind: MonitoringCategoryKind } }) =>
      monitoringApi.categories.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['monitoring-categories'] });
      toast.success('Category updated');
      setEditCat(null);
    },
    onError: (err: Error) => toast.error('Failed to update category', { description: err.message }),
  });

  const deleteCat = useMutation({
    mutationFn: (id: number) => monitoringApi.categories.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['monitoring-categories'] });
      toast.success('Category deleted');
    },
    onError: (err: Error) => toast.error('Failed to delete category', { description: err.message }),
  });

  const resetCatForm = () => {
    setCatName('');
    setCatKind('both');
  };

  const openAddCat = () => {
    resetCatForm();
    setShowAddCat(true);
  };

  const openEditCat = (c: MonitoringCategory) => {
    setEditCat(c);
    setCatName(c.name);
    setCatKind(c.kind);
  };

  const loading = categoriesQuery.isLoading || typesQuery.isLoading;

  if (loading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (categoriesQuery.error || typesQuery.error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <p className="text-destructive">
          Failed to load configuration: {((categoriesQuery.error ?? typesQuery.error) as Error).message}
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div>
        <h1 className="font-display font-bold text-2xl text-foreground">Categories & Types</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Productive / Unproductive / Neutral types define what the scorecard counts, and categories group apps and
          websites by department or use.
        </p>
      </div>

      <Tabs defaultValue="categories">
        <TabsList>
          <TabsTrigger value="categories" className="flex items-center gap-2">
            <FolderTree className="w-4 h-4" /> Categories
          </TabsTrigger>
          <TabsTrigger value="types" className="flex items-center gap-2">
            <Tag className="w-4 h-4" /> Types
          </TabsTrigger>
        </TabsList>

        {/* ── Categories tab ── */}
        <TabsContent value="categories">
          <div className="flex justify-between items-center">
            <p className="text-sm text-muted-foreground">{categories.length} categories</p>
            <button onClick={openAddCat} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity">
              <Plus className="w-4 h-4" /> Add Category
            </button>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 mt-4">
            {categories.map((cat, i) => (
              <motion.div
                key={cat.id}
                initial={{ opacity: 0, y: 10 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: i * 0.04 }}
                className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all group"
              >
                <div className="flex items-start justify-between mb-3">
                  <div className="w-10 h-10 rounded-lg bg-accent flex items-center justify-center">
                    <FolderTree className="w-5 h-5 text-accent-foreground" />
                  </div>
                  <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-all">
                    <button onClick={() => openEditCat(cat)} className="p-1.5 rounded hover:bg-muted transition-colors" aria-label={`Edit ${cat.name}`}>
                      <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                    </button>
                    <button
                      onClick={() => deleteCat.mutate(cat.id)}
                      disabled={deleteCat.isPending}
                      className="p-1.5 rounded hover:bg-muted transition-colors"
                      aria-label={`Delete ${cat.name}`}
                    >
                      <Trash2 className="w-3.5 h-3.5 text-destructive" />
                    </button>
                  </div>
                </div>
                <h3 className="font-display font-bold text-foreground mb-1">{cat.name}</h3>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-[11px] font-medium bg-accent text-accent-foreground">
                  {KIND_LABEL[cat.kind]}
                </span>
              </motion.div>
            ))}
          </div>
        </TabsContent>

        {/* ── Types tab ── */}
        <TabsContent value="types">
          <div className="flex justify-between items-center">
            <p className="text-sm text-muted-foreground">{types.length} types</p>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4 mt-4">
            {types.map((t, i) => (
              <motion.div
                key={t.id}
                initial={{ opacity: 0, y: 10 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ delay: i * 0.04 }}
                className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all"
              >
                <div className="flex items-start justify-between mb-3">
                  <div className="w-10 h-10 rounded-lg flex items-center justify-center" style={{ backgroundColor: `${t.color || '#7C3AED'}22`, color: t.color || '#7C3AED' }}>
                    <Palette className="w-5 h-5" />
                  </div>
                </div>
                <h3 className="font-display font-bold text-foreground mb-1 flex items-center gap-2">
                  <span className="w-3 h-3 rounded-full inline-block shrink-0" style={{ backgroundColor: t.color || '#7C3AED' }} />
                  {t.name}
                </h3>
                <p className="text-sm text-muted-foreground">{t.description}</p>
              </motion.div>
            ))}
          </div>
        </TabsContent>
      </Tabs>

      {/* Add Category Dialog */}
      <Dialog open={showAddCat} onOpenChange={setShowAddCat}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader><DialogTitle className="font-display">Add Category</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={catName}
              onChange={e => setCatName(e.target.value)}
              placeholder="Category Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={catKind}
              onChange={e => setCatKind(e.target.value as MonitoringCategoryKind)}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {KINDS.map(k => <option key={k.value} value={k.value}>{k.label}</option>)}
            </select>
            <button
              onClick={() => catName.trim() && createCat.mutate({ name: catName.trim(), kind: catKind })}
              disabled={createCat.isPending || !catName.trim()}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {createCat.isPending ? 'Adding...' : 'Add Category'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Edit Category Dialog */}
      <Dialog open={editCat !== null} onOpenChange={(open) => { if (!open) setEditCat(null); }}>
        <DialogContent className="bg-card sm:max-w-[425px]">
          <DialogHeader><DialogTitle className="font-display">Edit Category</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={catName}
              onChange={e => setCatName(e.target.value)}
              placeholder="Category Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <select
              value={catKind}
              onChange={e => setCatKind(e.target.value as MonitoringCategoryKind)}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            >
              {KINDS.map(k => <option key={k.value} value={k.value}>{k.label}</option>)}
            </select>
            <button
              onClick={() => editCat && catName.trim() && updateCat.mutate({ id: editCat.id, data: { name: catName.trim(), kind: catKind } })}
              disabled={updateCat.isPending || !catName.trim()}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {updateCat.isPending ? 'Saving...' : 'Save Changes'}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
