import { useState } from "react";
import { AlertTriangle, CheckCircle2, Copy, Loader2, XCircle } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  useEnableMirroredNetworking,
  useInstallRuntime,
  useRuntimeStatus,
} from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import type { RuntimePhase } from "@/api/types";

const PHASE_LABELS: Partial<Record<RuntimePhase, string>> = {
  InstallingWsl: "Installing WSL (approve the administrator prompt)…",
  DownloadingDistro: "Downloading the runtime…",
  ImportingDistro: "Importing the runtime…",
  StartingEngine: "Starting the engine…",
};

/**
 * Windows bundled-runtime install and networking flow on the Setup screen
 * (docs/docker-migration.md → Part 2). Hidden entirely when the engine is
 * already reachable and fully configured, and on Linux (native engine — the
 * install script is a docs concern, not a UI flow).
 */
export function RuntimeCard() {
  const runtime = useRuntimeStatus();
  const install = useInstallRuntime();
  const mirrored = useEnableMirroredNetworking();
  const [shutdownNeeded, setShutdownNeeded] = useState(false);

  if (!runtime.isSuccess) {
    return null;
  }

  const status = runtime.data;
  const installing = PHASE_LABELS[status.phase] !== undefined;
  const allGood =
    status.engineReachable && (status.platform !== "windows" || status.mirroredNetworkingConfigured);

  if (status.platform !== "windows") {
    // Linux: the engine is native; only surface anything when it's missing.
    if (status.engineReachable) return null;
    return (
      <Card>
        <CardHeader>
          <CardTitle>Container runtime</CardTitle>
          <CardDescription>No Docker engine was found on this machine.</CardDescription>
        </CardHeader>
        <CardContent>
          <p className="text-sm text-muted-foreground">
            Install it once with the bundled script, then restart the dashboard:
          </p>
          <CopyableCommand command="sudo scripts/runtime/install-linux.sh" />
        </CardContent>
      </Card>
    );
  }

  if (allGood && !installing && status.phase !== "Failed" && status.phase !== "AwaitingReboot") {
    return null;
  }

  const startInstall = () =>
    install.mutate(undefined, {
      onError: toastApiError,
    });

  const enableMirrored = () =>
    mirrored.mutate(undefined, {
      onSuccess: ({ applied, shutdownRequired }) => {
        if (applied) {
          toast.success("Mirrored networking enabled.");
          setShutdownNeeded(shutdownRequired);
        } else {
          toast.info("Mirrored networking was already enabled.");
        }
      },
      onError: toastApiError,
    });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Container runtime</CardTitle>
        <CardDescription>
          Game servers run in a lightweight VM the app manages — no Docker Desktop needed.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="divide-y">
          <RuntimeCheck ok={status.wslInstalled} label="WSL 2 installed" />
          <RuntimeCheck ok={status.distroImported} label="Runtime imported" />
          <RuntimeCheck ok={status.engineReachable} label="Engine running" />
          <RuntimeCheck
            ok={status.mirroredNetworkingConfigured}
            optional
            label="Mirrored networking (needed for friends to join)"
          />
        </div>

        {installing && (
          <div className="space-y-2">
            <p className="flex items-center gap-2 text-sm">
              <Loader2 className="size-4 animate-spin" />
              {PHASE_LABELS[status.phase]}
            </p>
            {status.phase === "DownloadingDistro" && status.downloadPercent !== null && (
              <Progress value={status.downloadPercent} />
            )}
          </div>
        )}

        {status.phase === "AwaitingReboot" && (
          <Alert>
            <AlertTriangle className="size-4" />
            <AlertTitle>Restart Windows to finish</AlertTitle>
            <AlertDescription>
              WSL was installed but needs a reboot. After restarting, come back here and press
              Install again.
            </AlertDescription>
          </Alert>
        )}

        {status.phase === "Failed" && status.error && (
          <Alert variant="destructive">
            <AlertTitle>Runtime setup failed</AlertTitle>
            <AlertDescription>{status.error}</AlertDescription>
          </Alert>
        )}

        {!status.engineReachable && !installing && status.phase !== "AwaitingReboot" && (
          <Button onClick={startInstall} disabled={install.isPending}>
            {status.distroImported ? "Start runtime" : "Install runtime"}
          </Button>
        )}

        {status.engineReachable && !status.mirroredNetworkingConfigured && (
          <div className="space-y-2">
            <p className="text-sm text-muted-foreground">
              Without mirrored networking, other players can't reach your servers (UDP games break
              behind WSL's NAT). Enabling it edits your .wslconfig; the runtime restarts next time
              WSL does.
            </p>
            <Button variant="outline" onClick={enableMirrored} disabled={mirrored.isPending}>
              Enable mirrored networking
            </Button>
            {shutdownNeeded && (
              <p className="text-xs text-muted-foreground">
                Applies after WSL restarts — run <code>wsl --shutdown</code> when no one is playing,
                then start a server again.
              </p>
            )}
          </div>
        )}

        {status.firewallCommands.length > 0 && (
          <details className="text-sm">
            <summary className="cursor-pointer text-muted-foreground">
              Firewall commands (run once as administrator so players can connect)
            </summary>
            <div className="mt-2 space-y-2">
              {status.firewallCommands.map((command) => (
                <CopyableCommand key={command} command={command} />
              ))}
            </div>
          </details>
        )}
      </CardContent>
    </Card>
  );
}

function RuntimeCheck({ ok, optional, label }: { ok: boolean; optional?: boolean; label: string }) {
  const Icon = ok ? CheckCircle2 : optional ? AlertTriangle : XCircle;
  const color = ok ? "text-emerald-500" : optional ? "text-amber-500" : "text-destructive";
  return (
    <div className="flex items-center gap-3 py-2">
      <Icon className={`size-5 shrink-0 ${color}`} />
      <p className="text-sm">{label}</p>
    </div>
  );
}

function CopyableCommand({ command }: { command: string }) {
  return (
    <div className="flex items-start gap-2">
      <code className="grow overflow-x-auto whitespace-pre rounded bg-muted px-2 py-1.5 font-mono text-xs">
        {command}
      </code>
      <Button
        variant="ghost"
        size="icon"
        className="shrink-0"
        onClick={() => {
          void navigator.clipboard.writeText(command);
          toast.success("Copied.");
        }}
      >
        <Copy className="size-4" />
      </Button>
    </div>
  );
}
