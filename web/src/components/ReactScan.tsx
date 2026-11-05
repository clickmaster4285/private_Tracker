"use client";

// Must load before React so renders are instrumented from the first paint.
import { scan } from "react-scan";

scan({
  enabled: process.env.NEXT_PUBLIC_NODE_ENV === "development",
  showToolbar: process.env.NEXT_PUBLIC_NODE_ENV === "development",
});

/** Dev-only render highlighter. No-op in production. */
export function ReactScan() {
  return null;
}
