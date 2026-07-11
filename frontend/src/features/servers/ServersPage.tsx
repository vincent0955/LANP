import { useState } from "react";
import { Link } from "react-router-dom";
import { Gamepad2, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useServers } from "@/api/queries";
import { ServerCard } from "./ServerCard";
import { DeleteServerDialog } from "./DeleteServerDialog";
import { NodeMetricsBar } from "./NodeMetricsBar";

export function ServersPage() {
  const servers = useServers();
  const [deleting, setDeleting] = useState<string | null>(null);

  return (
    <div className="space-y-6 p-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-xl font-semibold">Servers</h1>
          <p className="text-sm text-muted-foreground">Deployed game servers in the cluster.</p>
        </div>
        <Button asChild>
          <Link to="/library">
            <Plus className="size-4" />
            Deploy a game
          </Link>
        </Button>
      </div>

      <NodeMetricsBar />

      {servers.isPending && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => (
            <Skeleton key={i} className="h-44" />
          ))}
        </div>
      )}

      {servers.isError && (
        <Alert variant="destructive">
          <AlertTitle>Could not load servers</AlertTitle>
          <AlertDescription>{servers.error.message}</AlertDescription>
        </Alert>
      )}

      {servers.isSuccess && servers.data.length === 0 && (
        <div className="flex flex-col items-center gap-3 rounded-lg border border-dashed py-16 text-center">
          <Gamepad2 className="size-8 text-muted-foreground" />
          <div>
            <p className="font-medium">No servers deployed yet</p>
            <p className="text-sm text-muted-foreground">
              Pick a game from the library to deploy your first server.
            </p>
          </div>
          <Button asChild variant="outline">
            <Link to="/library">Browse the game library</Link>
          </Button>
        </div>
      )}

      {servers.isSuccess && servers.data.length > 0 && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {servers.data.map((server) => (
            <ServerCard key={server.name} server={server} onDelete={setDeleting} />
          ))}
        </div>
      )}

      <DeleteServerDialog serverName={deleting} onClose={() => setDeleting(null)} />
    </div>
  );
}
