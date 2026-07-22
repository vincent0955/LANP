import { useNetworkInfo } from "@/api/queries";
import { CopyButton } from "@/components/CopyButton";
import { useConnectivity } from "@/lib/connectivity";
import { formatDateTime } from "@/lib/format";
import type { ServerDetail } from "@/api/types";
import { ConnectionsCard } from "./ConnectionsCard";
import type { DetailTab } from "./ServerDetailPage";

interface Props {
  server: ServerDetail;
  onOpenHelp: () => void;
  onGoTab: (tab: DetailTab) => void;
  onDelete: () => void;
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 border-b border-[#eef3f7] py-3 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span className="truncate text-right font-semibold">{value}</span>
    </div>
  );
}

export function OverviewTab({ server, onOpenHelp, onGoTab, onDelete }: Props) {
  const networkInfo = useNetworkInfo().data;
  const verified = useConnectivity((s) => s.verified);

  // RCON is an admin channel, not a join address; templates list the primary
  // game port first, so the first non-rcon port is the one players join through.
  const joinPort = server.ports.find((p) => !p.name.toLowerCase().includes("rcon"));
  const lanAddress = networkInfo?.lanAddresses[0] ?? "localhost";
  const publicAddress = networkInfo?.publicAddress;
  const localJoin = joinPort ? `${lanAddress}:${joinPort.nodePort}` : null;
  const publicJoin = joinPort && publicAddress ? `${publicAddress}:${joinPort.nodePort}` : null;

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

      {/* This server + Connections */}
      <div className="grid gap-5 lg:grid-cols-[1.4fr_1fr]">
        <div className="self-start rounded-[10px] border bg-card px-[30px] py-[26px]">
          <div className="mb-1.5 text-base font-bold">This server</div>
          <Row label="Game" value={server.game} />
          <Row label="Image" value={<span className="font-mono text-xs">{server.image}</span>} />
          <Row label="Replicas" value={server.replicas} />
          <Row label="Created" value={formatDateTime(server.createdAt)} />
          {server.players && (
            <Row
              label="Players"
              value={`${server.players.currentPlayers} / ${server.players.maxPlayers}${
                server.players.currentMap ? ` · ${server.players.currentMap}` : ""
              }`}
            />
          )}
          {server.resources && (
            <>
              <Row
                label="CPU (request / limit)"
                value={`${server.resources.cpuRequest} / ${server.resources.cpuLimit}`}
              />
              <Row
                label="Memory (request / limit)"
                value={`${server.resources.memoryRequest} / ${server.resources.memoryLimit}`}
              />
            </>
          )}
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

        <ConnectionsCard server={server} />
      </div>
    </div>
  );
}
