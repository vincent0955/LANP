import { CheckCircle2, HelpCircle, Loader2, Wifi, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Skeleton } from "@/components/ui/skeleton";
import { CopyableCommand } from "@/components/CopyableCommand";
import { useForwardingGuide, useReachabilityTest } from "@/api/queries";
import type { PortReachability, ServerDetail } from "@/api/types";

/**
 * "Let friends join" panel (WS2): exact per-server forwarding instructions plus
 * an on-demand connectivity test (CGNAT detection + per-port local/outside-in
 * reachability). The app only diagnoses and instructs — it never changes the
 * user's network itself.
 */
export function ConnectivityPanel({ server }: { server: ServerDetail }) {
  const guide = useForwardingGuide(server.name);
  const test = useReachabilityTest(server.name);
  const result = test.data;

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Let friends join</CardTitle>
        <CardDescription>
          Forward these ports once on your router, then test that players can reach the server.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {guide.isPending ? (
          <Skeleton className="h-40" />
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

            <div>
              <p className="mb-2 text-sm font-medium">Router port forwarding</p>
              {guide.data.rules.length === 0 ? (
                <p className="text-sm text-muted-foreground">This server exposes no ports.</p>
              ) : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b text-left text-xs text-muted-foreground">
                      <th className="py-1.5 font-medium">External port</th>
                      <th className="py-1.5 font-medium">Protocol</th>
                      <th className="py-1.5 font-medium">Internal IP</th>
                      <th className="py-1.5 font-medium">Internal port</th>
                    </tr>
                  </thead>
                  <tbody>
                    {guide.data.rules.map((rule) => (
                      <tr key={`${rule.name}-${rule.protocol}`} className="border-b last:border-0">
                        <td className="py-1.5 font-mono text-xs font-semibold">{rule.port}</td>
                        <td className="py-1.5">{rule.protocol}</td>
                        <td className="py-1.5 font-mono text-xs">
                          {guide.data.lanAddresses[0] ?? "this PC"}
                        </td>
                        <td className="py-1.5 font-mono text-xs">{rule.port}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </div>

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

            <div className="space-y-3 border-t pt-3">
              <div className="flex items-center justify-between gap-3">
                <p className="text-sm text-muted-foreground">
                  Check whether players on the internet can currently reach this server.
                </p>
                <Button
                  variant="outline"
                  onClick={() => test.mutate()}
                  disabled={test.isPending}
                >
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
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
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
