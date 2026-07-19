import { useState } from "react";
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
import { Label } from "@/components/ui/label";
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group";
import { useDeleteServer } from "@/api/queries";
import { toastApiError } from "@/lib/errors";

interface Props {
  serverName: string | null;
  onClose: () => void;
  /** Called after successful deletion (e.g. to navigate away from a detail page). */
  onDeleted?: () => void;
}

export function DeleteServerDialog({ serverName, onClose, onDeleted }: Props) {
  const [wipeData, setWipeData] = useState(false);
  const deleteServer = useDeleteServer();

  const confirm = () => {
    if (!serverName) return;
    deleteServer.mutate(
      { name: serverName, deleteData: wipeData },
      {
        onSuccess: () => {
          toast.success(`Deleted '${serverName}'${wipeData ? " and its saved data" : ""}.`);
          onClose();
          onDeleted?.();
        },
        onError: toastApiError,
      },
    );
  };

  return (
    <Dialog open={serverName !== null} onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Delete {serverName}?</DialogTitle>
          <DialogDescription>
            The server will be removed from this machine. Choose what happens to its saved data
            (world files, configs, downloaded game files).
          </DialogDescription>
        </DialogHeader>
        <RadioGroup
          value={wipeData ? "wipe" : "keep"}
          onValueChange={(v) => setWipeData(v === "wipe")}
          className="gap-3"
        >
          <div className="flex items-start gap-2">
            <RadioGroupItem value="keep" id="keep-data" className="mt-0.5" />
            <div>
              <Label htmlFor="keep-data">Keep saved data</Label>
              <p className="text-xs text-muted-foreground">
                Redeploying a server with the same name picks the data back up (no re-download).
              </p>
            </div>
          </div>
          <div className="flex items-start gap-2">
            <RadioGroupItem value="wipe" id="wipe-data" className="mt-0.5" />
            <div>
              <Label htmlFor="wipe-data" className="text-destructive">
                Wipe saved data
              </Label>
              <p className="text-xs text-muted-foreground">
                Deletes the persistent volume. Worlds and game files are gone for good.
              </p>
            </div>
          </div>
        </RadioGroup>
        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={deleteServer.isPending}>
            Cancel
          </Button>
          <Button variant="destructive" onClick={confirm} disabled={deleteServer.isPending}>
            {deleteServer.isPending ? "Deleting…" : wipeData ? "Delete and wipe data" : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
