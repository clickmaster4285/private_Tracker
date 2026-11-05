import type { Metadata } from "next";
import "@/globals.css";
import { Providers } from "@/components/providers";
import { ReactScan } from "@/components/ReactScan";

export const metadata: Metadata = {
  title: "Alpha AI Tracking, Monitoring & Productivity System",
  description: "Alpha AI Tracker - AI-powered tracking, monitoring and productivity system",
  icons: {
    icon: "/favicon.ico",
  },
};

export default function RootLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <html lang="en">
      <head>
        <meta property="og:type" content="website" />
        <meta property="og:title" content="Alpha AI Tracking, Monitoring &amp; Productivity System" />
        <meta property="og:description" content="Alpha AI Tracker - AI-powered tracking, monitoring and productivity system" />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content="Alpha AI Tracking, Monitoring &amp; Productivity System" />
        <meta name="twitter:description" content="Alpha AI Tracker - AI-powered tracking, monitoring and productivity system" />
        <link rel="icon" type="image/png" href="/app-logo.png" />
        <link rel="apple-touch-icon" href="/app-logo.png" />
      </head>
      <body>
        <ReactScan />
        <Providers>{children}</Providers>
      </body>
    </html>
  );
}
