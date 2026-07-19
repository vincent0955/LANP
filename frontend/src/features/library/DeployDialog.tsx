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
import { useDeployServer, useServers } from "@/api/queries";
import { isValidServerName, SERVER_NAME_RULES, suggestServerName } from "@/lib/serverName";
import { toastApiError } from "@/lib/errors";
import { formatBytes } from "@/lib/format";
import type { GameTemplate } from "@/api/types";
import { MinecraftDeployForm } from "./MinecraftDeployForm";

interface Props {
  game: GameTemplate | null;
  onClose: () => void;
}

// Config keys that get a first-class labeled field above the generic config list.
// `required` blocks deploy while empty — the server would just crash-loop without it.
const FEATURED_KEYS: Record<
  string,
  { label: string; placeholder: string; hint: string; required: boolean }
> = {
  MODRINTH_MODPACK: {
    label: "Modpack",
    placeholder: "https://modrinth.com/modpack/…",
    hint: "Paste a Modrinth modpack page link (or its project slug). The pack is downloaded and installed on first start.",
    required: true,
  },
};

export function DeployDialog({ game, onClose }: Props) {
  if (!game) return null;
  // Minecraft (Java) gets the specialized software/version/content panel;
  // everything else uses the generic config form. Key the form state by the
  // selected game so switching games resets it.
  if (game.kind === "MinecraftJava") {
    return <MinecraftDeployForm key={game.imageTag} game={game} onClose={onClose} />;
  }
  return <DeployForm key={game.imageTag} game={game} onClose={onClose} />;
}

function DeployForm({ game, onClose }: { game: GameTemplate; onClose: () => void }) {
  const navigate = useNavigate();
  const deploy = useDeployServer();

  // The name stays a derived suggestion (unique against existing servers, so a
  // second deploy of the same game gets e.g. "minecraft-java-2") until the user
  // edits the field, at which point their text wins.
  const servers = useServers();
  const [editedName, setEditedName] = useState<string | null>(null);
  const name =
    editedName ?? suggestServerName(game.displayName, (servers.data ?? []).map((s) => s.name));
  const [config, setConfig] = useState<Record<string, string>>(() => ({ ...game.defaultConfig }));
  const [touched, setTouched] = useState(false);

  const nameValid = isValidServerName(name);
  const featuredKeys = useMemo(
    () => Object.keys(game.defaultConfig).filter((key) => key in FEATURED_KEYS),
    [game],
  );
  const configKeys = useMemo(
    () => Object.keys(game.defaultConfig).filter((key) => !(key in FEATURED_KEYS)),
    [game],
  );
  const secretKeys = useMemo(() => new Set(Object.keys(game.secretKeyRefs)), [game]);
  const missingRequired = featuredKeys.filter(
    (key) => FEATURED_KEYS[key].required && !(config[key] ?? "").trim(),
  );

  const submit = () => {
    setTouched(true);
    if (!nameValid || missingRequired.length > 0) return;

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
              onChange={(e) => setEditedName(e.target.value)}
              onBlur={() => setTouched(true)}
              aria-invalid={touched && !nameValid}
            />
            {touched && !nameValid && (
              <p className="text-xs text-destructive">{SERVER_NAME_RULES}</p>
            )}
          </div>

          {featuredKeys.map((key) => {
            const meta = FEATURED_KEYS[key];
            const missing = touched && meta.required && !(config[key] ?? "").trim();
            return (
              <div key={key} className="space-y-2">
                <Label htmlFor={`featured-${key}`}>{meta.label}</Label>
                <Input
                  id={`featured-${key}`}
                  value={config[key] ?? ""}
                  placeholder={meta.placeholder}
                  onChange={(e) => setConfig((c) => ({ ...c, [key]: e.target.value }))}
                  aria-invalid={missing}
                />
                <p className={`text-xs ${missing ? "text-destructive" : "text-muted-foreground"}`}>
                  {meta.hint}
                </p>
              </div>
            );
          })}

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
                  {[...secretKeys].join(", ")} are secrets — set them on the server's Secrets tab after
                  it's created. The server won't start until they're set.
                </p>
              )}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={deploy.isPending}>
            Cancel
          </Button>
          <Button
            onClick={submit}
            disabled={deploy.isPending || (touched && (!nameValid || missingRequired.length > 0))}
          >
            {deploy.isPending ? "Deploying…" : "Deploy"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
