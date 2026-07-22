import { CheckCircle2, HelpCircle, Loader2, Wifi, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Skeleton } from "@/components/ui/skeleton";
import { CopyableCommand } from "@/components/CopyableCommand";
import { useForwardingGuide, useReachabilityTest } from "@/api/queries";
import type { ForwardingRule, PortReachability, ServerDetail } from "@/api/types";

/**
 * The server's Connections card (WS2): the technical port table + how to reach
 * the server on this PC, and — for shareable game ports — exact router/firewall
 * instructions plus an on-demand outside-in connectivity test. The app only
 * diagnoses and instructs; it never changes the user's network itself.
 */
export function ConnectivityPanel({ server }: { server: ServerDetail }) {
  const guide = useForwardingGuide(server.name);
  const test = useReachabilityTest(server.name);
  const result = test.data;

  // RCON is an admin channel, not a join address — only non-rcon ports are
  // worth forwarding/sharing, so the "let friends join" half hinges on those.
  const hasJoinPort = server.ports.some((p) => !p.name.toLowerCase().includes("rcon"));

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Connections</CardTitle>
        <CardDescription>How to connect on this PC, and how to let friends join.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {/* Ports (always) */}
        {server.ports.length === 0 ? (
          <p className="text-sm text-muted-foreground">No ports exposed.</p>
        ) : (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-xs text-muted-foreground">
                <th className="py-1.5 font-medium">Name</th>
                <th className="py-1.5 font-medium">Container</th>
                <th className="py-1.5 font-medium">Host port</th>
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
        <p className="text-xs text-muted-foreground">
          On this PC, connect to <span className="font-mono">localhost:&lt;host port&gt;</span>.
        </p>

        {/* Let friends join (shareable game ports only) */}
        {hasJoinPort && (
          <div className="space-y-4 border-t pt-4">
            <p className="text-sm font-medium">Let friends join</p>

            {guide.isPending ? (
              <Skeleton className="h-24" />
            ) : guide.isError ? (
              <Alert variant="destructive">
                <AlertTitle>Couldn't build the forwarding guide</AlertTitle>
                <AlertDescription>{guide.error.message}</AlertDescription>
              </Alert>
            ) : (
              <>
                {guide.data.note && (
                  <Alert variant="destructive">
                    <AlertTitle>This network can't accept forwarded connections</AlertTitle>
                    <AlertDescription>{guide.data.note}</AlertDescription>
                  </Alert>
                )}

                <p className="text-sm text-muted-foreground">
                  On your router, forward{" "}
                  {summarizeRules(guide.data.rules)} to{" "}
                  <span className="font-mono">{guide.data.lanAddresses[0] ?? "this PC"}</span>{" "}
                  (external and internal ports are the same).
                </p>

                {guide.data.firewallCommands.length > 0 && (
                  <details className="text-sm">
                    <summary className="cursor-pointer text-muted-foreground">
                      Windows firewall (run once as administrator)
                    </summary>
                    <div className="mt-2 space-y-2">
                      {guide.data.firewallCommands.map((command) => (
                        <CopyableCommand key={command} command={command} />
                      ))}
                    </div>
                  </details>
                )}

                <div className="flex items-center justify-between gap-3">
                  <p className="text-sm text-muted-foreground">
                    Check whether players on the internet can currently reach this server.
                  </p>
                  <Button variant="outline" onClick={() => test.mutate()} disabled={test.isPending}>
                    {test.isPending ? <Loader2 className="size-4 animate-spin" /> : <Wifi className="size-4" />}
                    {test.isPending ? "Testing…" : "Test connectivity"}
                  </Button>
                </div>

                {result && (
                  <div className="space-y-1.5">
                    {result.cgnatDetected && result.cgnatDetail && (
                      <Alert variant="destructive">
                        <AlertTitle>Carrier-Grade NAT detected</AlertTitle>
                        <AlertDescription>{result.cgnatDetail}</AlertDescription>
                      </Alert>
                    )}
                    {result.ports.map((port) => (
                      <PortResultRow key={`${port.name}-${port.protocol}`} port={port} />
                    ))}
                  </div>
                )}
              </>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

/** Compact "TCP 30016 · UDP 30015, 30020" summary of the forward rules. */
function summarizeRules(rules: ForwardingRule[]): string {
  return ["TCP", "UDP"]
    .map((proto) => {
      const ports = rules
        .filter((r) => r.protocol.toUpperCase() === proto)
        .map((r) => r.port);
      return ports.length ? `${proto} ${ports.join(", ")}` : null;
    })
    .filter(Boolean)
    .join(" · ");
}

function PortResultRow({ port }: { port: PortReachability }) {
  const { icon: Icon, color } = reachabilityIndicator(port);
  return (
    <div className="flex items-start gap-2.5 rounded-md border px-3 py-2 text-sm">
      <Icon className={`mt-0.5 size-4 shrink-0 ${color}`} />
      <div className="min-w-0">
        <div className="font-medium">
          {port.name} <span className="font-mono text-xs text-muted-foreground">{port.protocol} {port.port}</span>
        </div>
        {port.detail && <div className="text-xs text-muted-foreground">{port.detail}</div>}
      </div>
    </div>
  );
}

function reachabilityIndicator(port: PortReachability) {
  if (port.external === "Open") return { icon: CheckCircle2, color: "text-emerald-500" };
  if (port.external === "Closed") return { icon: XCircle, color: "text-destructive" };
  // Unverified: lean on the reliable local signal for the icon tone.
  return port.locallyListening
    ? { icon: HelpCircle, color: "text-amber-500" }
    : { icon: XCircle, color: "text-muted-foreground" };
}
