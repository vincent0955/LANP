import { Link } from "react-router-dom";
import { Play, Square, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardFooter, CardHeader } from "@/components/ui/card";
import { StatusBadge } from "@/components/StatusBadge";
import { useMetrics, useScaleServer } from "@/api/queries";
import { formatBytes, formatDateTime, formatMillicores } from "@/lib/format";
import { toastApiError } from "@/lib/errors";
import { DeployActivity } from "./DeployActivity";
import type { ServerSummary } from "@/api/types";

interface Props {
  server: ServerSummary;
  onDelete: (name: string) => void;
}

export function ServerCard({ server, onDelete }: Props) {
  const scale = useScaleServer(server.name);
  const metrics = useMetrics();

  const podMetrics = metrics.data?.available
    ? metrics.data.pods.find((p) => p.serverName === server.name)
    : undefined;

  // Scale intent follows current replicas, not status: a Pending server (replicas
  // 1, still starting) should offer Stop, not Start.
  const running = server.replicas > 0;
  const toggle = () =>
    scale.mutate(running ? 0 : 1, { onError: toastApiError });

  return (
    <Card className="flex flex-col">
      <CardHeader className="flex-row items-start justify-between space-y-0">
        <div className="min-w-0">
          <Link
            to={`/servers/${encodeURIComponent(server.name)}`}
            className="block truncate font-semibold hover:underline"
          >
            {server.name}
          </Link>
          <p className="truncate text-sm text-muted-foreground">{server.game}</p>
        </div>
        <StatusBadge status={server.status} />
      </CardHeader>
      <CardContent className="flex-1 space-y-1 text-sm text-muted-foreground">
        {podMetrics && (
          <p>
            {formatMillicores(podMetrics.cpuUsedMillicores)} · {formatBytes(podMetrics.memUsedBytes)}
          </p>
        )}
        <p>Created {formatDateTime(server.createdAt)}</p>
        <DeployActivity server={server} />
      </CardContent>
      <CardFooter className="gap-2">
        <Button
          size="sm"
          variant={running ? "outline" : "default"}
          onClick={toggle}
          disabled={scale.isPending}
        >
          {running ? <Square className="size-4" /> : <Play className="size-4" />}
          {scale.isPending ? "…" : running ? "Stop" : "Start"}
        </Button>
        <Button
          size="sm"
          variant="ghost"
          className="text-destructive hover:text-destructive"
          onClick={() => onDelete(server.name)}
        >
          <Trash2 className="size-4" />
          Delete
        </Button>
      </CardFooter>
    </Card>
  );
}
