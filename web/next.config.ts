import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  // Allow opening `next dev` from other machines on the LAN (e.g. http://192.168.88.55:3000)
  // without the "Cross origin request detected" warning for /_next/* dev resources.
  allowedDevOrigins: ["localhost", "127.0.0.1", "192.168.88.35"],
};

export default nextConfig;
