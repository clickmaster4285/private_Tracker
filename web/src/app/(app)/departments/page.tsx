'use client';

import { useState } from 'react';
import { motion } from 'framer-motion';
import { Plus, MoreVertical, Users, Building2, Loader2, Edit2, Trash2 } from 'lucide-react';
import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { toast } from 'sonner';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { departmentsApi, type Department } from '@/lib/api';

export default function Departments() {
  const queryClient = useQueryClient();
  const [showAdd, setShowAdd] = useState(false);
  const [newName, setNewName] = useState('');
  const [editDept, setEditDept] = useState<Department | null>(null);
  const [editName, setEditName] = useState('');

  const { data: deptResponse, isLoading, error } = useQuery({
    queryKey: ['departments'],
    queryFn: () => departmentsApi.list(),
  });

  const departments = deptResponse?.departments || [];

  const createMutation = useMutation({
    mutationFn: (name: string) => departmentsApi.create(name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['departments'] });
      toast.success('Department created!');
      setShowAdd(false);
      setNewName('');
    },
    onError: (err: Error) => toast.error('Failed to create department', { description: err.message }),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, name }: { id: number; name: string }) => departmentsApi.update(id, name),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['departments'] });
      toast.success('Department updated!');
      setEditDept(null);
    },
    onError: (err: Error) => toast.error('Failed to update department', { description: err.message }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: number) => departmentsApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['departments'] });
      toast.success('Department removed');
    },
    onError: (err: Error) => toast.error('Failed to delete department', { description: err.message }),
  });

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <Loader2 className="w-8 h-8 animate-spin text-primary" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <p className="text-destructive">Failed to load departments: {(error as Error).message}</p>
      </div>
    );
  }

  return (
    <div className="space-y-4 animate-fade-in">
      <div className="flex justify-between items-center">
        <p className="text-sm text-muted-foreground">{departments.length} departments</p>
        <button onClick={() => setShowAdd(true)} className="gradient-primary text-primary-foreground px-4 py-2 rounded-lg text-sm font-medium flex items-center gap-2 hover:opacity-90 transition-opacity">
          <Plus className="w-4 h-4" /> Add Department
        </button>
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-4">
        {departments.map((dept, i) => (
          <motion.div
            key={dept.id}
            initial={{ opacity: 0, y: 10 }}
            animate={{ opacity: 1, y: 0 }}
            transition={{ delay: i * 0.04 }}
            className="bg-card rounded-xl border border-border p-5 shadow-card hover:shadow-card-hover transition-all group"
          >
            <div className="flex items-start justify-between mb-3">
              <div className="w-10 h-10 rounded-lg bg-accent flex items-center justify-center">
                <Building2 className="w-5 h-5 text-accent-foreground" />
              </div>
              <div className="flex gap-1 opacity-0 group-hover:opacity-100 transition-all">
                <button
                  onClick={() => { setEditDept(dept); setEditName(dept.name); }}
                  className="p-1.5 rounded hover:bg-muted transition-colors"
                >
                  <Edit2 className="w-3.5 h-3.5 text-muted-foreground" />
                </button>
                <button
                  onClick={() => deleteMutation.mutate(dept.id)}
                  className="p-1.5 rounded hover:bg-muted transition-colors"
                  disabled={deleteMutation.isPending}
                >
                  <Trash2 className="w-3.5 h-3.5 text-destructive" />
                </button>
              </div>
            </div>
            <h3 className="font-display font-bold text-foreground mb-1">{dept.name}</h3>
            <div className="flex items-center gap-1 text-xs text-muted-foreground">
              <Users className="w-3.5 h-3.5" />
              <span>{dept.employeeCount} employees</span>
            </div>
          </motion.div>
        ))}
      </div>

      {/* Add Dialog */}
      <Dialog open={showAdd} onOpenChange={setShowAdd}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Add Department</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={newName}
              onChange={e => setNewName(e.target.value)}
              placeholder="Department Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <button
              onClick={() => newName && createMutation.mutate(newName)}
              disabled={createMutation.isPending || !newName}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {createMutation.isPending ? 'Adding...' : 'Add Department'}
            </button>
          </div>
        </DialogContent>
      </Dialog>

      {/* Edit Dialog */}
      <Dialog open={!!editDept} onOpenChange={(open) => { if (!open) setEditDept(null); }}>
        <DialogContent className="bg-card">
          <DialogHeader><DialogTitle className="font-display">Edit Department</DialogTitle></DialogHeader>
          <div className="space-y-3 mt-2">
            <input
              value={editName}
              onChange={e => setEditName(e.target.value)}
              placeholder="Department Name"
              className="w-full border border-border rounded-lg px-3 py-2 text-sm bg-background text-foreground"
            />
            <button
              onClick={() => editDept && updateMutation.mutate({ id: editDept.id, name: editName })}
              disabled={updateMutation.isPending || !editName}
              className="w-full gradient-primary text-primary-foreground py-2.5 rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
            >
              {updateMutation.isPending ? 'Saving...' : 'Save Changes'}
            </button>
          </div>
        </DialogContent>
      </Dialog>
    </div>
  );
}
