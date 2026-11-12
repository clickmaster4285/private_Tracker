import type { NextConfig } from "next";

// Browser calls relative /api/v1 (same-site cookies). Next rewrites that to the Go API.
// Prefer API_PROXY_TARGET (scheme://host:port, no /api/v1). Fall back to stripping
// /api/v1 from NEXT_PUBLIC_API_URL when set for older labs.
function resolveApiProxyTarget(): string {
  const explicit = (process.env.API_PROXY_TARGET || "").trim().replace(/\/+$/, "");
  if (explicit) return explicit;

  const fromPublic = (process.env.NEXT_PUBLIC_API_URL || "").trim().replace(/\/+$/, "");
  if (fromPublic) {
    return fromPublic.replace(/\/api\/v1$/i, "") || fromPublic;
  }
  return "https://localhost:8080";
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
