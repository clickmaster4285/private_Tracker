"use client";

import { useParams } from "next/navigation";
import TermsView from "@/components/terms/terms-view";

export default function TermsViewPage() {
  const { id } = useParams<{ id: string }>();
  return <TermsView id={id} />;
}