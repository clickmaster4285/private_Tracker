import Link from 'next/link';
import { APP_SHORT_NAME } from '@/config';

/**
 * Public route shell — no ProtectedRoute / RouteGuard.
 * Used for /releases so visitors can browse and download without login.
 */
export default function PublicLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-background text-foreground">
      <header className="border-b border-border bg-card/80 backdrop-blur-sm sticky top-0 z-20">
        <div className="mx-auto max-w-5xl px-4 py-3 flex items-center justify-between gap-4">
          <Link href="/releases" className="font-display font-bold text-foreground hover:opacity-90">
            {APP_SHORT_NAME}
          </Link>
          <nav className="flex items-center gap-3 text-sm">
            <Link href="/login" className="text-muted-foreground hover:text-foreground transition-colors">
              Admin login
            </Link>
          </nav>
        </div>
      </header>
      <main>{children}</main>
    </div>
  );
}
