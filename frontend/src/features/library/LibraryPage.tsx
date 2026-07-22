import { useDeferredValue, useState } from "react";
import { Play, Search } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { useGames } from "@/api/queries";
import { gameArt, gameInitials } from "@/lib/gameArt";
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
            <div
              key={game.imageTag}
              className="group overflow-hidden rounded-lg border border-[#e3eaf1] bg-card transition-all hover:-translate-y-[3px] hover:border-[#b9dff5] hover:shadow-[0_8px_22px_rgba(16,49,74,0.10)]"
            >
              {/* Gradient box art + initials — no image assets. */}
              <div
                className="flex h-[150px] items-center justify-center"
                style={{ background: gameArt(game.displayName) }}
              >
                <span className="text-5xl font-extrabold tracking-[-0.02em] text-white/90">
                  {gameInitials(game.displayName)}
                </span>
              </div>
              <div className="px-4 pb-[17px] pt-[15px]">
                <div
                  className="flex min-h-[38px] items-center text-[15.5px] font-bold leading-tight"
                  title={game.displayName}
                >
                  {game.displayName}
                </div>
                <button
                  type="button"
                  onClick={() => setDeploying(game)}
                  className="mt-2.5 flex w-full items-center justify-center gap-2 rounded-md bg-primary py-2.5 text-[12.5px] font-bold uppercase tracking-[0.07em] text-primary-foreground transition hover:bg-[#85d7ff]"
                >
                  <Play className="size-3.5" />
                  Play
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      <DeployDialog game={deploying} onClose={() => setDeploying(null)} />
    </div>
  );
}
