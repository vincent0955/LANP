import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useDeployServer } from "@/api/queries";
import { isValidServerName, SERVER_NAME_RULES, suggestServerName } from "@/lib/serverName";
import { toastApiError } from "@/lib/errors";
import { formatBytes } from "@/lib/format";
import type { GameTemplate } from "@/api/types";

interface Props {
  game: GameTemplate | null;
  onClose: () => void;
}

export function DeployDialog({ game, onClose }: Props) {
  // Key the form state by the selected game so switching games resets it.
  return game ? <DeployForm key={game.imageTag} game={game} onClose={onClose} /> : null;
}

function DeployForm({ game, onClose }: { game: GameTemplate; onClose: () => void }) {
  const navigate = useNavigate();
  const deploy = useDeployServer();

  const [name, setName] = useState(() => suggestServerName(game.displayName));
  const [config, setConfig] = useState<Record<string, string>>(() => ({ ...game.defaultConfig }));
  const [touched, setTouched] = useState(false);

  const nameValid = isValidServerName(name);
  const configKeys = useMemo(() => Object.keys(game.defaultConfig), [game]);
  const secretKeys = useMemo(() => new Set(Object.keys(game.secretKeyRefs)), [game]);

  const submit = () => {
    setTouched(true);
    if (!nameValid) return;

    // Only send values the user actually changed from the template defaults.
    const overrides: Record<string, string> = {};
    for (const key of configKeys) {
      if (config[key] !== game.defaultConfig[key]) overrides[key] = config[key] ?? "";
    }

    deploy.mutate(
      {
        name,
        imageTag: game.imageTag,
        configOverrides: Object.keys(overrides).length > 0 ? overrides : null,
      },
      {
        onSuccess: () => {
          toast.success(`Deploying '${name}'`, {
            description: "First-time deploys download the game inside the container — this can take a while.",
          });
          onClose();
          navigate("/");
        },
        onError: toastApiError,
      },
    );
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>Deploy {game.displayName}</DialogTitle>
          <DialogDescription>
            {game.defaultResources.memoryLimit} memory · {game.defaultResources.cpuLimit} CPU ·{" "}
            {formatBytes(game.defaultStorageBytes)} storage
            {game.steamAppId ? ` · Steam app ${game.steamAppId}` : ""}
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4">
          <div className="space-y-2">
            <Label htmlFor="server-name">Server name</Label>
            <Input
              id="server-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              onBlur={() => setTouched(true)}
              aria-invalid={touched && !nameValid}
            />
            {touched && !nameValid && (
              <p className="text-xs text-destructive">{SERVER_NAME_RULES}</p>
            )}
          </div>

          {configKeys.length > 0 && (
            <div className="space-y-2">
              <Label>Configuration</Label>
              {/* Plain overflow div, not Radix ScrollArea: the latter doesn't
                  constrain its viewport from a max-h on the root, letting the
                  inputs spill over the dialog footer. */}
              <div className="max-h-64 space-y-2 overflow-y-auto rounded-md border p-3">
                {configKeys.map((key) => (
                  <div key={key} className="grid grid-cols-[1fr_1.2fr] items-center gap-2">
                    <span className="truncate font-mono text-xs" title={key}>
                      {key}
                    </span>
                    <Input
                      className="h-8 font-mono text-xs"
                      value={config[key] ?? ""}
                      onChange={(e) => setConfig((c) => ({ ...c, [key]: e.target.value }))}
                    />
                  </div>
                ))}
              </div>
              {secretKeys.size > 0 && (
                <p className="text-xs text-muted-foreground">
                  {[...secretKeys].join(", ")} come from cluster secrets — configure them on the
                  Setup screen.
                </p>
              )}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={deploy.isPending}>
            Cancel
          </Button>
          <Button onClick={submit} disabled={deploy.isPending || (touched && !nameValid)}>
            {deploy.isPending ? "Deploying…" : "Deploy"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
