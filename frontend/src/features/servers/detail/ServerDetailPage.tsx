import { useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { ArrowLeft, Play, Square, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { StatusBadge } from "@/components/StatusBadge";
import { useScaleServer, useServer } from "@/api/queries";
import { toastApiError } from "@/lib/errors";
import { DeleteServerDialog } from "../DeleteServerDialog";
import { OverviewTab } from "./OverviewTab";
import { ConfigTab } from "./ConfigTab";
import { LogsTab } from "./LogsTab";
import { RconTab } from "./RconTab";

export function ServerDetailPage() {
  const { name = "" } = useParams();
  const navigate = useNavigate();
  const server = useServer(name);
  const scale = useScaleServer(name);
  const [deleting, setDeleting] = useState(false);

  if (server.isPending) {
    return (
      <div className="space-y-4 p-6">
        <Skeleton className="h-10 w-1/3" />
        <Skeleton className="h-64" />
      </div>
    );
  }

  if (server.isError) {
    return (
      <div className="space-y-4 p-6">
        <Button variant="ghost" size="sm" asChild>
          <Link to="/">
            <ArrowLeft className="size-4" />
            Servers
          </Link>
        </Button>
        <Alert variant="destructive">
          <AlertTitle>Could not load server</AlertTitle>
          <AlertDescription>{server.error.message}</AlertDescription>
        </Alert>
      </div>
    );
  }

  const data = server.data;
  const running = data.replicas > 0;

  return (
    <div className="space-y-4 p-6">
      <Button variant="ghost" size="sm" asChild>
        <Link to="/">
          <ArrowLeft className="size-4" />
          Servers
        </Link>
      </Button>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-semibold">{data.name}</h1>
          <StatusBadge status={data.status} />
        </div>
        <div className="flex gap-2">
          <Button
            variant={running ? "outline" : "default"}
            onClick={() => scale.mutate(running ? 0 : 1, { onError: toastApiError })}
            disabled={scale.isPending}
          >
            {running ? <Square className="size-4" /> : <Play className="size-4" />}
            {scale.isPending ? "…" : running ? "Stop" : "Start"}
          </Button>
          <Button
            variant="ghost"
            className="text-destructive hover:text-destructive"
            onClick={() => setDeleting(true)}
          >
            <Trash2 className="size-4" />
            Delete
          </Button>
        </div>
      </div>

      <Tabs defaultValue="overview">
        <TabsList>
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="config">Config</TabsTrigger>
          <TabsTrigger value="logs">Logs</TabsTrigger>
          <TabsTrigger value="rcon">RCON</TabsTrigger>
        </TabsList>
        <TabsContent value="overview" className="mt-4">
          <OverviewTab server={data} />
        </TabsContent>
        <TabsContent value="config" className="mt-4">
          <ConfigTab serverName={data.name} />
        </TabsContent>
        <TabsContent value="logs" className="mt-4">
          <LogsTab serverName={data.name} />
        </TabsContent>
        <TabsContent value="rcon" className="mt-4">
          <RconTab server={data} />
        </TabsContent>
      </Tabs>

      <DeleteServerDialog
        serverName={deleting ? data.name : null}
        onClose={() => setDeleting(false)}
        onDeleted={() => navigate("/")}
      />
    </div>
  );
}
