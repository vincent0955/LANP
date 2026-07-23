import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Play } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useScaleServer, useServer, useServerSecrets } from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import { GameArt } from "@/components/GameArt";
import { cn } from "@/lib/utils";
import { DeleteServerDialog } from "../DeleteServerDialog";
import { OverviewTab } from "./OverviewTab";
import { ConfigTab } from "./ConfigTab";
import { LogsTab } from "./LogsTab";
import { ServerSecretsTab } from "./ServerSecretsTab";
import { BackupsTab } from "./BackupsTab";
import { ConnectionWizard } from "./ConnectionWizard";
import { primaryBtn, secondaryBtn } from "./ui";
import type { ServerStatus } from "@/api/types";

export type DetailTab = "overview" | "config" | "secrets" | "logs" | "backups";

const STATUS_LINE: Record<ServerStatus, { label: string; color: string }> = {
  Running: { label: "Running", color: "#1d7a51" },
  Pending: { label: "Starting…", color: "#8a6d1f" },
  Stopped: { label: "Stopped", color: "#8795a3" },
  Error: { label: "Error", color: "#b0433f" },
  Unknown: { label: "Unknown", color: "#8795a3" },
};

export function ServerDetailPage() {
  const { name = "" } = useParams();
  const navigate = useNavigate();
  const server = useServer(name);
  const scale = useScaleServer(name);
  // Only games whose template declares secretKeyRefs get a Secrets tab; the list
  // is empty for everything else, so the tab stays hidden.
  const secrets = useServerSecrets(name);
  const hasSecrets = (secrets.data?.length ?? 0) > 0;

  const [tab, setTab] = useState<DetailTab>("overview");
  const [helpOpen, setHelpOpen] = useState(false);
  const [deleting, setDeleting] = useState(false);

  if (server.isPending) {
    return (
      <div className="mx-auto w-full max-w-[1080px] space-y-4 px-10 py-9">
        <Skeleton className="h-16 w-1/2" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (server.isError) {
    return (
      <div className="mx-auto w-full max-w-[1080px] space-y-4 px-10 py-9">
        <Link
          to="/"
          className="text-[13px] font-semibold text-[#8795a3] transition-colors hover:text-foreground"
        >
          ← All games
        </Link>
        <Alert variant="destructive">
          <AlertTitle>Could not load server</AlertTitle>
          <AlertDescription>{server.error.message}</AlertDescription>
        </Alert>
      </div>
    );
  }

  const data = server.data;
  const running = data.replicas > 0;
  const status = STATUS_LINE[data.status] ?? STATUS_LINE.Unknown;

  const tabs: { id: DetailTab; label: string }[] = [
    { id: "overview", label: "Overview" },
    { id: "config", label: "Config" },
    ...(hasSecrets ? [{ id: "secrets" as const, label: "Secrets" }] : []),
    { id: "logs", label: "Logs" },
    { id: "backups", label: "Backups" },
  ];

  return (
    <div className="mx-auto w-full max-w-[1080px] px-10 py-9">
      <Link
        to="/"
        className="mb-[18px] inline-block text-[13px] font-semibold text-[#8795a3] transition-colors hover:text-foreground"
      >
        ← All games
      </Link>

      {/* Header */}
      <div className="mb-[30px] flex items-center gap-[18px]">
        <div className="relative size-16 shrink-0 overflow-hidden rounded-[10px]">
          <GameArt imageTag={data.image} name={data.game} variant="icon" />
        </div>
        <div className="min-w-0">
          <h1 className="truncate text-[27px] font-extrabold leading-[1.1] tracking-[-0.02em]">
            {data.game}
          </h1>
          <div
            className="mt-[5px] flex items-center gap-[7px] text-[13.5px] font-semibold"
            style={{ color: status.color }}
          >
            <span
              className="inline-block size-2 rounded-full"
              style={{ background: status.color }}
            />
            {status.label}
            <span className="font-mono text-xs font-normal text-[#9aa7b4]">{data.name}</span>
          </div>
        </div>
        <div className="ml-auto flex items-center gap-2.5">
          <button
            type="button"
            className={cn(running ? secondaryBtn : primaryBtn, "px-[26px] py-[13px] text-[13.5px]")}
            onClick={() => scale.mutate(running ? 0 : 1, { onError: toastApiError })}
            disabled={scale.isPending}
          >
            {!running && <Play className="size-3.5 fill-current" />}
            {scale.isPending ? "…" : running ? "Stop" : "Start"}
          </button>
          <button
            type="button"
            title="Connection help"
            onClick={() => setHelpOpen(true)}
            className="flex size-11 cursor-pointer items-center justify-center rounded-[6px] border border-input bg-white text-lg font-bold text-[#45596b] transition-colors hover:border-primary hover:bg-accent hover:text-primary-foreground"
          >
            ?
          </button>
        </div>
      </div>

      {/* Tab bar */}
      <div className="mb-6 flex gap-1 border-b">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => setTab(t.id)}
            className={cn(
              "-mb-px cursor-pointer border-b-2 px-4 py-2.5 text-[13.5px] font-semibold transition-colors",
              tab === t.id
                ? "border-primary text-foreground"
                : "border-transparent text-[#8795a3] hover:text-foreground",
            )}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === "overview" && (
        <OverviewTab
          server={data}
          onOpenHelp={() => setHelpOpen(true)}
          onGoTab={setTab}
          onDelete={() => setDeleting(true)}
        />
      )}
      {tab === "config" && <ConfigTab serverName={data.name} hasSecrets={hasSecrets} onGoSecrets={() => setTab("secrets")} />}
      {tab === "secrets" && hasSecrets && <ServerSecretsTab serverName={data.name} />}
      {tab === "logs" && <LogsTab server={data} />}
      {tab === "backups" && <BackupsTab server={data} />}

      <ConnectionWizard server={data} open={helpOpen} onClose={() => setHelpOpen(false)} />

      <DeleteServerDialog
        serverName={deleting ? data.name : null}
        onClose={() => setDeleting(false)}
        onDeleted={() => navigate("/")}
      />
    </div>
  );
}
