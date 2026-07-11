import { useDeferredValue, useState } from "react";
import { Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardFooter, CardHeader } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useGames } from "@/api/queries";
import { formatBytes } from "@/lib/format";
import { DeployDialog } from "./DeployDialog";
import type { GameTemplate } from "@/api/types";

export function LibraryPage() {
  const [search, setSearch] = useState("");
  const deferredSearch = useDeferredValue(search);
  const games = useGames(deferredSearch.trim());
  const [deploying, setDeploying] = useState<GameTemplate | null>(null);

  return (
    <div className="space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Game Library</h1>
        <p className="text-sm text-muted-foreground">
          Deploy a dedicated server for any of these games with one click.
        </p>
      </div>

      <div className="relative max-w-sm">
        <Search className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
        <Input
          className="pl-8"
          placeholder="Search games…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {games.isPending && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {Array.from({ length: 6 }).map((_, i) => (
            <Skeleton key={i} className="h-40" />
          ))}
        </div>
      )}

      {games.isError && (
        <Alert variant="destructive">
          <AlertTitle>Could not load the game catalog</AlertTitle>
          <AlertDescription>{games.error.message}</AlertDescription>
        </Alert>
      )}

      {games.isSuccess && games.data.length === 0 && (
        <p className="py-16 text-center text-sm text-muted-foreground">
          No games match “{deferredSearch}”.
        </p>
      )}

      {games.isSuccess && games.data.length > 0 && (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
          {games.data.map((game) => (
            <Card key={game.imageTag} className="flex flex-col">
              <CardHeader className="flex-row items-start justify-between space-y-0">
                <div className="min-w-0">
                  <p className="truncate font-semibold" title={game.displayName}>
                    {game.displayName}
                  </p>
                  <p className="truncate text-xs text-muted-foreground" title={game.imageTag}>
                    {game.imageTag}
                  </p>
                </div>
                {game.steamAppId !== null && <Badge variant="secondary">Steam</Badge>}
              </CardHeader>
              <CardContent className="flex-1 text-sm text-muted-foreground">
                <p>
                  {game.defaultResources.memoryLimit} memory · {game.defaultResources.cpuLimit} CPU
                  · {formatBytes(game.defaultStorageBytes)} storage
                </p>
                <p className="mt-1 text-xs">
                  Ports: {game.defaultPorts.map((p) => `${p.containerPort}/${p.protocol}`).join(", ")}
                </p>
              </CardContent>
              <CardFooter>
                <Button className="w-full" onClick={() => setDeploying(game)}>
                  Deploy
                </Button>
              </CardFooter>
            </Card>
          ))}
        </div>
      )}

      <DeployDialog game={deploying} onClose={() => setDeploying(null)} />
    </div>
  );
}
