import { useState } from "react";
import { Download, RotateCcw, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { api } from "@/api/endpoints";
import { useBackups, useCreateBackup, useDeleteBackup, useRestoreBackup } from "@/api/queries";
import { formatBytes, formatFriendlyDateTime } from "@/lib/format";
import { toastApiError } from "@/lib/errors";
import { iconBtn, outlineBtn, primaryBtn } from "./ui";
import type { ServerDetail } from "@/api/types";

/**
 * World backup/restore for one server (WS1). Backups are game-agnostic snapshots
 * of the server's single data volume, stored as .tar.gz on the host. Restoring
 * requires the server to be stopped so an open save is never corrupted.
 */
export function BackupsTab({ server }: { server: ServerDetail }) {
  const backups = useBackups(server.name);
  const create = useCreateBackup(server.name);
  const restore = useRestoreBackup(server.name);
  const remove = useDeleteBackup(server.name);

  const running = server.replicas > 0;
  const [pendingRestore, setPendingRestore] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<string | null>(null);
  const [downloading, setDownloading] = useState<string | null>(null);

  const backUpNow = () => {
    create.mutate(undefined, {
      onSuccess: (info) =>
        toast.success("Backup created.", {
          description: `${formatBytes(info.sizeBytes)} saved to this machine.`,
        }),
      onError: toastApiError,
    });
  };

  const restoreBackup = (id: string) => {
    if (pendingRestore !== id) {
      setPendingRestore(id);
      return;
    }
    restore.mutate(id, {
      onSuccess: () => {
        toast.success("Backup restored.", { description: "The server's world was replaced." });
        setPendingRestore(null);
      },
      onError: (error) => {
        setPendingRestore(null);
        toastApiError(error);
      },
    });
  };

  const deleteBackup = (id: string) => {
    if (pendingDelete !== id) {
      setPendingDelete(id);
      return;
    }
    remove.mutate(id, {
      onSuccess: () => {
        toast.success("Backup deleted.");
        setPendingDelete(null);
      },
      onError: (error) => {
        setPendingDelete(null);
        toastApiError(error);
      },
    });
  };

  const download = async (id: string) => {
    setDownloading(id);
    try {
      await api.downloadBackup(server.name, id);
    } catch (error) {
      toastApiError(error);
    } finally {
      setDownloading(null);
    }
  };

  return (
    <div>
      <div className="mb-[18px] flex items-center justify-between gap-5">
        <p className="text-sm text-muted-foreground">
          Snapshots of this server's world data, saved to this machine. Safe to keep even if the
          runtime is reset.
        </p>
        <button
          type="button"
          className={`${primaryBtn} whitespace-nowrap px-[22px]`}
          onClick={backUpNow}
          disabled={create.isPending}
        >
          {create.isPending ? "Backing up…" : "Back up now"}
        </button>
      </div>

      {running && (
        <div className="mb-[18px] rounded-lg border border-[#f3e2bd] bg-[#fff8ec] px-[18px] py-[13px] text-[13.5px] text-[#7a5c1e]">
          <span className="font-bold">Restoring is disabled while the server is running.</span>{" "}
          Stop the server first to restore a backup. Backing up now still works, but a snapshot of
          a live world is best-effort — stop the server for a guaranteed-consistent copy.
        </div>
      )}

      {backups.isPending ? (
        <Skeleton className="h-32" />
      ) : backups.isError ? (
        <Alert variant="destructive">
          <AlertTitle>Could not load backups</AlertTitle>
          <AlertDescription>{backups.error.message}</AlertDescription>
        </Alert>
      ) : backups.data.length === 0 ? (
        <div className="rounded-[10px] border border-dashed p-8 text-center text-sm text-muted-foreground">
          No backups yet. Use “Back up now” to save this server's world.
        </div>
      ) : (
        <div className="flex flex-col gap-3">
          {backups.data.map((backup) => (
            <div
              key={backup.id}
              className="flex flex-wrap items-center gap-4 rounded-[10px] border bg-card px-6 py-[18px]"
            >
              <div className="min-w-0">
                <div className="text-[14.5px] font-bold">
                  {formatFriendlyDateTime(backup.createdAt)}
                </div>
                <div className="mt-0.5 text-[12.5px] text-[#8795a3]">
                  {formatBytes(backup.sizeBytes)}
                </div>
              </div>
              <div className="ml-auto flex items-center gap-2">
                {pendingRestore === backup.id ? (
                  <button
                    type="button"
                    className="cursor-pointer rounded-[6px] bg-secondary px-4 py-2 text-[13px] font-bold text-secondary-foreground transition hover:brightness-105 disabled:cursor-default disabled:opacity-50"
                    disabled={restore.isPending || running}
                    onClick={() => restoreBackup(backup.id)}
                  >
                    Confirm restore
                  </button>
                ) : (
                  <button
                    type="button"
                    className={outlineBtn}
                    disabled={running}
                    title={running ? "Stop the server to restore" : "Replace the world with this backup"}
                    onClick={() => restoreBackup(backup.id)}
                  >
                    <RotateCcw className="size-3.5" />
                    Restore
                  </button>
                )}
                <button
                  type="button"
                  className={iconBtn}
                  title="Download this backup"
                  disabled={downloading === backup.id}
                  onClick={() => void download(backup.id)}
                >
                  <Download className="size-4" />
                </button>
                {pendingDelete === backup.id ? (
                  <button
                    type="button"
                    className="cursor-pointer rounded-[6px] bg-[#b0433f] px-4 py-2 text-[13px] font-bold text-white transition hover:brightness-105 disabled:cursor-default disabled:opacity-50"
                    disabled={remove.isPending}
                    onClick={() => deleteBackup(backup.id)}
                  >
                    Confirm delete
                  </button>
                ) : (
                  <button
                    type="button"
                    className={`${iconBtn} hover:bg-[#fbf1f0] hover:text-[#b0433f]`}
                    title="Delete this backup"
                    onClick={() => deleteBackup(backup.id)}
                  >
                    <Trash2 className="size-4" />
                  </button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
