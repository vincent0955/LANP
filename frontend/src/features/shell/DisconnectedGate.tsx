import type { ReactNode } from "react";
import { PlugZap } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { useHealth } from "@/api/queries";
import { useSettings } from "@/lib/settings";
import { ApiError } from "@/api/http";
import { Link } from "react-router-dom";

/**
 * Full-screen takeover while the backend itself is unreachable (network-level
 * failure or auth rejection). Cluster problems are NOT gated here — /api/health
 * returns 200 even when the cluster is down, and the Setup screen is the place
 * that explains cluster state.
 */
export function DisconnectedGate({ children }: { children: ReactNode }) {
  const health = useHealth();
  const baseUrl = useSettings((s) => s.baseUrl);

  if (health.isPending) {
    return (
      <div className="flex h-screen items-center justify-center">
        <div className="w-80 space-y-3">
          <Skeleton className="h-8 w-full" />
          <Skeleton className="h-4 w-2/3" />
        </div>
      </div>
    );
  }

  if (health.isError) {
    const error = health.error;
    const unauthorized = error instanceof ApiError && error.status === 401;
    return (
      <div className="flex h-screen flex-col items-center justify-center gap-4 p-8 text-center">
        <PlugZap className="size-10 text-muted-foreground" />
        <div>
          <h1 className="text-lg font-semibold">
            {unauthorized ? "Backend rejected the API token" : "Backend unreachable"}
          </h1>
          <p className="mt-1 max-w-md text-sm text-muted-foreground">
            {unauthorized
              ? "The backend requires a valid X-Api-Token. Set it in Settings."
              : `Could not reach the dashboard backend at ${baseUrl}. Make sure it is running, then retry.`}
          </p>
        </div>
        <div className="flex gap-2">
          <Button onClick={() => health.refetch()} disabled={health.isFetching}>
            {health.isFetching ? "Retrying…" : "Retry"}
          </Button>
          <Button variant="outline" asChild>
            <Link to="/settings">Open Settings</Link>
          </Button>
        </div>
      </div>
    );
  }

  return <>{children}</>;
}
