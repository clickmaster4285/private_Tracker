import type { NextConfig } from "next";

// Browser calls relative /api/v1 (same-site cookies). Next rewrites that to the Go API.
// NEXT_PUBLIC_API_URL may be scheme://host:port or …/api/v1 — strip the suffix for the proxy root.
function resolveApiProxyTarget(): string {
  const raw = (process.env.NEXT_PUBLIC_API_URL || "").trim().replace(/\/+$/, "");
  if (raw) {
    return raw.replace(/\/api\/v1$/i, "") || raw;
  }
  return "http://localhost:8080";
}

const apiProxyTarget = resolveApiProxyTarget();

const nextConfig: NextConfig = {
  // Allow opening `next dev` from other machines on the LAN without the
  // "Cross origin request detected" warning for /_next/* dev resources.
  allowedDevOrigins: ["localhost", "127.0.0.1", "192.168.88.35", "192.168.88.44"],

  async rewrites() {
    return [
      { source: "/api/v1/:path*", destination: `${apiProxyTarget}/api/v1/:path*` },
    ];
  },
};

export default nextConfig;
