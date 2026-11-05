'use client';

import { Suspense } from 'react';
import { Globe, Loader2 } from 'lucide-react';
import ClassifiedItemsTable from '@/components/configuration/ClassifiedItemsTable';
import { monitoringApi, type MonitoredApp } from '@/lib/api';

export default function ApplicationsClassification() {
  return (
    <Suspense fallback={<div className="flex items-center justify-center min-h-[400px]"><Loader2 className="w-8 h-8 animate-spin text-primary" /></div>}>
      <ClassifiedItemsTable<MonitoredApp>
        title="Applications"
        description="Classify the applications detected across employee machines. Apps link to the real installed_applications catalog — nothing here is static."
        queryKeyPrefix="monitoring-apps"
        scope="application"
        nameHeader="Application"
        nameOf={(item) => item.appName}
        badge={(item) =>
          item.isBrowser ? (
            <span className="inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-[11px] font-medium bg-accent text-accent-foreground">
              <Globe className="w-3 h-3" /> Browser
            </span>
          ) : undefined
        }
        listFn={(params) => monitoringApi.apps.list(params)}
        classifyFn={(id, payload) => monitoringApi.apps.classify(String(id), payload)}
      />
    </Suspense>
  );
}
