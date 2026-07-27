import { useState } from "react";
import { Loader2 } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { useRuntimeStatus, useSetRuntimeMode } from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import { restartApp } from "@/lib/restart";
import { FirewallCommands } from "./FirewallCommands";
import type { RuntimeMode } from "@/api/types";

/**
 * Container-engine choice on the Setup screen: the app-managed bundled runtime
 * (default) or a Docker Desktop the user already runs on the machine — the
 * common case for self-hosters who don't want a second VM's worth of RAM for
 * an engine they've already got.
 *
 * Switching is restart-gated on purpose. The backend picks its Docker endpoint,
 * decides whether to auto-start the WSL runtime, and starts its engine watchers
 * at boot, so a live switch would leave half the app pointed at the old engine.
 */
export function EngineCard() {
  const runtime = useRuntimeStatus();
  const setMode = useSetRuntimeMode();
  const [pending, setPending] = useState<RuntimeMode | null>(null);
  const [restarting, setRestarting] = useState(false);

  // Linux has no bundled runtime to opt out of — the native daemon already is
  // "the engine the user already runs", so the choice would be a no-op.
  if (!runtime.isSuccess || runtime.data.platform !== "windows") {
    return null;
  }

  const mode = runtime.data.mode;
  const useDockerDesktop = mode === "DockerDesktop";

  const confirmSwitch = () => {
    if (!pending) return;
    setRestarting(true);
    setMode.mutate(pending, {
      onSuccess: async () => {
        // The app relaunches out from under us; no success toast would ever be
        // seen. Outside Tauri (browser dev) restartApp reports back instead.
        const restarted = await restartApp();
        if (!restarted) {
          setRestarting(false);
          setPending(null);
          toast.success("Engine saved — restart the app to apply it.");
        }
      },
      onError: (error) => {
        setRestarting(false);
        setPending(null);
        toastApiError(error);
      },
    });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Container engine</CardTitle>
        <CardDescription>
          Where your game servers actually run. The bundled runtime is managed entirely by this app;
          Docker Desktop is yours to start and stop.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex items-start justify-between gap-4">
          <div className="space-y-1">
            <Label htmlFor="use-docker-desktop" className="text-sm font-medium">
              Use my Docker Desktop
            </Label>
            <p className="text-sm text-muted-foreground">
              {useDockerDesktop
                ? "Servers run on your own Docker engine. The app won't install or start a runtime of its own."
                : "Off — the app runs its own lightweight VM. Turn this on if you already run Docker Desktop on this PC."}
            </p>
          </div>
          <Switch
            id="use-docker-desktop"
            checked={useDockerDesktop}
            disabled={setMode.isPending || restarting}
            onCheckedChange={(checked) => setPending(checked ? "DockerDesktop" : "Bundled")}
          />
        </div>

        {useDockerDesktop && !runtime.data.engineReachable && (
          <p className="text-sm text-amber-600 dark:text-amber-500">
            Docker Desktop isn't responding. Start it — your servers will come back on their own.
          </p>
        )}

        {/* RuntimeCard carries these in bundled mode, but it hides itself here. */}
        {useDockerDesktop && <FirewallCommands commands={runtime.data.firewallCommands} />}
      </CardContent>

      <Dialog
        open={pending !== null}
        onOpenChange={(open) => {
          if (!open && !restarting) setPending(null);
        }}
      >
        <DialogContent>
          <DialogHeader>
            <DialogTitle>
              {pending === "DockerDesktop" ? "Switch to Docker Desktop?" : "Switch to the bundled runtime?"}
            </DialogTitle>
            <DialogDescription asChild>
              <div className="space-y-2 text-sm">
                <p>
                  LANP needs to restart to change engines. Running servers are stopped and saved
                  first, so no one loses progress.
                </p>
                <p>
                  {pending === "DockerDesktop"
                    ? "Your servers move to Docker Desktop, which needs to be running for them to start. The bundled runtime stays installed if you want to switch back."
                    : "Your servers move back to the app's own runtime. Anything created while on Docker Desktop stays there, and reappears if you switch back."}
                </p>
              </div>
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setPending(null)} disabled={restarting}>
              Cancel
            </Button>
            <Button onClick={confirmSwitch} disabled={restarting}>
              {restarting && <Loader2 className="mr-2 size-4 animate-spin" />}
              {restarting ? "Restarting…" : "Restart now"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  );
}
