import { useState } from "react";
import { CheckCircle2, Globe, TriangleAlert, Wifi } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useNetworkInfo } from "@/api/queries";
import { useConnectivity } from "@/lib/connectivity";
import { PortForwardWizard, type PortForwardStep } from "./PortForwardWizard";

/**
 * Machine-wide network summary on the Setup screen (WS2): LAN/public address,
 * the one-time forward range, and a CGNAT hint. Also hosts the whole-range
 * tutorial and connection test, for users who'd rather open every port once
 * than repeat the per-server wizard on each server's Overview tab.
 */
export function NetworkCard() {
  const network = useNetworkInfo();
  const verified = useConnectivity((s) => s.verified);
  const [wizardStep, setWizardStep] = useState<PortForwardStep | null>(null);

  if (!network.isSuccess) return null;

  const { lanAddresses, publicAddress, nodePortRangeStart, nodePortRangeEnd } = network.data;
  const cgnat = isLikelyCgnat(publicAddress);
  const portCount = nodePortRangeEnd - nodePortRangeStart + 1;

  return (
    <>
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Globe className="size-5" /> Network
          </CardTitle>
          <CardDescription>How players reach your servers.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="divide-y text-sm">
            <Row label="Local address" value={lanAddresses[0] ?? "—"} />
            <Row label="Public address" value={publicAddress ?? "unknown"} />
            <Row
              label="Forward once"
              value={`${nodePortRangeStart}–${nodePortRangeEnd} (TCP & UDP)`}
            />
          </div>

          {cgnat && (
            <Alert variant="destructive">
              <TriangleAlert className="size-4" />
              <AlertTitle>Carrier-Grade NAT likely</AlertTitle>
              <AlertDescription>
                Your public address looks like a shared/CGNAT address, so router port forwarding
                won't make servers reachable. Use a tunnel (e.g. Tailscale) or ask your ISP for a
                public IP.
              </AlertDescription>
            </Alert>
          )}

          <p className="text-xs text-muted-foreground">
            Open all {portCount} ports once and every server — the ones you have and the ones you
            make later — is ready to go. Or skip this and set servers up one at a time from their
            Overview tab.
          </p>

          <div className="flex flex-wrap items-center gap-2">
            <Button onClick={() => setWizardStep("firewall")}>
              Open all ports (one-time setup)
            </Button>
            <Button variant="outline" onClick={() => setWizardStep("test")}>
              <Wifi className="size-4" /> Test public connection
            </Button>
            {verified && (
              <span className="flex items-center gap-1.5 text-xs font-medium text-emerald-600">
                <CheckCircle2 className="size-4" /> Verified
              </span>
            )}
          </div>
        </CardContent>
      </Card>

      <PortForwardWizard
        open={wizardStep !== null}
        initialStep={wizardStep ?? "firewall"}
        onClose={() => setWizardStep(null)}
      />
    </>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between gap-4 py-1.5">
      <span className="text-muted-foreground">{label}</span>
      <span className="text-right font-mono text-xs font-medium">{value}</span>
    </div>
  );
}

/** Client-side hint only; the server-side probe is authoritative (ConnectionWizard). */
function isLikelyCgnat(ip: string | null): boolean {
  if (!ip) return false;
  const parts = ip.split(".").map(Number);
  if (parts.length !== 4 || parts.some((n) => Number.isNaN(n))) return false;
  const [a, b] = parts;
  if (a === 100 && b >= 64 && b <= 127) return true; // 100.64.0.0/10 CGNAT
  if (a === 10) return true;
  if (a === 172 && b >= 16 && b <= 31) return true;
  if (a === 192 && b === 168) return true;
  return false;
}
