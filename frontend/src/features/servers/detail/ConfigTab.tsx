import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useServerConfig, useUpdateConfig } from "@/api/queries";
import { toastApiError } from "@/lib/errors";

export function ConfigTab({ serverName }: { serverName: string }) {
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
    <div className="space-y-4">
      {keys.length === 0 ? (
        <p className="text-sm text-muted-foreground">This server has no editable configuration.</p>
      ) : (
        <div className="rounded-md border">
          <div className="divide-y">
            {keys.map((key) => (
              <div key={key} className="grid grid-cols-[1fr_1.4fr] items-center gap-3 px-3 py-2">
                <span className="truncate font-mono text-xs" title={key}>
                  {key}
                </span>
                <Input
                  className="h-8 font-mono text-xs"
                  value={draft[key] ?? ""}
                  onChange={(e) => setDraft((d) => ({ ...d, [key]: e.target.value }))}
                />
              </div>
            ))}
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <Button onClick={save} disabled={!dirty || update.isPending}>
          {update.isPending ? "Saving…" : "Save changes"}
        </Button>
        {dirty && (
          <Button variant="ghost" onClick={() => setDraft(config.data)} disabled={update.isPending}>
            Discard
          </Button>
        )}
      </div>

      <p className="text-xs text-muted-foreground">
        Sensitive values (Steam tokens, RCON passwords) are not listed here — they live in the
        secrets store, managed on the <Link className="underline" to="/setup">Setup screen</Link>.
      </p>
    </div>
  );
}
