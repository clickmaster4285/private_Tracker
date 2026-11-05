"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth";
import { STORAGE_PREFIX } from "@/config";

export default function HomePage() {
  const { user } = useAuth();
  const router = useRouter();

  useEffect(() => {
    // Purge legacy mock-data keys seeded by the removed localStorage store.
    if (typeof window !== "undefined") {
      const stale: string[] = [];
      for (let i = 0; i < window.localStorage.length; i++) {
        const key = window.localStorage.key(i);
        if (key?.startsWith(STORAGE_PREFIX)) stale.push(key);
      }
      stale.forEach(k => window.localStorage.removeItem(k));
    }
    if (user) {
      router.replace("/dashboard");
    } else {
      router.replace("/login");
    }
  }, [user, router]);

  return null;
}
