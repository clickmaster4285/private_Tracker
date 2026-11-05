"use client";

import { useParams } from "next/navigation";
import TermsEditor from "@/components/terms/terms-editor";

export default function TermsEditPage() {
  const { id } = useParams<{ id: string }>();
  return <TermsEditor mode="edit" id={id} />;
}