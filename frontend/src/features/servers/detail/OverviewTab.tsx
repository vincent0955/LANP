import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useNetworkInfo } from "@/api/queries";
import { formatDateTime } from "@/lib/format";
import type { ServerDetail } from "@/api/types";
import { ConnectivityPanel } from "./ConnectivityPanel";

/** A join address rendered as a click-to-copy chip. */
function CopyableAddress({ address, large = false }: { address: string; large?: boolean }) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(address);
      toast.success("Copied", { description: address });
    } catch {
      toast.error("Couldn't access the clipboard", { description: address });
    }
  };
  return (
    <button
      type="button"
      onClick={copy}
      title="Copy to clipboard"
      className={
        large
          ? "inline-flex items-center gap-2 rounded-md border px-3 py-2 font-mono text-lg font-semibold hover:bg-accent"
          : "inline-flex items-center gap-1.5 rounded-md border px-2 py-1 font-mono text-xs hover:bg-accent"
      }
    >
      {address}
      <Copy className={large ? "size-4 text-muted-foreground" : "size-3 text-muted-foreground"} />
    </button>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div className="flex justify-between gap-4 py-1.5 text-sm">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right font-medium">{value}</span>
    </div>
  );
}

export function OverviewTab({ server }: { server: ServerDetail }) {
  // Best-effort: when the backend can't report a LAN address, the card falls
  // back to the plain localhost hint.
  const networkInfo = useNetworkInfo().data;
  const lanAddress = networkInfo?.lanAddresses[0];
  const publicAddress = networkInfo?.publicAddress;
  // One-time router setup: the backend allocates every host port from this small
  // window, so forwarding it once covers all current and future servers.
  const forwardRange = networkInfo
    ? `${networkInfo.nodePortRangeStart}–${networkInfo.nodePortRangeEnd}`
    : null;
  // RCON is an admin channel, not a join address — the app's RCON tab already
  // covers it locally, and sharing it would hand out server control.
  const shareablePorts = server.ports.filter((p) => !p.name.toLowerCase().includes("rcon"));
  // Templates list the primary game port first, so the first shareable port is
  // the one players actually join through.
  const joinPort = shareablePorts[0];

  return (
    <div className="space-y-4">
      {joinPort && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Join</CardTitle>
          </CardHeader>
          <CardContent>
            {lanAddress || publicAddress ? (
              <div className="grid gap-4 sm:grid-cols-2">
                {lanAddress && (
                  <div className="space-y-1.5">
                    <p className="text-xs font-medium">Local</p>
                    <CopyableAddress large address={`${lanAddress}:${joinPort.nodePort}`} />
                    <p className="text-xs text-muted-foreground">
                      Share with players on your network.
                    </p>
                  </div>
                )}
                {publicAddress && (
                  <div className="space-y-1.5">
                    <p className="text-xs font-medium">Public</p>
                    <CopyableAddress large address={`${publicAddress}:${joinPort.nodePort}`} />
                    <p className="text-xs text-muted-foreground">
                      Friends anywhere join with this address. One-time setup: forward ports{" "}
                      <span className="font-mono">{forwardRange}</span> (TCP &amp; UDP) to this PC on
                      your router — that covers every server you deploy, now and later.
                    </p>
                  </div>
                )}
              </div>
            ) : (
              <p className="text-sm text-muted-foreground">
                {networkInfo
                  ? "Couldn't determine this network's addresses. Check your internet connection."
                  : "Looking up this network's addresses…"}
              </p>
            )}
          </CardContent>
        </Card>
      )}

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Server</CardTitle>
          </CardHeader>
          <CardContent className="divide-y">
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
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="text-base">Connection ports</CardTitle>
          </CardHeader>
          <CardContent>
            {server.ports.length === 0 ? (
              <p className="text-sm text-muted-foreground">No ports exposed.</p>
            ) : (
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b text-left text-xs text-muted-foreground">
                    <th className="py-1.5 font-medium">Name</th>
                    <th className="py-1.5 font-medium">Container</th>
                    <th className="py-1.5 font-medium">Node port</th>
                    <th className="py-1.5 font-medium">Protocol</th>
                  </tr>
                </thead>
                <tbody>
                  {server.ports.map((port) => (
                    <tr key={`${port.name}-${port.containerPort}`} className="border-b last:border-0">
                      <td className="py-1.5">{port.name}</td>
                      <td className="py-1.5 font-mono text-xs">{port.containerPort}</td>
                      <td className="py-1.5 font-mono text-xs font-semibold">{port.nodePort}</td>
                      <td className="py-1.5">{port.protocol}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            <p className="mt-3 text-xs text-muted-foreground">
              On this PC, connect to <span className="font-mono">localhost:&lt;node port&gt;</span>.
            </p>
          </CardContent>
        </Card>
      </div>

      {joinPort && <ConnectivityPanel server={server} />}
    </div>
  );
}
