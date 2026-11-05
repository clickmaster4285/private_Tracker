"use client";

import { Suspense, useState } from "react";
import { useRouter } from "next/navigation";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import {
  Loader2,
  Pencil,
  Trash2,
  Plus,
  Star,
  Eye,
  FileText,
  Globe,
  FolderOpen,
  Monitor,
  MoreVertical,
  Calendar,
} from "lucide-react";
import { termsContentApi, type TermsContentItem } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Separator } from "@/components/ui/separator";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useUrlQueryState } from "@/hooks/use-url-query-state";
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
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

type TermTab = "featured" | "custom";

const FEATURE_ICONS: Record<string, React.ReactNode> = {
  app_usage: <FileText className="w-5 h-5" />,
  browser_journey: <Globe className="w-5 h-5" />,
  file_journey: <FolderOpen className="w-5 h-5" />,
  live_view: <Monitor className="w-5 h-5" />,
};

function getFeatureIcon(featureId?: string) {
  return featureId ? FEATURE_ICONS[featureId] ?? <FileText className="w-5 h-5" /> : <FileText className="w-5 h-5" />;
}

function getFeatureLabel(featureId?: string) {
  const labels: Record<string, string> = {
    app_usage: "App Usage",
    browser_journey: "Browser Journey",
    file_journey: "File Journey",
    live_view: "Live View",
  };
  return featureId ? labels[featureId] ?? featureId : "";
}

function TermCard({
  item,
  onOpen,
  onEdit,
  onDelete,
  onToggleActive,
  isToggling,
}: {
  item: TermsContentItem;
  onOpen: () => void;
  onEdit: () => void;
  onDelete: () => void;
  onToggleActive: (checked: boolean) => void;
  isToggling: boolean;
}) {
  const isActive = item.isActive === 1;
  const snippet = item.body.replace(/<[^>]*>/g, "").slice(0, 600);

  return (
    <div
      role="button"
      tabIndex={0}
      onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") onOpen(); }}
      className={`group relative flex flex-col h-80 rounded-xl border bg-card p-5 transition-all duration-200 cursor-pointer hover:shadow-md hover:border-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring ${
        isActive ? "border-border" : "border-dashed border-muted-foreground/30 opacity-60"
      }`}
    >
      <div className="flex items-start justify-between gap-3 mb-2">
        <div className="flex items-center gap-2.5 min-w-0">
          <div
            className={`flex h-9 w-9 shrink-0 items-center justify-center rounded-lg ${
              isActive ? "bg-primary/10 text-primary" : "bg-muted text-muted-foreground"
            }`}
          >
            {getFeatureIcon(item.featureId)}
          </div>
          <div className="min-w-0">
            <h3 className="font-semibold leading-tight truncate">{item.heading}</h3>
            {item.featureId && (
              <span className="text-xs text-muted-foreground">
                {getFeatureLabel(item.featureId)}
              </span>
            )}
          </div>
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          {item.isSystem && (
            <Badge variant="secondary" className="text-[10px] gap-1">
              <Star className="w-2.5 h-2.5" /> Featured
            </Badge>
          )}
        </div>
      </div>

      <p className="text-sm text-muted-foreground line-clamp-8 ">
        {snippet || "No content yet..."}
      </p>

      {/* bottom bar */}
      <div className="mt-auto flex items-center justify-between pt-3 border-t">
        <div className="flex items-center gap-2 text-[11px] text-muted-foreground">
          <span>v{item.termsVersion}</span>
          <span className="text-border">·</span>
          <Calendar className="w-3 h-3" />
          <span>{new Date(item.updatedAt).toLocaleDateString()}</span>
        </div>

        <div className="flex items-center gap-2">
          <div className="flex items-center gap-1.5 rounded-full border px-2.5 py-1">
            <div className={`h-1.5 w-1.5 rounded-full ${isActive ? "bg-green-500" : "bg-muted-foreground/40"}`} />
            <span className={`text-[11px] font-medium ${isActive ? "text-green-600 dark:text-green-400" : "text-muted-foreground"}`}>
              {isActive ? "Active" : "Inactive"}
            </span>
            <Switch
              checked={isActive}
              onCheckedChange={onToggleActive}
              disabled={isToggling}
              className="h-4 w-7 [&>span]:h-3 [&>span]:w-3"
            />
          </div>

          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button
                variant="ghost"
                size="sm"
                className="h-7 w-7 p-0"
                onClick={(e) => e.stopPropagation()}
              >
                <MoreVertical className="h-3.5 w-3.5" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-36">
              <DropdownMenuItem onClick={onOpen}>
                <Eye className="h-3.5 w-3.5 mr-2" /> View
              </DropdownMenuItem>
              <DropdownMenuItem onClick={onEdit}>
                <Pencil className="h-3.5 w-3.5 mr-2" /> Edit
              </DropdownMenuItem>
              {!item.isSystem && (
                <>
                  <DropdownMenuSeparator />
                  <DropdownMenuItem
                    onClick={onDelete}
                    className="text-destructive focus:text-destructive"
                  >
                    <Trash2 className="h-3.5 w-3.5 mr-2" /> Delete
                  </DropdownMenuItem>
                </>
              )}
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>
    </div>
  );
}

function TermsGrid({
  items,
  onOpen,
  onEdit,
  onDelete,
  onToggleActive,
  togglingId,
}: {
  items: TermsContentItem[];
  onOpen: (item: TermsContentItem) => void;
  onEdit: (item: TermsContentItem) => void;
  onDelete: (item: TermsContentItem) => void;
  onToggleActive: (item: TermsContentItem, checked: boolean) => void;
  togglingId: string | null;
}) {
  if (items.length === 0) {
    return (
      <div className="rounded-xl border border-dashed p-12 text-center text-sm text-muted-foreground">
        No terms in this category.
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
      {items.map((item) => (
        <TermCard
          key={item.id}
          item={item}
          onOpen={() => onOpen(item)}
          onEdit={() => onEdit(item)}
          onDelete={() => onDelete(item)}
          onToggleActive={(checked) => onToggleActive(item, checked)}
          isToggling={togglingId === item.id}
        />
      ))}
    </div>
  );
}

function TermsAndConditionsInner() {
  const router = useRouter();
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState<TermsContentItem | null>(null);
  const [togglingId, setTogglingId] = useState<string | null>(null);

  const [urlState, setUrlState] = useUrlQueryState<{ tab: TermTab }>(
    { tab: { parse: (raw) => (raw === "custom" ? "custom" : "featured") } },
    { tab: "featured" },
  );

  const contentQuery = useQuery({
    queryKey: ["terms-content"],
    queryFn: () => termsContentApi.list(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => termsContentApi.delete(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
    },
  });

  const toggleActiveMutation = useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: number }) =>
      termsContentApi.updateActive(id, isActive),
    onMutate: async ({ id, isActive }) => {
      setTogglingId(id);
      await queryClient.cancelQueries({ queryKey: ["terms-content"] });
      const previous = queryClient.getQueryData<{ items: TermsContentItem[] }>(["terms-content"]);
      queryClient.setQueryData<{ items: TermsContentItem[] }>(["terms-content"], (old) => {
        if (!old) return old;
        return {
          ...old,
          items: old.items.map((item) =>
            item.id === id ? { ...item, isActive } : item
          ),
        };
      });
      return { previous };
    },
    onError: (_err, _vars, context) => {
      if (context?.previous) {
        queryClient.setQueryData(["terms-content"], context.previous);
      }
    },
    onSettled: () => {
      setTogglingId(null);
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
    },
  });

  const items = contentQuery.data?.items ?? [];
  const featured = items.filter((i) => i.isSystem);
  const custom = items.filter((i) => !i.isSystem);

  const activeCount = items.filter((i) => i.isActive === 1).length;
  const totalCount = items.length;

  return (
    <>
      <div className="flex items-start justify-between mb-6">
        <div>
          <h1 className="text-3xl font-bold tracking-tight">Terms &amp; Conditions</h1>
          <p className="text-muted-foreground mt-1">
            Manage the T&amp;C content for each tracking feature.
          </p>
        </div>
        <Button onClick={() => router.push("/settings/terms-and-conditions/create")}>
          <Plus className="w-4 h-4 mr-1" /> New Terms
        </Button>
      </div>

      <div className="flex items-center gap-4 mb-6">
        <div className="flex items-center gap-2 text-sm">
          <div className="h-2 w-2 rounded-full bg-green-500" />
          <span className="text-muted-foreground">
            <span className="font-medium text-foreground">{activeCount}</span> active
          </span>
        </div>
        <div className="flex items-center gap-2 text-sm">
          <div className="h-2 w-2 rounded-full bg-muted-foreground/40" />
          <span className="text-muted-foreground">
            <span className="font-medium text-foreground">{totalCount - activeCount}</span> inactive
          </span>
        </div>
      </div>

      <Separator className="mb-6" />

      {contentQuery.isLoading ? (
        <div className="flex items-center justify-center min-h-[200px]">
          <Loader2 className="w-6 h-6 animate-spin text-primary" />
        </div>
      ) : (
        <Tabs value={urlState.tab} onValueChange={(value) => setUrlState({ tab: value as TermTab })}>
          <TabsList>
            <TabsTrigger value="featured">
              Featured Terms
              <Badge variant="secondary" className="ml-1.5 text-[10px]">{featured.length}</Badge>
            </TabsTrigger>
            <TabsTrigger value="custom">
              Custom Terms
              <Badge variant="outline" className="ml-1.5 text-[10px]">{custom.length}</Badge>
            </TabsTrigger>
          </TabsList>
          <TabsContent value="featured" className="mt-4">
            <TermsGrid
              items={featured}
              onOpen={(item) => router.push(`/settings/terms-and-conditions/view/${item.id}`)}
              onEdit={(item) => router.push(`/settings/terms-and-conditions/edit/${item.id}`)}
              onDelete={(item) => setDeleting(item)}
              onToggleActive={(item, checked) =>
                toggleActiveMutation.mutate({ id: item.id, isActive: checked ? 1 : 0 })
              }
              togglingId={togglingId}
            />
          </TabsContent>
          <TabsContent value="custom" className="mt-4">
            <TermsGrid
              items={custom}
              onOpen={(item) => router.push(`/settings/terms-and-conditions/view/${item.id}`)}
              onEdit={(item) => router.push(`/settings/terms-and-conditions/edit/${item.id}`)}
              onDelete={(item) => setDeleting(item)}
              onToggleActive={(item, checked) =>
                toggleActiveMutation.mutate({ id: item.id, isActive: checked ? 1 : 0 })
              }
              togglingId={togglingId}
            />
          </TabsContent>
        </Tabs>
      )}

      <AlertDialog open={!!deleting} onOpenChange={() => setDeleting(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Custom Terms</AlertDialogTitle>
            <AlertDialogDescription>
              Are you sure you want to delete &quot;{deleting?.heading}&quot;? This action cannot be undone.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={() => {
                if (deleting) deleteMutation.mutate(deleting.id);
                setDeleting(null);
              }}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </>
  );
}

export default function TermsAndConditionsPage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center min-h-[300px]">
          <Loader2 className="w-7 h-7 animate-spin text-primary" />
        </div>
      }
    >
      <TermsAndConditionsInner />
    </Suspense>
  );
}
