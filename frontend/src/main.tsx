import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Toaster } from "@/components/ui/sonner";
import { initRealtime } from "@/realtime/connection";
import "./index.css";
import App from "./App";

// No polling: SignalR is the liveness mechanism (except the health heartbeat,
// see api/queries.ts). REST refetches happen on mount, focus, and reconnect.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchInterval: false,
      retry: 1,
      staleTime: 5_000,
    },
  },
});

initRealtime(queryClient);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipProvider>
        <App />
        <Toaster richColors position="bottom-right" />
      </TooltipProvider>
    </QueryClientProvider>
  </StrictMode>,
);
