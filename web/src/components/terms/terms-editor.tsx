"use client";

import { useEffect, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { ArrowLeft, Loader2, Save, Star } from "lucide-react";
import { termsContentApi, type TermsContentItem } from "@/lib/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { RichTextEditor } from "@/components/terms/rich-text-editor";

interface TermsEditorProps {
  mode: "create" | "edit";
  id?: string;
}

export default function TermsEditor({ mode, id }: TermsEditorProps) {
  const router = useRouter();
  const queryClient = useQueryClient();
  const isEdit = mode === "edit";
  const backHref = isEdit && id
    ? `/settings/terms-and-conditions/view/${id}`
    : "/settings/terms-and-conditions";

  const itemQuery = useQuery({
    queryKey: ["terms-content", id ?? ""],
    queryFn: () => termsContentApi.get(id!),
    enabled: isEdit && !!id,
  });
  const item = itemQuery.data;

  const [heading, setHeading] = useState<string>("");
  const [body, setBody] = useState<string>("");
  const [version, setVersion] = useState<string>("1.0");
  const [originalVersion, setOriginalVersion] = useState<string>("");
  const [versionError, setVersionError] = useState<string>("");
  const [hydrated, setHydrated] = useState<boolean>(false);

  // Hydrate the form from the fetched record once (edit mode).
  useEffect(() => {
    if (isEdit && item && !hydrated) {
      setHeading(item.heading);
      setBody(item.body);
      setVersion(item.termsVersion || "1.0");
      setOriginalVersion(item.termsVersion || "1.0");
      setHydrated(true);
    }
  }, [isEdit, item, hydrated]);

  const updateMutation = useMutation({
    mutationFn: (data: { heading: string; body: string; termsVersion: string }) =>
      termsContentApi.update(id!, data),
    onSuccess: (updated: TermsContentItem) => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
      router.push(`/settings/terms-and-conditions/view/${updated.id}`);
    },
    onError: (error: Error) => {
      const msg = error?.message || "";
      if (msg.toLowerCase().includes("version")) {
        setVersionError(msg);
      }
    },
  });

  const createMutation = useMutation({
    mutationFn: (data: { heading: string; body: string; termsVersion: string }) =>
      termsContentApi.create(data),
    onSuccess: (created: TermsContentItem) => {
      queryClient.invalidateQueries({ queryKey: ["terms-content"] });
      router.push(`/settings/terms-and-conditions/view/${created.id}`);
    },
  });

  const compareVersions = (a: string, b: string): number => {
    const partsA = a.split(".").map(Number);
    const partsB = b.split(".").map(Number);
    const maxLen = Math.max(partsA.length, partsB.length);
    for (let i = 0; i < maxLen; i++) {
      const va = partsA[i] || 0;
      const vb = partsB[i] || 0;
      if (va < vb) return -1;
      if (va > vb) return 1;
    }
    return 0;
  };

  const handleSave = () => {
    if (!heading.trim() || isSaving) return;
    if (isEdit && originalVersion && version && compareVersions(version, originalVersion) <= 0) {
      setVersionError(`Version must be greater than ${originalVersion}`);
      return;
    }
    setVersionError("");
    const data = { heading, body, termsVersion: version };
    if (isEdit) updateMutation.mutate(data);
    else createMutation.mutate(data);
  };

  const isSaving = updateMutation.isPending || createMutation.isPending;
  const isReady = !isEdit || !!(hydrated || item);

  if (isEdit && itemQuery.isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[300px]">
        <Loader2 className="w-7 h-7 animate-spin text-primary" />
      </div>
    );
  }

  if (isEdit && (!isReady || itemQuery.isError)) {
    return (
      <div className="space-y-6">
        <Button variant="ghost" size="sm" onClick={() => router.push("/settings/terms-and-conditions")}>
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
        <Button variant="ghost" size="sm" onClick={() => router.push(backHref)}>
          <ArrowLeft className="h-4 w-4 mr-1" /> Back
        </Button>
        <Separator orientation="vertical" className="h-6" />
        <h1 className="text-2xl font-bold tracking-tight">
          {isEdit ? `Edit: ${item?.heading}` : "Create New Terms"}
        </h1>
        {isEdit && item?.isSystem && (
          <Badge variant="secondary" className="text-xs gap-1">
            <Star className="w-3 h-3" /> Featured
          </Badge>
        )}
      </div>

      <div className="space-y-4">
        <div className="space-y-2">
          <Label className="text-sm font-bold">Heading</Label>
          <Input
            value={heading}
            onChange={(e) => setHeading(e.target.value)}
            placeholder="e.g. Screen Recording Policy"
          />
        </div>

        <div className="space-y-2">
          <Label className="text-sm font-bold">Content</Label>
          <RichTextEditor value={body} onChange={setBody} />
        </div>

        <div className="flex items-center gap-4">
          <div className="space-y-1">
            <Label className="text-sm font-bold">Version</Label>
            <Input
              value={version}
              onChange={(e) => { setVersion(e.target.value); setVersionError(""); }}
              className="h-9 w-24"
            />
            {versionError && (
              <p className="text-sm text-destructive mt-1">{versionError}</p>
            )}
          </div>
        </div>
      </div>

      <div className="flex items-center gap-2">
        <Button onClick={handleSave} disabled={!heading.trim() || isSaving}>
          {isSaving ? (
            <Loader2 className="h-4 w-4 mr-1 animate-spin" />
          ) : (
            <Save className="h-4 w-4 mr-1" />
          )}
          {isEdit ? "Save Changes" : "Create Terms"}
        </Button>
        <Button variant="outline" onClick={() => router.push(backHref)}>Cancel</Button>
      </div>
    </div>
  );
}