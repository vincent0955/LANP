import { useEffect, useMemo } from "react";
import { HardDriveDownload } from "lucide-react";
import { Progress } from "@/components/ui/progress";
import { subscribeLogs, unsubscribeLogs, useLogStore } from "@/realtime/logStore";
import { useProgressStore } from "@/realtime/progressStore";
import { parseSteamcmdProgress } from "@/lib/steamcmdProgress";
import { formatBytes } from "@/lib/format";
import type { ServerSummary } from "@/api/types";

/** How long after creation a server still counts as "first deploy". */
const FRESH_MS = 30 * 60 * 1000;

function isFirstDeployActivity(server: ServerSummary): boolean {
  // On a first deploy the game downloads inside the container (the server
  // stays Pending until its readiness probe passes) — so tail logs for any
  // recently created server that has a pod to tail (replicas > 0).
  if (server.replicas === 0) return false;
  if (server.status === "Error" || server.status === "Unknown") return false;
  const age = Date.now() - new Date(server.createdAt).getTime();
  return Number.isFinite(age) && age >= 0 && age < FRESH_MS;
}

/**
 * Live one-line activity feed on server cards during first deploy: shows the
 * latest raw log line verbatim (real feedback regardless of game/image), plus
 * a progress bar when the line happens to be steamcmd download progress.
 */
export function DeployActivity({ server }: { server: ServerSummary }) {
  const active = isFirstDeployActivity(server);

  useEffect(() => {
    if (!active) return;
    subscribeLogs(server.name);
    return () => unsubscribeLogs(server.name);
  }, [active, server.name]);

  const lines = useLogStore((s) => s.buffers[server.name]);
  // Backend-measured bytes on the data volume (DownloadProgress hub event,
  // du inside the pod) — works for every image; the PVC capacity is not the
  // install size, so this is shown as an absolute, never a percentage.
  const bytesOnDisk = useProgressStore((s) => s.byServer[server.name]?.bytesUsed);

  const { lastLine, progress } = useMemo(() => {
    if (!active || !lines || lines.length === 0) return { lastLine: null, progress: null };
    // Progress may not be in the very last line; scan a small recent window.
    let pct: number | null = null;
    for (let i = lines.length - 1; i >= 0 && i >= lines.length - 25; i--) {
      pct = parseSteamcmdProgress(lines[i].line);
      if (pct !== null) break;
    }
    return { lastLine: lines[lines.length - 1].line, progress: pct };
  }, [active, lines]);

  if (!active || (lastLine === null && bytesOnDisk === undefined)) return null;

  return (
    <div className="space-y-1.5">
      {progress !== null && <Progress value={progress} />}
      {bytesOnDisk !== undefined && (
        <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
          <HardDriveDownload className="size-3.5" />
          {formatBytes(bytesOnDisk)} on disk
        </p>
      )}
      {lastLine !== null && (
        <p className="truncate font-mono text-xs text-muted-foreground" title={lastLine}>
          {lastLine}
        </p>
      )}
    </div>
  );
}
