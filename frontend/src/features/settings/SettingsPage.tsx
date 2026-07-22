import { useEffect, useState } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useBackupSettings, useUpdateBackupSettings } from "@/api/queries";
import { toastApiError } from "@/lib/errors";

export function SettingsPage() {
  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Settings</h1>
        <p className="text-sm text-muted-foreground">
          Configure how the dashboard protects and manages your servers.
        </p>
      </div>

      <BackupScheduleCard />
    </div>
  );
}

function BackupScheduleCard() {
  const settings = useBackupSettings();
  const update = useUpdateBackupSettings();

  const [enabled, setEnabled] = useState(false);
  const [intervalMinutes, setIntervalMinutes] = useState(360);
  const [retentionCount, setRetentionCount] = useState(5);

  // Seed the draft once the settings load (and whenever they change server-side).
  useEffect(() => {
    if (settings.data) {
      setEnabled(settings.data.enabled);
      setIntervalMinutes(settings.data.intervalMinutes);
      setRetentionCount(settings.data.retentionCount);
    }
  }, [settings.data]);

  const save = () => {
    update.mutate(
      { enabled, intervalMinutes, retentionCount },
      {
        onSuccess: () => toast.success("Backup schedule saved."),
        onError: toastApiError,
      },
    );
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Automatic backups</CardTitle>
        <CardDescription>
          Periodically snapshot every server's world data to this machine. Snapshots survive a
          runtime reset. You can always back up a server on demand from its Backups tab.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {settings.isPending ? (
          <Skeleton className="h-32" />
        ) : settings.isError ? (
          <Alert variant="destructive">
            <AlertTitle>Could not load backup settings</AlertTitle>
            <AlertDescription>{settings.error.message}</AlertDescription>
          </Alert>
        ) : (
          <>
            <div className="flex items-center justify-between">
              <Label htmlFor="backup-enabled">Run scheduled backups</Label>
              <Switch id="backup-enabled" checked={enabled} onCheckedChange={setEnabled} />
            </div>

            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label htmlFor="backup-interval">Every (minutes)</Label>
                <Input
                  id="backup-interval"
                  type="number"
                  min={1}
                  disabled={!enabled}
                  value={intervalMinutes}
                  onChange={(e) => setIntervalMinutes(Math.max(1, Number(e.target.value) || 1))}
                />
                <p className="text-xs text-muted-foreground">
                  {intervalMinutes % 60 === 0
                    ? `Every ${intervalMinutes / 60} h`
                    : `Every ${intervalMinutes} min`}
                </p>
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="backup-retention">Keep per server</Label>
                <Input
                  id="backup-retention"
                  type="number"
                  min={0}
                  disabled={!enabled}
                  value={retentionCount}
                  onChange={(e) => setRetentionCount(Math.max(0, Number(e.target.value) || 0))}
                />
                <p className="text-xs text-muted-foreground">
                  {retentionCount === 0
                    ? "Keep every scheduled backup"
                    : `Older ones are pruned after each run`}
                </p>
              </div>
            </div>

            <Button onClick={save} disabled={update.isPending}>
              {update.isPending ? "Saving…" : "Save"}
            </Button>
          </>
        )}
      </CardContent>
    </Card>
  );
}
