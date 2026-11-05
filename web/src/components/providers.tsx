"use client";

import { QueryClient, QueryClientProvider, QueryClientContext } from "@tanstack/react-query";
import { useState, createContext, useContext, ReactNode } from "react";
import { Provider as ReduxProvider } from "react-redux";
import { store } from "@/lib/store/redux";
import { Toaster as Sonner } from "@/components/ui/sonner";
import { Toaster } from "@/components/ui/toaster";
import { TooltipProvider } from "@/components/ui/tooltip";
import { AuthProvider } from "@/lib/auth";
import { PermissionsProvider } from "@/lib/permissions";

export function Providers({ children }: { children: React.ReactNode }) {
  const [queryClient] = useState(() => new QueryClient({
    defaultOptions: {
      queries: {
        retry: false,
        staleTime: 1000 * 60 * 5,
      },
    },
  }));

  return (
    <QueryClientContext.Provider value={queryClient}>
      <ReduxProvider store={store}>
        <QueryClientProvider client={queryClient}>
          <TooltipProvider>
            <Toaster />
            <Sonner />
            <AuthProvider>
              <PermissionsProvider>{children}</PermissionsProvider>
            </AuthProvider>
          </TooltipProvider>
        </QueryClientProvider>
      </ReduxProvider>
    </QueryClientContext.Provider>
  );
}

export function useQueryClient() {
  return useContext(QueryClientContext);
}
