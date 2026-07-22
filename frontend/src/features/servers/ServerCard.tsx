import { Link } from "react-router-dom";
import { Play, Square, X } from "lucide-react";
import { useMetrics, useScaleServer } from "@/api/queries";
import { gameArt, gameInitials, formatUptime } from "@/lib/gameArt";
import { formatBytes, formatMillicores } from "@/lib/format";
import { toastApiError } from "@/lib/errors";
import { DeployActivity } from "./DeployActivity";
import type { ServerStatus, ServerSummary } from "@/api/types";

interface Props {
  server: ServerSummary;
  onDelete: (name: string) => void;
}

// Status dot colour for the art-header badge (dark pill, coloured dot + label).
const statusDot: Record<ServerStatus, string> = {
  Running: "#3fe08e",
  Pending: "#ffb454",
  Error: "#ff6b6b",
  Stopped: "#cbd5e1",
  Unknown: "#cbd5e1",
};

const statusLabel: Record<ServerStatus, string> = {
  Running: "Running",
  Pending: "Starting",
  Error: "Error",
  Stopped: "Stopped",
  Unknown: "Stopped",
};

export function ServerCard({ server, onDelete }: Props) {
  const scale = useScaleServer(server.name);
  const metrics = useMetrics();

  const podMetrics = metrics.data?.available
    ? metrics.data.pods.find((p) => p.serverName === server.name)
    : undefined;

  // Scale intent follows current replicas, not status: a Pending server (replicas
  // 1, still starting) should offer Stop, not Start.
  const running = server.replicas > 0;
  const toggle = () => scale.mutate(running ? 0 : 1, { onError: toastApiError });

  const uptime = running ? formatUptime(server.createdAt) : null;
  const sub = uptime ? `${server.name} · ${uptime}` : server.name;
  const detailPath = `/servers/${encodeURIComponent(server.name)}`;

  return (
    <div className="group overflow-hidden rounded-lg border border-[#e3eaf1] bg-card transition-all hover:-translate-y-[3px] hover:border-[#b9dff5] hover:shadow-[0_8px_22px_rgba(16,49,74,0.10)]">
      {/* Art header — gradient cover, initials, status badge. Clickable to detail. */}
      <Link
        to={detailPath}
        className="relative flex h-[130px] items-center justify-center"
        style={{ background: gameArt(server.game || server.name) }}
      >
        <span className="text-[44px] font-extrabold tracking-[-0.02em] text-white/90">
          {gameInitials(server.game || server.name)}
        </span>
        <span className="absolute right-3 top-3 flex items-center gap-1.5 rounded-md bg-[rgba(10,16,24,0.72)] px-[11px] py-1 text-xs font-semibold text-white">
          <span
            className="inline-block size-1.5 rounded-full"
            style={{ background: statusDot[server.status] }}
          />
          {statusLabel[server.status]}
        </span>
      </Link>

      <div className="px-[18px] pb-[18px] pt-4">
        <Link to={detailPath} className="block truncate text-[16.5px] font-bold hover:underline">
          {server.game || server.name}
        </Link>
        <p className="mt-0.5 truncate text-[13px] text-[#8795a3]" title={sub}>
          {sub}
        </p>
        {podMetrics && (
          <p className="mt-0.5 truncate text-[13px] text-[#8795a3]">
            {formatMillicores(podMetrics.cpuUsedMillicores)} · {formatBytes(podMetrics.memUsedBytes)}
          </p>
        )}
        <DeployActivity server={server} />

        <div className="mt-3.5 flex gap-2">
          <button
            type="button"
            onClick={toggle}
            disabled={scale.isPending}
            className={
              running
                ? "flex flex-1 items-center justify-center gap-2 rounded-md bg-[#eef3f7] py-[11px] text-[13px] font-bold uppercase tracking-[0.07em] text-[#45596b] transition hover:brightness-[0.97] disabled:opacity-60"
                : "flex flex-1 items-center justify-center gap-2 rounded-md bg-primary py-[11px] text-[13px] font-bold uppercase tracking-[0.07em] text-primary-foreground transition hover:bg-[#85d7ff] disabled:opacity-60"
            }
          >
            {running ? <Square className="size-4" /> : <Play className="size-4" />}
            {scale.isPending ? "…" : running ? "Stop" : "Play"}
          </button>
          <button
            type="button"
            onClick={() => onDelete(server.name)}
            aria-label={`Delete ${server.name}`}
            className="flex size-[42px] shrink-0 items-center justify-center rounded-md border border-[#e3eaf1] text-[#8795a3] transition hover:border-[#eccfcd] hover:bg-[#fbf1f0] hover:text-[#b0433f]"
          >
            <X className="size-4" />
          </button>
        </div>
      </div>
    </div>
  );
}
