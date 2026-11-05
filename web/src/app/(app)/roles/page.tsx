'use client';

import { useEffect, useMemo, useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, Shield, ShieldCheck, Lock, Edit2, Trash2, Loader2, CheckSquare, Square, MinusSquare, Users } from 'lucide-react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { toast } from 'sonner';
import { modulesApi, rolesApi, type ModuleNode, type Role } from '@/lib/api';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';

// ─── Module/submodule selection matrix ───────────────────────────────────────

function ModuleMatrix({
  modules,
  selected,
  onToggleSubmodule,
  onToggleModule,
}: {
  modules: ModuleNode[];
  selected: Set<number>;
  onToggleSubmodule: (id: number) => void;
  onToggleModule: (module: ModuleNode) => void;
}) {
  return (
    <div className="space-y-3">
      {modules.map(module => {
        const total = module.submodules.length;
        const chosen = module.submodules.filter(s => selected.has(s.id)).length;
        const allSelected = total > 0 && chosen === total;

        return (
          <div key={module.id} className="border border-border rounded-xl bg-background/40 overflow-hidden">
            <div className="flex items-center justify-between px-4 py-2.5 bg-muted/30 border-b border-border">
              <button
                type="button"
                onClick={() => onToggleModule(module)}
                className="flex items-center gap-2 text-sm font-semibold text-foreground hover:text-primary transition-colors"
              >
                {allSelected ? (
                  <CheckSquare className="w-4 h-4 text-primary" />
                ) : chosen > 0 ? (
                  <MinusSquare className="w-4 h-4 text-primary" />
                ) : (
                  <Square className="w-4 h-4 text-muted-foreground" />
                )}
                {module.name}
              </button>
              <span className="text-[11px] font-medium px-2 py-0.5 rounded-full bg-accent text-accent-foreground">
                {chosen}/{total} selected
              </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-1.5 p-3">
              {module.submodules.map(sub => {
                const active = selected.has(sub.id);
                return (
                  <button
                    key={sub.id}
                    type="button"
                    onClick={() => onToggleSubmodule(sub.id)}
                    aria-pressed={active}
                    className={`flex items-center gap-2 px-2.5 py-1.5 rounded-lg border text-left text-xs transition-all ${
                      active
                        ? 'border-primary/60 bg-primary/10 text-foreground font-medium'
                        : 'border-border bg-card text-muted-foreground hover:border-primary/30 hover:text-foreground'
                    }`}
                  >
                    <span
                      className={`w-3.5 h-3.5 rounded flex items-center justify-center shrink-0 ${
                        active ? 'gradient-primary' : 'border border-border bg-background'
                      }`}
                    >
                      {active && (
                        <svg viewBox="0 0 12 12" className="w-2.5 h-2.5 text-primary-foreground" fill="none">
                          <path d="M2.5 6.5L4.8 8.8L9.5 3.5" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
                        </svg>
                      )}
                    </span>
                    <span className="truncate">{sub.name}</span>
                  </button>
                );
              })}
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ─── Page ─────────────────────────────────────────────────────────────────────

export default function RolesPage() {
  const queryClient = useQueryClient();

  const [showDialog, setShowDialog] = useState(false);
  const [editingRole, setEditingRole] = useState<Role | null>(null);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [selected, setSelected] = useState<Set<number>>(new Set());

  const { data: treeData, isLoading: treeLoading } = useQuery({
    queryKey: ['module-tree'],
    queryFn: () => modulesApi.tree(),
  });

  const { data: rolesData, isLoading: rolesLoading } = useQuery({
    queryKey: ['roles'],
    queryFn: () => rolesApi.list(),
  });

  const modules = treeData?.modules ?? [];
  const roles = rolesData?.roles ?? [];
  const totalSubmodules = treeData?.total ?? 0;

  const saveMutation = useMutation({
    mutationFn: async () => {
      if (editingRole) {
        return rolesApi.update(editingRole.id, {
          name,
          description,
          submoduleIds: Array.from(selected),
        });
      }
      return rolesApi.create({ name, description, submoduleIds: Array.from(selected) });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['roles'] });
      toast.success(editingRole ? 'Role updated' : 'Role created', {
        description: `"${name}" now has ${selected.size} of ${totalSubmodules} access entries.`,
      });
      closeDialog();
    },
    onError: (err: Error) => {
      toast.error(editingRole ? 'Failed to update role' : 'Failed to create role', { description: err.message });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => rolesApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['roles'] });
      toast.success('Role deleted');
    },
    onError: (err: Error) => {
      toast.error('Cannot delete role', { description: err.message });
    },
  });

  const closeDialog = () => {
    setShowDialog(false);
    setEditingRole(null);
    setName('');
    setDescription('');
    setSelected(new Set());
  };

  const openCreate = () => {
    setEditingRole(null);
    setName('');
    setDescription('');
    setSelected(new Set());
    setShowDialog(true);
  };

  const openEdit = (role: Role) => {
    setEditingRole(role);
    setName(role.name);
    setDescription(role.description);
    setSelected(new Set(role.submoduleIds));
    setShowDialog(true);
  };

  const toggleSubmodule = (id: number) => {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleModule = (module: ModuleNode) => {
    setSelected(prev => {
      const next = new Set(prev);
      const allSelected = module.submodules.every(s => next.has(s.id));
      module.submodules.forEach(s => (allSelected ? next.delete(s.id) : next.add(s.id)));
      return next;
    });
  };

  // Granted module-group labels per role card (computed from the catalog).
  const grantedGroupsOf = useMemo(
    () =>
      new Map<number, string[]>(
        roles.map(role => {
          const groups = modules
            .filter(m => m.submodules.some(s => role.permissions.includes(s.key)))
            .map(m => m.name);
          return [role.id, groups];
        }),
      ),
    [roles, modules],
  );

  const isLoading = treeLoading || rolesLoading;

  // ── Loading state ──
  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="w-8 h-8 animate-spin text-primary" />
          <p className="text-sm text-muted-foreground">Loading roles &amp; modules...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <p className="text-sm text-muted-foreground">
          {roles.length} role{roles.length === 1 ? '' : 's'} configured across{' '}
          {modules.length} modules · {totalSubmodules} submodules
        </p>
        <button
          onClick={openCreate}
          className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90"
        >
          <Plus className="w-4 h-4" /> Add Role
        </button>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {roles.map((role, i) => {
          const groups = grantedGroupsOf.get(role.id) ?? [];
          return (
            <motion.div
              key={role.id}
              initial={{ opacity: 0, y: 10 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ delay: i * 0.05 }}
              className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all group"
            >
              <div className="flex items-start justify-between mb-3">
                <div className={`w-10 h-10 rounded-lg flex items-center justify-center ${role.isSystem ? 'bg-success/15' : 'gradient-primary'}`}>
                  {role.isSystem ? (
                    <ShieldCheck className="w-5 h-5 text-success" />
                  ) : (
                    <Shield className="w-5 h-5 text-primary-foreground" />
                  )}
                </div>
                <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                  {!role.isSystem && (
                    <>
                      <button
                        onClick={() => openEdit(role)}
                        title="Edit role"
                        className="p-1 rounded hover:bg-muted"
                      >
                        <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                      </button>
                      <button
                        onClick={() => deleteMutation.mutate(role.id)}
                        disabled={deleteMutation.isPending}
                        title="Delete role"
                        className="p-1 rounded hover:bg-muted disabled:opacity-50"
                      >
                        <Trash2 className="w-3.5 h-3.5 text-destructive" />
                      </button>
                    </>
                  )}
                  {role.isSystem && (
                    <span className="flex items-center gap-1 text-[10px] font-semibold uppercase tracking-wide text-muted-foreground px-2 py-1 rounded-full bg-muted">
                      <Lock className="w-3 h-3" /> System
                    </span>
                  )}
                </div>
              </div>

              <h3 className="font-display font-bold text-foreground mb-1 truncate">{role.name}</h3>
              <p className="text-xs text-muted-foreground mb-3 line-clamp-2 min-h-[2rem]">
                {role.description || '—'}
              </p>

              <div className="flex flex-wrap gap-1 mb-3">
                <span className="px-2 py-0.5 rounded-full text-[10px] bg-primary/10 text-primary font-semibold">
                  {role.permissions.length}/{totalSubmodules} accesses
                </span>
                {groups.slice(0, 2).map(g => (
                  <span key={g} className="px-2 py-0.5 rounded-full text-[10px] bg-accent text-accent-foreground font-medium">
                    {g}
                  </span>
                ))}
                {groups.length > 2 && (
                  <span className="px-2 py-0.5 rounded-full text-[10px] bg-muted text-muted-foreground">
                    +{groups.length - 2}
                  </span>
                )}
              </div>

              <p className="text-xs text-muted-foreground flex items-center gap-1">
                <Users className="w-3 h-3" /> {role.userCount} user{role.userCount === 1 ? '' : 's'}
              </p>
            </motion.div>
          );
        })}
      </div>

      {/* Create / Edit dialog */}
      <Dialog open={showDialog} onOpenChange={(open) => { if (!open) closeDialog(); }}>
        <DialogContent className="bg-card max-w-2xl flex flex-col max-h-[85vh]">
          <DialogHeader>
            <DialogTitle className="font-display">
              {editingRole ? `Edit Role — ${editingRole.name}` : 'Add Role'}
            </DialogTitle>
          </DialogHeader>

          <div className="space-y-3 mt-1">
            <input
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="Role Name"
              disabled={!!editingRole?.isSystem}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground disabled:opacity-60"
            />
            <input
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Description"
              disabled={!!editingRole?.isSystem}
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground disabled:opacity-60"
            />

            <div>
              <div className="flex items-center justify-between mb-2">
                <p className="text-sm font-medium text-foreground">
                  Modules &amp; Submodules{' '}
                  <span className="text-muted-foreground font-normal">
                    ({selected.size}/{totalSubmodules})
                  </span>
                </p>
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => setSelected(new Set(modules.flatMap(m => m.submodules.map(s => s.id))))}
                    className="text-xs text-primary hover:underline"
                  >
                    Select all
                  </button>
                  <span className="text-xs text-border">|</span>
                  <button
                    type="button"
                    onClick={() => setSelected(new Set())}
                    className="text-xs text-muted-foreground hover:text-destructive hover:underline"
                  >
                    Clear
                  </button>
                </div>
              </div>
              <div className="max-h-[42vh] overflow-y-auto pr-1 space-y-3">
                <ModuleMatrix
                  modules={modules}
                  selected={selected}
                  onToggleSubmodule={toggleSubmodule}
                  onToggleModule={toggleModule}
                />
              </div>
            </div>

            <button
              onClick={() => {
                if (!name.trim()) {
                  toast.error('Validation error', { description: 'Role name is required.' });
                  return;
                }
                saveMutation.mutate();
              }}
              disabled={saveMutation.isPending || !!editingRole?.isSystem}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 disabled:opacity-50"
            >
              {saveMutation.isPending
                ? 'Saving...'
                : editingRole
                  ? 'Save Changes'
                  : 'Create Role'}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
