import type { NextConfig } from "next";

// The browser must talk to the API through THIS origin (same-site), otherwise the
// server's SameSite=Lax httpOnly auth cookies are never sent and /auth/check and
// /auth/refresh always answer 401. NEXT_PUBLIC_API_URL is server-side only (not exposed
// to the browser); it is the Go API root without /api/v1.
const apiProxyTarget = (process.env.NEXT_PUBLIC_API_URL || "http://localhost:8080").replace(/\/+$/, "");

const nextConfig: NextConfig = {
  // Allow opening `next dev` from other machines on the LAN (e.g. http://192.168.88.55:3000)
  // without the "Cross origin request detected" warning for /_next/* dev resources.
  allowedDevOrigins: ["localhost", "127.0.0.1", "192.168.88.35", "192.168.88.44"],

  async rewrites() {
    return [
      { source: "/api/v1/:path*", destination: `${apiProxyTarget}/api/v1/:path*` },
    ];
  },
};

export default nextConfig;
