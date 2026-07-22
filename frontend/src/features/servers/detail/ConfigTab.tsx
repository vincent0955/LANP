import { useEffect, useState } from "react";
import { toast } from "sonner";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useServerConfig, useUpdateConfig } from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import { primaryBtn } from "./ui";

// Friendly names for well-known template env keys (design handoff → Config tab);
// anything unlisted falls back to a prettified version of the key itself.
const FRIENDLY_LABELS: Record<string, string> = {
  DIFFICULTY: "Difficulty",
  MODE: "Game mode",
  GAMEMODE: "Game mode",
  MAX_PLAYERS: "Max players",
  MAXPLAYERS: "Max players",
  MOTD: "Server message",
  MODRINTH_MODPACK: "Modpack",
  MODRINTH_PROJECTS: "Mods & plugins",
  MOD_PLATFORM: "Mod platform",
  MEMORY: "Memory",
  VERSION: "Version",
  TYPE: "Server software",
  LEVEL: "World name",
  SEED: "World seed",
  WORLD: "World name",
  MAP: "Map",
  SERVER_NAME: "Server name",
  SERVER_PASSWORD: "Server password",
  ONLINE_MODE: "Online mode",
  PVP: "PvP",
  HARDCORE: "Hardcore",
  VIEW_DISTANCE: "View distance",
};

function friendlyLabel(key: string): string {
  if (FRIENDLY_LABELS[key]) return FRIENDLY_LABELS[key];
  const words = key.toLowerCase().split(/[_-]+/).filter(Boolean);
  if (words.length === 0) return key;
  return words
    .map((word, i) => (i === 0 ? word[0].toUpperCase() + word.slice(1) : word))
    .join(" ");
}

interface Props {
  serverName: string;
  hasSecrets: boolean;
  onGoSecrets: () => void;
}

export function ConfigTab({ serverName, hasSecrets, onGoSecrets }: Props) {
  const config = useServerConfig(serverName);
  const update = useUpdateConfig(serverName);
  const [draft, setDraft] = useState<Record<string, string>>({});

  // Reset the draft whenever fresh config arrives and nothing is being edited.
  useEffect(() => {
    if (config.data) setDraft(config.data);
  }, [config.data]);

  if (config.isPending) return <Skeleton className="h-64" />;
  if (config.isError) {
    return (
      <Alert variant="destructive">
        <AlertTitle>Could not load configuration</AlertTitle>
        <AlertDescription>{config.error.message}</AlertDescription>
      </Alert>
    );
  }

  const keys = Object.keys(config.data).sort();
  const dirty = keys.some((key) => (draft[key] ?? "") !== config.data[key]);

  const save = () => {
    update.mutate(draft, {
      onSuccess: () =>
        toast.success("Configuration saved.", {
          description: "Restart the server (Stop, then Start) for changes to take effect.",
        }),
      onError: toastApiError,
    });
  };

  return (
    <div>
      <div className="rounded-[10px] border bg-card px-[30px] pb-[26px] pt-3">
        {keys.length === 0 ? (
          <p className="py-4 text-sm text-muted-foreground">
            This server has no editable configuration.
          </p>
        ) : (
          keys.map((key) => (
            <div
              key={key}
              className="flex items-center gap-6 border-b border-[#eef3f7] py-[13px]"
            >
              <div className="w-[220px] shrink-0">
                <div className="text-sm font-semibold">{friendlyLabel(key)}</div>
                <div className="mt-0.5 truncate font-mono text-[11px] text-[#9aa7b4]" title={key}>
                  {key}
                </div>
              </div>
              <input
                value={draft[key] ?? ""}
                onChange={(e) => setDraft((d) => ({ ...d, [key]: e.target.value }))}
                className="flex-1 rounded-[6px] border border-input bg-[#fbfdfe] px-[15px] py-2.5 font-mono text-[13px] text-foreground outline-none transition-shadow focus:border-primary focus:shadow-[0_0_0_3px_rgba(92,200,255,0.25)]"
              />
            </div>
          ))
        )}

        <div className="mt-5 flex items-center gap-4">
          <button
            type="button"
            className={primaryBtn}
            onClick={save}
            disabled={!dirty || update.isPending}
          >
            {update.isPending ? "Saving…" : "Save changes"}
          </button>
          <span className="text-[12.5px] text-[#8795a3]">
            Saving restarts the server if it's running.
          </span>
          {dirty && !update.isPending && (
            <button
              type="button"
              onClick={() => setDraft(config.data)}
              className="ml-auto cursor-pointer text-[13px] font-semibold text-[#8795a3] transition-colors hover:text-foreground"
            >
              Discard changes
            </button>
          )}
        </div>
      </div>

      {hasSecrets && (
        <p className="mt-3.5 text-[13px] text-[#8795a3]">
          Sensitive values (Steam tokens, RCON passwords) aren't listed here — they're managed on
          the{" "}
          <button
            type="button"
            onClick={onGoSecrets}
            className="cursor-pointer font-semibold text-[#2596d1] transition-colors hover:text-foreground"
          >
            Secrets
          </button>{" "}
          tab.
        </p>
      )}
    </div>
  );
}
