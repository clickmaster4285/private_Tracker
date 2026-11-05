import type { Metadata } from 'next';
import { APP_SHORT_NAME } from '@/config';

export const metadata: Metadata = {
  title: `${APP_SHORT_NAME} Releases`,
  description: `Download ${APP_SHORT_NAME} Windows and Linux desktop installers. Browse release notes for every published version.`,
  robots: { index: true, follow: true },
};

export default function ReleasesLayout({ children }: { children: React.ReactNode }) {
  return children;
}
