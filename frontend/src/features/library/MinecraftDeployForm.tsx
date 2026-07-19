import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { toast } from "sonner";
import { Check, Package, Plus, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  useDeployServer,
  useMinecraftContentSearch,
  useMinecraftVersions,
  useServers,
} from "@/api/queries";
import { isValidServerName, SERVER_NAME_RULES, suggestServerName } from "@/lib/serverName";
import { toastApiError } from "@/lib/errors";
import type { GameTemplate, ModrinthProjectHit } from "@/api/types";
import {
  composeMinecraftOverrides,
  contentKindFor,
  defaultMemoryFor,
  loaderFacetFor,
  loaderLabel,
  MEMORY_OPTIONS,
  MODPACK_LOADER_OPTIONS,
  parseModrinthSlug,
  resourcesForMemory,
  SOFTWARE_GROUPS,
  versionsTypeFor,
  type MemoryOption,
  type ModpackLoader,
  type ServerSoftware,
} from "./minecraftDeploy";

function useDebounced(value: string, ms: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = setTimeout(() => setDebounced(value), ms);
    return () => clearTimeout(handle);
  }, [value, ms]);
  return debounced;
}

const formatDownloads = (n: number) =>
  new Intl.NumberFormat(undefined, { notation: "compact" }).format(n);

/**
 * AMP-style deploy panel for the Minecraft (Java) template: pick server
 * software and version, stack Modrinth mods/plugins or pick a modpack, and
 * deploy — the itzg image installs everything on first boot.
 * See docs/minecraft-server-types.md.
 */
export function MinecraftDeployForm({ game, onClose }: { game: GameTemplate; onClose: () => void }) {
  const navigate = useNavigate();
  const deploy = useDeployServer();

  // The name stays a derived suggestion (unique against existing servers, so a
  // second deploy of the same game gets e.g. "minecraft-java-2") until the user
  // edits the field, at which point their text wins.
  const servers = useServers();
  const [editedName, setEditedName] = useState<string | null>(null);
  const name =
    editedName ?? suggestServerName(game.displayName, (servers.data ?? []).map((s) => s.name));
  const [software, setSoftware] = useState<ServerSoftware>("VANILLA");
  const [version, setVersion] = useState("LATEST");
  const [memory, setMemory] = useState<MemoryOption>(defaultMemoryFor("VANILLA"));
  const [memoryTouched, setMemoryTouched] = useState(false);
  const [projects, setProjects] = useState<ModrinthProjectHit[]>([]);
  const [modpack, setModpack] = useState<ModrinthProjectHit | null>(null);
  const [modpackText, setModpackText] = useState("");
  const [packLoader, setPackLoader] = useState<ModpackLoader>("any");
  const [query, setQuery] = useState("");
  const [advanced, setAdvanced] = useState<Record<string, string>>(() => ({
    ...game.defaultConfig,
  }));
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [touched, setTouched] = useState(false);

  const debouncedQuery = useDebounced(query, 300);

  const versionsType = versionsTypeFor(software);
  const versions = useMinecraftVersions(versionsType);
  // Content searches filter by the concrete version so nothing incompatible is
  // ever offered; "LATEST" resolves through the fetched version list.
  const resolvedVersion = version === "LATEST" ? (versions.data?.latest ?? undefined) : version;

  const contentKind = contentKindFor(software);
  const search = useMinecraftContentSearch({
    q: debouncedQuery,
    kind: contentKind,
    loader:
      contentKind === "modpack"
        ? packLoader === "any"
          ? undefined
          : packLoader
        : loaderFacetFor(software),
    mcVersion: contentKind === "modpack" ? undefined : resolvedVersion,
  });

  // MEMORY is featured; everything else stays in the advanced list.
  const advancedKeys = useMemo(
    () => Object.keys(game.defaultConfig).filter((key) => key !== "MEMORY"),
    [game],
  );
  const secretKeys = useMemo(() => Object.keys(game.secretKeyRefs), [game]);

  const changeSoftware = (next: ServerSoftware) => {
    if (next === software) return;
    // Selections don't carry across content kinds: plugins can't run on a mod
    // loader and vice versa, and a modpack replaces individual picks.
    if (contentKindFor(next) !== contentKind) {
      if (projects.length > 0 || modpack) {
        toast.info("Cleared added content — it doesn't apply to the new server software.");
      }
      setProjects([]);
      setModpack(null);
      setModpackText("");
      setPackLoader("any");
      setQuery("");
    }
    setSoftware(next);
    setVersion("LATEST");
    if (!memoryTouched) setMemory(defaultMemoryFor(next));
  };

  const nameValid = isValidServerName(name);
  const modpackSlug = modpack?.slug ?? parseModrinthSlug(modpackText);
  const modpackMissing = software === "MODPACK" && modpackSlug.trim().length === 0;

  const submit = () => {
    setTouched(true);
    if (!nameValid || modpackMissing) return;

    const overrides = composeMinecraftOverrides(
      {
        software,
        version,
        memory,
        modpack: modpackSlug,
        projects: projects.map((p) => p.slug),
        advanced,
      },
      game.defaultConfig,
    );

    deploy.mutate(
      {
        name,
        imageTag: game.imageTag,
        resources: resourcesForMemory(memory),
        configOverrides: Object.keys(overrides).length > 0 ? overrides : null,
      },
      {
        onSuccess: () => {
          toast.success(`Deploying '${name}'`, {
            description:
              software === "MODPACK"
                ? "First start downloads the whole modpack — this can take ten minutes or more."
                : "First start downloads the server inside the container — this can take a while.",
          });
          onClose();
          navigate("/");
        },
        onError: toastApiError,
      },
    );
  };

  const addProject = (hit: ModrinthProjectHit) => {
    if (!projects.some((p) => p.slug === hit.slug)) setProjects((list) => [...list, hit]);
  };

  const hits = search.data?.hits ?? [];

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="flex max-h-[calc(100dvh-4rem)] max-w-lg flex-col">
        <DialogHeader>
          <DialogTitle>Deploy {game.displayName}</DialogTitle>
          <DialogDescription>
            Pick the server software, add content from Modrinth, and deploy — everything installs
            on first start, no accounts or API keys.
          </DialogDescription>
        </DialogHeader>

        <div className="-mx-4 min-h-0 flex-1 space-y-4 overflow-y-auto px-4">
          <div className="space-y-2">
            <Label htmlFor="server-name">Server name</Label>
            <Input
              id="server-name"
              value={name}
              onChange={(e) => setEditedName(e.target.value)}
              onBlur={() => setTouched(true)}
              aria-invalid={touched && !nameValid}
            />
            {touched && !nameValid && (
              <p className="text-xs text-destructive">{SERVER_NAME_RULES}</p>
            )}
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-2">
              <Label>Server software</Label>
              <Select value={software} onValueChange={(v) => changeSoftware(v as ServerSoftware)}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {SOFTWARE_GROUPS.map((group) => (
                    <SelectGroup key={group.label}>
                      <SelectLabel>{group.label}</SelectLabel>
                      {group.options.map((opt) => (
                        <SelectItem key={opt.value} value={opt.value}>
                          {opt.label}
                        </SelectItem>
                      ))}
                    </SelectGroup>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <div className="space-y-2">
              <Label>{software === "MODPACK" ? "Memory" : "Version"}</Label>
              {software === "MODPACK" ? (
                <MemorySelect value={memory} onChange={(m) => { setMemory(m); setMemoryTouched(true); }} />
              ) : versions.isError ? (
                // Metadata outage: deploys must never be blocked — free text.
                <Input
                  value={version}
                  onChange={(e) => setVersion(e.target.value || "LATEST")}
                  placeholder="LATEST"
                  title="Version list unavailable — type a version like 1.21.7, or LATEST"
                />
              ) : (
                <Select value={version} onValueChange={setVersion}>
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent className="max-h-64">
                    <SelectItem value="LATEST">
                      Latest release{versions.data?.latest ? ` (${versions.data.latest})` : ""}
                    </SelectItem>
                    {(versions.data?.versions ?? [])
                      .filter((v) => v !== "LATEST")
                      .map((v) => (
                        <SelectItem key={v} value={v}>
                          {v}
                        </SelectItem>
                      ))}
                  </SelectContent>
                </Select>
              )}
            </div>
          </div>

          {software !== "MODPACK" && (
            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-2">
                <Label>Memory</Label>
                <MemorySelect value={memory} onChange={(m) => { setMemory(m); setMemoryTouched(true); }} />
              </div>
            </div>
          )}

          {contentKind !== null && (
            <div className="space-y-2">
              <div className="flex items-baseline justify-between">
                <Label>{contentKind === "modpack" ? "Modpack" : "Add content"}</Label>
                {contentKind !== "modpack" && projects.length > 0 && (
                  <span className="text-xs text-muted-foreground">{projects.length} added</span>
                )}
              </div>

              {contentKind === "modpack" && (
                <Input
                  value={modpack ? "" : modpackText}
                  onChange={(e) => {
                    setModpackText(e.target.value);
                    setModpack(null);
                  }}
                  placeholder={modpack ? "" : "Search below, or paste a modrinth.com link / slug"}
                  aria-invalid={touched && modpackMissing}
                  disabled={modpack !== null}
                />
              )}

              {modpack && (
                <div className="flex items-center gap-2 rounded-md border bg-muted/40 px-2 py-1.5">
                  {modpack.iconUrl ? (
                    <img src={modpack.iconUrl} alt="" className="size-5 rounded" />
                  ) : (
                    <Package className="size-4 text-muted-foreground" />
                  )}
                  <span className="flex-1 truncate text-sm font-medium">{modpack.title}</span>
                  {(modpack.loaders ?? []).length > 0 && (
                    <span className="shrink-0 rounded border px-1 py-0.5 text-[10px] text-muted-foreground">
                      {modpack.loaders.map(loaderLabel).join(" · ")}
                    </span>
                  )}
                  <button
                    type="button"
                    title="Remove modpack"
                    onClick={() => setModpack(null)}
                    className="rounded p-0.5 hover:bg-accent"
                  >
                    <X className="size-3.5" />
                  </button>
                </div>
              )}

              {contentKind !== "modpack" && projects.length > 0 && (
                <div className="flex flex-wrap gap-1.5">
                  {projects.map((p) => (
                    <span
                      key={p.slug}
                      className="inline-flex items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs"
                    >
                      {p.title}
                      <button
                        type="button"
                        title={`Remove ${p.title}`}
                        onClick={() => setProjects((list) => list.filter((x) => x.slug !== p.slug))}
                        className="rounded p-0.5 hover:bg-accent"
                      >
                        <X className="size-3" />
                      </button>
                    </span>
                  ))}
                </div>
              )}

              <div className="flex gap-2">
                <Input
                  className="flex-1"
                  value={query}
                  onChange={(e) => setQuery(e.target.value)}
                  placeholder="Search content…"
                />
                {contentKind === "modpack" && (
                  <Select
                    value={packLoader}
                    onValueChange={(v) => setPackLoader(v as ModpackLoader)}
                  >
                    <SelectTrigger className="w-32 shrink-0" title="Filter modpacks by mod loader">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {MODPACK_LOADER_OPTIONS.map((opt) => (
                        <SelectItem key={opt.value} value={opt.value}>
                          {opt.label}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                )}
              </div>

              {search.isError ? (
                <p className="text-xs text-muted-foreground">
                  Modrinth is unreachable right now
                  {contentKind === "modpack" ? " — you can still paste a link or slug above." : "."}
                </p>
              ) : (
                <div className="max-h-44 space-y-0.5 overflow-y-auto rounded-md border p-1">
                  {hits.length === 0 && (
                    <p className="p-2 text-xs text-muted-foreground">
                      {search.isFetching ? "Searching…" : "No results."}
                    </p>
                  )}
                  {hits.map((hit) => {
                    const added =
                      contentKind === "modpack"
                        ? modpack?.slug === hit.slug
                        : projects.some((p) => p.slug === hit.slug);
                    return (
                      <button
                        key={hit.slug}
                        type="button"
                        onClick={() =>
                          contentKind === "modpack"
                            ? (setModpack(hit), setModpackText(""))
                            : addProject(hit)
                        }
                        disabled={added}
                        className="flex w-full items-center gap-2 rounded px-1.5 py-1 text-left hover:bg-accent disabled:opacity-60"
                      >
                        {hit.iconUrl ? (
                          <img src={hit.iconUrl} alt="" className="size-6 shrink-0 rounded" />
                        ) : (
                          <Package className="size-5 shrink-0 text-muted-foreground" />
                        )}
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-sm">{hit.title}</span>
                          <span className="block truncate text-xs text-muted-foreground">
                            {hit.description}
                          </span>
                        </span>
                        {contentKind === "modpack" && (hit.loaders ?? []).length > 0 && (
                          <span className="shrink-0 rounded border px-1 py-0.5 text-[10px] text-muted-foreground">
                            {hit.loaders.map(loaderLabel).join(" · ")}
                          </span>
                        )}
                        <span className="shrink-0 text-xs text-muted-foreground">
                          {formatDownloads(hit.downloads)} ↓
                        </span>
                        {added ? (
                          <Check className="size-4 shrink-0 text-muted-foreground" />
                        ) : (
                          <Plus className="size-4 shrink-0 text-muted-foreground" />
                        )}
                      </button>
                    );
                  })}
                </div>
              )}
              {contentKind !== "modpack" && (
                <p className="text-xs text-muted-foreground">
                  Required dependencies are downloaded automatically.
                </p>
              )}
              {touched && modpackMissing && (
                <p className="text-xs text-destructive">Pick a modpack or paste its link first.</p>
              )}
            </div>
          )}

          <div className="space-y-2">
            <button
              type="button"
              className="text-xs text-muted-foreground underline-offset-2 hover:underline"
              onClick={() => setShowAdvanced((s) => !s)}
            >
              {showAdvanced ? "Hide advanced configuration" : "Show advanced configuration"}
            </button>
            {showAdvanced && (
              <>
                <div className="max-h-48 space-y-2 overflow-y-auto rounded-md border p-3">
                  {advancedKeys.map((key) => (
                    <div key={key} className="grid grid-cols-[1fr_1.2fr] items-center gap-2">
                      <span className="truncate font-mono text-xs" title={key}>
                        {key}
                      </span>
                      <Input
                        className="h-8 font-mono text-xs"
                        value={advanced[key] ?? ""}
                        onChange={(e) => setAdvanced((c) => ({ ...c, [key]: e.target.value }))}
                      />
                    </div>
                  ))}
                </div>
                {secretKeys.length > 0 && (
                  <p className="text-xs text-muted-foreground">
                    {secretKeys.join(", ")} are secrets — set them on the server's Secrets tab after
                    it's created. The server won't start until they're set.
                  </p>
                )}
              </>
            )}
          </div>
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={onClose} disabled={deploy.isPending}>
            Cancel
          </Button>
          <Button
            onClick={submit}
            disabled={deploy.isPending || (touched && (!nameValid || modpackMissing))}
          >
            {deploy.isPending ? "Deploying…" : "Deploy"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function MemorySelect({
  value,
  onChange,
}: {
  value: MemoryOption;
  onChange: (m: MemoryOption) => void;
}) {
  return (
    <Select value={value} onValueChange={(v) => onChange(v as MemoryOption)}>
      <SelectTrigger>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        {MEMORY_OPTIONS.map((m) => (
          <SelectItem key={m} value={m}>
            {m} RAM
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
