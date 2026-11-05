"use client";

import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { ArrowLeft, Loader2, Pencil, Star, Trash2 } from "lucide-react";
import { termsContentApi, type TermsContentItem } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { Switch } from "@/components/ui/switch";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";

export default function TermsView({ id }: { id: string }) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [confirmingDelete, setConfirmingDelete] = useState(false);

  const itemQuery = useQuery({
    queryKey: ["terms-content", id],
    queryFn: () => termsContentApi.get(id),
  });

  const deleteMutation = useMutation({
    mutationFn: (termId: string) => termsContentApi.delete(termId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
      router.push("/settings/terms-and-conditions");
    },
  });

  const toggleActiveMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: number }) =>
      termsContentApi.updateActive(id, isActive),
    onMutate: async ({ id, isActive }) => {
      await queryClient.cancelQueries({ queryKey: ["terms-content", id] });
      const previous = queryClient.getQueryData<TermsContentItem>(["terms-content", id]);
      queryClient.setQueryData<TermsContentItem>(["terms-content", id], (old) =>
        old ? { ...old, isActive } : old
      );
      return { previous };
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(["terms-content", _vars.id], context.previous);
      }
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
    },
  });

  const item = itemQuery.data;

  if (itemQuery.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[300px]">
        <Loader2 className="w-7 h-7 animate-spin text-primary" />
      </div>
    );
  }

  if (itemQuery.isError || !item) {
    return (
      <div className="space-y-6">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => router.push("/settings/terms-and-conditions")}
        >
          <ArrowLeft className="h-4 w-4 mr-1" /> Back to terms
        </Button>
        <div className="rounded-lg border border-dashed p-10 text-center text-sm text-muted-foreground">
          Could not load this term. It may have been deleted.
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => router.push("/settings/terms-and-conditions")}
        >
          <ArrowLeft className="h-4 w-4 mr-1" /> Back
        </Button>
        <Separator orientation="vertical" className="h-6" />
        <h1 className="text-2xl font-bold tracking-tight truncate">{item.heading}</h1>
        {item.isSystem && (
          <Badge variant="secondary" className="text-xs gap-1 shrink-0">
            <Star className="w-3 h-3" /> Featured
          </Badge>
        )}
        <Badge variant={item.isActive === 1 ? "default" : "outline"} className="text-xs shrink-0">
          {item.isActive === 1 ? "Active" : "Inactive"}
        </Badge>
      </div>

      <Card>
        <CardContent className="p-6">
          <div
            className="prose prose-sm dark:prose-invert max-w-none"
            dangerouslySetInnerHTML={{ __html: item.body }}
          />
          <div className="flex items-center gap-3 mt-6 pt-4 border-t text-xs text-muted-foreground">
            <span>Terms v{item.termsVersion}</span>
            <span>|</span>
            <span>Updated {new Date(item.updatedAt).toLocaleString()}</span>
            <span>|</span>
            <span>Created {new Date(item.createdAt).toLocaleDateString()}</span>
          </div>
        </CardContent>
      </Card>

      <div className="flex items-center gap-3">
        <Button onClick={() => router.push(`/settings/terms-and-conditions/edit/${item.id}`)}>
          <Pencil className="h-4 w-4 mr-1" /> Edit
        </Button>
        {!item.isSystem && (
          <Button variant="destructive" onClick={() => setConfirmingDelete(true)}>
            <Trash2 className="h-4 w-4 mr-1" /> Delete
          </Button>
        )}
        <div className="ml-auto flex items-center gap-2">
          <span className="text-sm text-muted-foreground">
            {item.isActive === 1 ? "Active" : "Inactive"}
          </span>
          <Switch
            checked={item.isActive === 1}
            onCheckedChange={(checked) =>
              toggleActiveMutation.mutate({ id: item.id, isActive: checked ? 1 : 0 })
            }
          />
        </div>
      </div>

      <AlertDialog open={confirmingDelete} onOpenChange={setConfirmingDelete}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Custom Terms</AlertDialogTitle>
            <AlertDialogDescription>
              Are you sure you want to delete &quot;{item.heading}&quot;? This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                deleteMutation.mutate(item.id);
                setConfirmingDelete(false);
              }}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  );
}