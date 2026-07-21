import { useState } from "react";
import { Download, RotateCcw, Save, Trash2, TriangleAlert } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { api } from "@/api/endpoints";
import { useBackups, useCreateBackup, useDeleteBackup, useRestoreBackup } from "@/api/queries";
import { formatBytes, formatDateTime } from "@/lib/format";
import { toastApiError } from "@/lib/errors";
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
    <div className="space-y-4">
      <div className="flex items-center justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          Snapshots of this server's world data, saved to this machine. Safe to keep even if the
          runtime is reset.
        </p>
        <Button onClick={backUpNow} disabled={create.isPending}>
          <Save className="size-4" />
          {create.isPending ? "Backing up…" : "Back up now"}
        </Button>
      </div>

      {running && (
        <Alert>
          <TriangleAlert className="size-4" />
          <AlertTitle>Restoring is disabled while the server is running</AlertTitle>
          <AlertDescription>
            Stop the server first to restore a backup. Backing up now still works, but a snapshot of
            a live world is best-effort — stop the server for a guaranteed-consistent copy.
          </AlertDescription>
        </Alert>
      )}

      {backups.isPending ? (
        <Skeleton className="h-32" />
      ) : backups.isError ? (
        <Alert variant="destructive">
          <AlertTitle>Could not load backups</AlertTitle>
          <AlertDescription>{backups.error.message}</AlertDescription>
        </Alert>
      ) : backups.data.length === 0 ? (
        <div className="rounded-md border border-dashed p-8 text-center text-sm text-muted-foreground">
          No backups yet. Use “Back up now” to save this server's world.
        </div>
      ) : (
        <div className="divide-y rounded-md border">
          {backups.data.map((backup) => (
            <div key={backup.id} className="flex flex-wrap items-center justify-between gap-3 p-3">
              <div className="min-w-0">
                <div className="font-medium">{formatDateTime(backup.createdAt)}</div>
                <div className="text-xs text-muted-foreground">{formatBytes(backup.sizeBytes)}</div>
              </div>
              <div className="flex items-center gap-1">
                {pendingRestore === backup.id ? (
                  <Button
                    variant="secondary"
                    size="sm"
                    disabled={restore.isPending || running}
                    onClick={() => restoreBackup(backup.id)}
                  >
                    Confirm restore
                  </Button>
                ) : (
                  <Button
                    variant="ghost"
                    size="sm"
                    disabled={running}
                    title={running ? "Stop the server to restore" : "Replace the world with this backup"}
                    onClick={() => restoreBackup(backup.id)}
                  >
                    <RotateCcw className="size-4" />
                    Restore
                  </Button>
                )}
                <Button
                  variant="ghost"
                  size="icon"
                  title="Download this backup"
                  disabled={downloading === backup.id}
                  onClick={() => download(backup.id)}
                >
                  <Download className="size-4" />
                </Button>
                {pendingDelete === backup.id ? (
                  <Button
                    variant="destructive"
                    size="sm"
                    disabled={remove.isPending}
                    onClick={() => deleteBackup(backup.id)}
                  >
                    Confirm delete
                  </Button>
                ) : (
                  <Button
                    variant="ghost"
                    size="icon"
                    className="text-destructive hover:text-destructive"
                    title="Delete this backup"
                    onClick={() => deleteBackup(backup.id)}
                  >
                    <Trash2 className="size-4" />
                  </Button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
