import { Progress } from "@/components/ui/progress";
import { useMetrics } from "@/api/queries";
import { formatBytes, formatMillicores } from "@/lib/format";

/**
 * Compact host CPU/memory gauges. Metrics arrive via MetricsUpdate
 * pushes overwriting the same query cache entry this reads (Req 8). When
 * metrics-server is absent the backend reports available=false — render an
 * unobtrusive note, never an error (Req 8.2).
 */
export function NodeMetricsBar() {
  const metrics = useMetrics();

  if (metrics.isPending || metrics.isError) return null;

  if (!metrics.data.available) {
    return (
      <p className="text-xs text-muted-foreground">
        Resource metrics unavailable
        {metrics.data.unavailableReason ? ` — ${metrics.data.unavailableReason}` : ""}.
      </p>
    );
  }

  const node = metrics.data.node;
  if (!node) return null;

  const cpuPct = (node.cpuUsedMillicores / node.cpuCapacityMillicores) * 100;
  const memPct = (node.memUsedBytes / node.memCapacityBytes) * 100;

  return (
    <div className="flex flex-wrap gap-x-8 gap-y-2 rounded-lg border px-4 py-3">
      <div className="min-w-48 flex-1">
        <div className="mb-1 flex justify-between text-xs">
          <span className="text-muted-foreground">Node CPU</span>
          <span>
            {formatMillicores(node.cpuUsedMillicores)} / {formatMillicores(node.cpuCapacityMillicores)}
          </span>
        </div>
        <Progress value={cpuPct} />
      </div>
      <div className="min-w-48 flex-1">
        <div className="mb-1 flex justify-between text-xs">
          <span className="text-muted-foreground">Node memory</span>
          <span>
            {formatBytes(node.memUsedBytes)} / {formatBytes(node.memCapacityBytes)}
          </span>
        </div>
        <Progress value={memPct} />
      </div>
    </div>
  );
}
