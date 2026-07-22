import { useBackupSettings, useNetworkInfo, useServerConfig } from "@/api/queries";
import { CopyButton } from "@/components/CopyButton";
import { useConnectivity } from "@/lib/connectivity";
import { formatDate, formatShortTime } from "@/lib/format";
import { useActivityStore } from "@/realtime/activityStore";
import type { ServerDetail } from "@/api/types";
import type { DetailTab } from "./ServerDetailPage";

interface Props {
  server: ServerDetail;
  onOpenHelp: () => void;
  onGoTab: (tab: DetailTab) => void;
  onDelete: () => void;
}

/** First config value present among `keys`, else null. */
function configValue(config: Record<string, string> | undefined, keys: string[]): string | null {
  for (const key of keys) {
    const value = config?.[key];
    if (value) return value;
  }
  return null;
}

function backupScheduleLabel(enabled: boolean, intervalMinutes: number): string {
  if (!enabled) return "Manual";
  if (intervalMinutes % 60 === 0) {
    const hours = intervalMinutes / 60;
    return hours === 1 ? "Every hour" : `Every ${hours} hours`;
  }
  return `Every ${intervalMinutes} minutes`;
}

/** "5:58 PM" for today's entries, "Jul 18" for older ones. */
function activityStamp(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  const now = new Date();
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate();
  return sameDay
    ? formatShortTime(iso)
    : date.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

export function OverviewTab({ server, onOpenHelp, onGoTab, onDelete }: Props) {
  const networkInfo = useNetworkInfo().data;
  const config = useServerConfig(server.name).data;
  const backupSettings = useBackupSettings().data;
  const verified = useConnectivity((s) => s.verified);
  const activity = useActivityStore((s) => s.byServer[server.name]);

  // RCON is an admin channel, not a join address; templates list the primary
  // game port first, so the first non-rcon port is the one players join through.
  const joinPort = server.ports.find((p) => !p.name.toLowerCase().includes("rcon"));
  const lanAddress = networkInfo?.lanAddresses[0] ?? "localhost";
  const publicAddress = networkInfo?.publicAddress;
  const localJoin = joinPort ? `${lanAddress}:${joinPort.nodePort}` : null;
  const publicJoin = joinPort && publicAddress ? `${publicAddress}:${joinPort.nodePort}` : null;

  const facts: { k: string; v: string }[] = [
    { k: "World", v: configValue(config, ["LEVEL", "WORLD", "WORLD_NAME", "MAP"]) },
    { k: "Version", v: configValue(config, ["VERSION"]) },
    {
      k: "Max players",
      v:
        configValue(config, ["MAX_PLAYERS", "MAXPLAYERS"]) ??
        (server.players ? String(server.players.maxPlayers) : null),
    },
    {
      k: "Backups",
      v: backupSettings
        ? backupScheduleLabel(backupSettings.enabled, backupSettings.intervalMinutes)
        : null,
    },
    { k: "Created", v: formatDate(server.createdAt) },
  ].filter((f): f is { k: string; v: string } => f.v !== null);

  // Feed is newest-first in the store; the card reads top-down chronologically,
  // ending at the most recent event, with creation pinned first.
  const feed = [
    { timestamp: server.createdAt, message: "Server created from Game Library" },
    ...[...(activity ?? [])].reverse(),
  ].slice(-6);

  return (
    <div className="space-y-5">
      {/* Invite friends */}
      {joinPort && (
        <div className="rounded-[10px] border bg-card px-[30px] py-7">
          <div className="flex flex-wrap items-baseline justify-between gap-4">
            <div>
              <div className="text-lg font-bold">Invite friends</div>
              <div className="mt-[3px] text-[13.5px] text-muted-foreground">
                Send them the address that matches where they are.
              </div>
            </div>
            <button
              type="button"
              onClick={onOpenHelp}
              className="cursor-pointer text-[13px] font-semibold text-[#2596d1] transition-colors hover:text-foreground"
            >
              Which one do I share? →
            </button>
          </div>

          <div className="mt-5 grid gap-4 sm:grid-cols-2">
            {/* On your network */}
            <div className="rounded-lg border bg-[#fbfdfe] px-5 py-[18px]">
              <div className="flex items-center gap-2">
                <span className="text-xs font-bold uppercase tracking-[0.08em] text-muted-foreground">
                  On your network
                </span>
                <span className="rounded-full bg-[#e4f7ee] px-[9px] py-0.5 text-[11.5px] font-semibold text-[#1d7a51]">
                  No setup
                </span>
              </div>
              <div className="mt-2.5 flex items-center gap-2.5">
                <span className="break-all font-mono text-base font-semibold">{localJoin}</span>
                {localJoin && <CopyButton text={localJoin} />}
              </div>
              <div className="mt-2 text-[12.5px] text-[#8795a3]">
                For people in the same house or on the same Wi‑Fi.
              </div>
            </div>

            {/* Over the internet */}
            <div className="rounded-lg border bg-[#fbfdfe] px-5 py-[18px]">
              <div className="flex items-center gap-2">
                <span className="text-xs font-bold uppercase tracking-[0.08em] text-muted-foreground">
                  Over the internet
                </span>
                {verified ? (
                  <span className="rounded-full bg-[#e4f7ee] px-[9px] py-0.5 text-[11.5px] font-semibold text-[#1d7a51]">
                    Verified ✓
                  </span>
                ) : (
                  <span className="rounded-full bg-[#fff3d6] px-[9px] py-0.5 text-[11.5px] font-semibold text-[#8a6d1f]">
                    Setup needed
                  </span>
                )}
              </div>
              <div className="mt-2.5 flex items-center gap-2.5">
                {publicJoin ? (
                  <>
                    <span className="break-all font-mono text-base font-semibold">{publicJoin}</span>
                    <CopyButton text={publicJoin} />
                  </>
                ) : (
                  <span className="text-sm text-[#8795a3]">
                    {networkInfo ? "Couldn't determine your public address." : "Looking up…"}
                  </span>
                )}
              </div>
              <div className="mt-2 text-[12.5px] text-[#8795a3]">
                For friends anywhere. One-time router setup — click the ? for a guided walkthrough.
              </div>
            </div>
          </div>

          {!verified && (
            <div className="mt-4 flex flex-wrap items-center gap-3 rounded-lg border border-[#f3e2bd] bg-[#fff8ec] px-[18px] py-[13px]">
              <span className="text-[15px]">⚠️</span>
              <span className="text-[13.5px] text-[#7a5c1e]">
                Internet play isn't verified yet — friends outside your network may not be able to
                join.
              </span>
              <button
                type="button"
                onClick={onOpenHelp}
                className="ml-auto cursor-pointer whitespace-nowrap rounded-[6px] border border-[#e5d5ad] bg-white px-4 py-[7px] text-[13px] font-bold text-foreground transition-colors hover:border-primary"
              >
                Run setup check
              </button>
            </div>
          )}
        </div>
      )}

      {/* This server + Recent activity */}
      <div className="grid gap-5 lg:grid-cols-[1.4fr_1fr]">
        <div className="rounded-[10px] border bg-card px-[30px] py-[26px]">
          <div className="mb-1.5 text-base font-bold">This server</div>
          {facts.map((f) => (
            <div
              key={f.k}
              className="flex items-center justify-between gap-4 border-b border-[#eef3f7] py-3 text-sm"
            >
              <span className="text-muted-foreground">{f.k}</span>
              <span className="truncate font-semibold" title={f.v}>
                {f.v}
              </span>
            </div>
          ))}
          <div className="mt-4 flex gap-[18px] text-[13px] font-semibold">
            <button
              type="button"
              onClick={() => onGoTab("config")}
              className="cursor-pointer text-[#2596d1] transition-colors hover:text-foreground"
            >
              Edit settings
            </button>
            <button
              type="button"
              onClick={() => onGoTab("logs")}
              className="cursor-pointer text-[#2596d1] transition-colors hover:text-foreground"
            >
              View logs
            </button>
            <button
              type="button"
              onClick={() => onGoTab("backups")}
              className="cursor-pointer text-[#2596d1] transition-colors hover:text-foreground"
            >
              Backups
            </button>
            <button
              type="button"
              onClick={onDelete}
              className="ml-auto cursor-pointer text-[#b0433f] transition-colors hover:text-[#7c2320]"
            >
              Delete
            </button>
          </div>
        </div>

        <div className="rounded-[10px] border bg-card px-[30px] py-[26px]">
          <div className="mb-1.5 text-base font-bold">Recent activity</div>
          {feed.map((entry, i) => (
            <div key={i} className="flex gap-3 border-b border-[#eef3f7] py-[11px]">
              <span className="shrink-0 pt-0.5 font-mono text-xs text-[#8795a3]">
                {activityStamp(entry.timestamp)}
              </span>
              <span className="text-[13.5px] text-[#45596b]">{entry.message}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
