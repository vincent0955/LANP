// Pure logic behind MinecraftDeployForm: server-software catalog, the mapping
// from UI selections to itzg/minecraft-server env overrides, and memory-tier →
// pod resources. Kept free of React so it's unit-testable.
// See docs/minecraft-server-types.md ("Mapping to itzg env vars").

import type { ResourceSpec } from "@/api/types";

export type ServerSoftware =
  | "VANILLA"
  | "PAPER"
  | "PURPUR"
  | "FABRIC"
  | "QUILT"
  | "FORGE"
  | "NEOFORGE"
  | "MODPACK";

export const SOFTWARE_GROUPS: {
  label: string;
  options: { value: ServerSoftware; label: string }[];
}[] = [
  { label: "Vanilla", options: [{ value: "VANILLA", label: "Vanilla" }] },
  {
    label: "Plugin servers",
    options: [
      { value: "PAPER", label: "Paper" },
      { value: "PURPUR", label: "Purpur" },
    ],
  },
  {
    label: "Mod loaders",
    options: [
      { value: "FABRIC", label: "Fabric" },
      { value: "QUILT", label: "Quilt" },
      { value: "FORGE", label: "Forge" },
      { value: "NEOFORGE", label: "NeoForge" },
    ],
  },
  { label: "Modpacks", options: [{ value: "MODPACK", label: "Modrinth modpack" }] },
];

const MOD_LOADERS: ServerSoftware[] = ["FABRIC", "QUILT", "FORGE", "NEOFORGE"];
const PLUGIN_SERVERS: ServerSoftware[] = ["PAPER", "PURPUR"];

/** What the "Add content" section searches for; null hides it (vanilla). */
export function contentKindFor(software: ServerSoftware): "mod" | "plugin" | "modpack" | null {
  if (software === "MODPACK") return "modpack";
  if (MOD_LOADERS.includes(software)) return "mod";
  if (PLUGIN_SERVERS.includes(software)) return "plugin";
  return null;
}

/** The `type` param for GET /api/minecraft/versions; null hides the version picker. */
export function versionsTypeFor(software: ServerSoftware): string | null {
  return software === "MODPACK" ? null : software.toLowerCase();
}

/** Modrinth loader facet for mod searches (plugin loaders are fixed server-side). */
export function loaderFacetFor(software: ServerSoftware): string | undefined {
  return MOD_LOADERS.includes(software) ? software.toLowerCase() : undefined;
}

/**
 * Loader filter for the modpack browser — packs bundle their own loader, so
 * this only narrows the search (e.g. Forge packs only); "any" sends no facet.
 */
export const MODPACK_LOADER_OPTIONS = [
  { value: "any", label: "Any loader" },
  { value: "forge", label: "Forge" },
  { value: "neoforge", label: "NeoForge" },
  { value: "fabric", label: "Fabric" },
  { value: "quilt", label: "Quilt" },
] as const;
export type ModpackLoader = (typeof MODPACK_LOADER_OPTIONS)[number]["value"];

/** Display name for a Modrinth loader category (e.g. "neoforge" → "NeoForge"). */
export function loaderLabel(loader: string): string {
  const known = MODPACK_LOADER_OPTIONS.find((o) => o.value === loader);
  return known ? known.label : loader.charAt(0).toUpperCase() + loader.slice(1);
}

export const MEMORY_OPTIONS = ["2G", "4G", "6G", "8G"] as const;
export type MemoryOption = (typeof MEMORY_OPTIONS)[number];

/** Heavier software groups start at a heavier JVM heap (spec open question 2). */
export function defaultMemoryFor(software: ServerSoftware): MemoryOption {
  if (software === "MODPACK") return "6G";
  if (MOD_LOADERS.includes(software)) return "4G";
  return "2G";
}

/**
 * Pod resources per heap tier: memory request = heap, limit = heap + headroom
 * for the JVM's own overhead. The 2G and 6G tiers match the retired vanilla and
 * modded templates' hand-tuned values.
 */
export function resourcesForMemory(memory: MemoryOption): ResourceSpec {
  switch (memory) {
    case "2G":
      return { cpuRequest: "500m", cpuLimit: "2000m", memoryRequest: "2Gi", memoryLimit: "3Gi" };
    case "4G":
      return { cpuRequest: "1000m", cpuLimit: "3000m", memoryRequest: "4Gi", memoryLimit: "5Gi" };
    case "6G":
      return { cpuRequest: "1000m", cpuLimit: "4000m", memoryRequest: "6Gi", memoryLimit: "8Gi" };
    case "8G":
      return { cpuRequest: "2000m", cpuLimit: "4000m", memoryRequest: "8Gi", memoryLimit: "10Gi" };
  }
}

/** Accepts a bare Modrinth slug or any modrinth.com project URL; returns the slug. */
export function parseModrinthSlug(input: string): string {
  const trimmed = input.trim();
  const match = trimmed.match(
    /modrinth\.com\/(?:modpack|mod|plugin|datapack|project)\/([\w!@$()`.+,"-]+)/i,
  );
  return match ? match[1] : trimmed;
}

export interface MinecraftSelection {
  software: ServerSoftware;
  /** "LATEST" or a concrete release like "1.21.7". Ignored for MODPACK. */
  version: string;
  memory: MemoryOption;
  /** Modrinth modpack slug or URL; only used when software is MODPACK. */
  modpack: string;
  /** Modrinth project slugs to install alongside the server. */
  projects: string[];
  /** Values of the advanced config fields (keyed like defaultConfig). */
  advanced: Record<string, string>;
}

/**
 * Builds the ConfigOverrides for a deploy: only values that differ from the
 * template defaults (and from itzg's own defaults — TYPE=VANILLA and
 * VERSION=LATEST are omitted rather than sent). A modpack deploy sends no
 * TYPE/VERSION at all: the pack pins both, and a stray TYPE would fight the
 * pack's loader.
 */
export function composeMinecraftOverrides(
  selection: MinecraftSelection,
  defaults: Record<string, string>,
): Record<string, string> {
  const overrides: Record<string, string> = {};

  if (selection.software === "MODPACK") {
    overrides.MOD_PLATFORM = "MODRINTH";
    overrides.MODRINTH_MODPACK = parseModrinthSlug(selection.modpack);
  } else {
    if (selection.software !== "VANILLA") overrides.TYPE = selection.software;
    if (selection.version !== "LATEST") overrides.VERSION = selection.version;
    if (selection.projects.length > 0) {
      overrides.MODRINTH_PROJECTS = selection.projects.join(",");
      // Pull hard dependencies (e.g. Fabric API) automatically so a missing
      // library can never crash-loop the server.
      overrides.MODRINTH_DOWNLOAD_DEPENDENCIES = "required";
    }
  }

  if (selection.memory !== (defaults.MEMORY ?? "2G")) {
    overrides.MEMORY = selection.memory;
  }

  for (const [key, value] of Object.entries(selection.advanced)) {
    if (value !== defaults[key]) overrides[key] = value;
  }

  return overrides;
}
