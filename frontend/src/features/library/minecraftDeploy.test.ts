import { describe, expect, it } from "vitest";
import {
  composeMinecraftOverrides,
  contentKindFor,
  defaultMemoryFor,
  parseModrinthSlug,
  resourcesForMemory,
  type MinecraftSelection,
} from "./minecraftDeploy";

// Mirrors CuratedGameTemplates.Minecraft.DefaultConfig (no TYPE/VERSION keys —
// itzg's own defaults cover those; see docs/minecraft-server-types.md).
const DEFAULTS: Record<string, string> = {
  EULA: "TRUE",
  MEMORY: "2G",
  MAX_PLAYERS: "10",
  MOTD: "Local Minecraft Server",
  DIFFICULTY: "normal",
  MODE: "survival",
  ENABLE_RCON: "true",
  RCON_PORT: "25575",
};

const selection = (partial: Partial<MinecraftSelection>): MinecraftSelection => ({
  software: "VANILLA",
  version: "LATEST",
  memory: "2G",
  modpack: "",
  projects: [],
  advanced: { ...DEFAULTS },
  ...partial,
});

describe("composeMinecraftOverrides", () => {
  it("sends nothing for a default vanilla deploy (itzg defaults cover it)", () => {
    expect(composeMinecraftOverrides(selection({}), DEFAULTS)).toEqual({});
  });

  it("omits TYPE for vanilla but sends a pinned VERSION", () => {
    expect(composeMinecraftOverrides(selection({ version: "1.20.4" }), DEFAULTS)).toEqual({
      VERSION: "1.20.4",
    });
  });

  it("sends TYPE, VERSION, projects, and dependency resolution for a modded server", () => {
    const overrides = composeMinecraftOverrides(
      selection({
        software: "FABRIC",
        version: "1.21.7",
        memory: "4G",
        projects: ["sodium", "lithium"],
      }),
      DEFAULTS,
    );
    expect(overrides).toEqual({
      TYPE: "FABRIC",
      VERSION: "1.21.7",
      MODRINTH_PROJECTS: "sodium,lithium",
      MODRINTH_DOWNLOAD_DEPENDENCIES: "required",
      MEMORY: "4G",
    });
  });

  it("sends only modpack keys for a modpack deploy — never TYPE or VERSION", () => {
    const overrides = composeMinecraftOverrides(
      selection({
        software: "MODPACK",
        version: "1.20.1", // stale UI state must not leak into the deploy
        memory: "6G",
        modpack: "https://modrinth.com/modpack/cobblemon",
        projects: ["sodium"], // individual picks don't apply to modpack deploys
      }),
      DEFAULTS,
    );
    expect(overrides).toEqual({
      MOD_PLATFORM: "MODRINTH",
      MODRINTH_MODPACK: "cobblemon",
      MEMORY: "6G",
    });
  });

  it("includes only advanced values that differ from the template defaults", () => {
    const overrides = composeMinecraftOverrides(
      selection({ advanced: { ...DEFAULTS, DIFFICULTY: "hard", MOTD: "Hi" } }),
      DEFAULTS,
    );
    expect(overrides).toEqual({ DIFFICULTY: "hard", MOTD: "Hi" });
  });
});

describe("parseModrinthSlug", () => {
  it.each([
    ["cobblemon", "cobblemon"],
    ["  fabulously-optimized ", "fabulously-optimized"],
    ["https://modrinth.com/modpack/cobblemon", "cobblemon"],
    ["https://modrinth.com/modpack/adrenaline/versions", "adrenaline"],
    ["https://modrinth.com/mod/sodium", "sodium"],
    ["modrinth.com/plugin/chunky", "chunky"],
  ])("parses %s → %s", (input, expected) => {
    expect(parseModrinthSlug(input)).toBe(expected);
  });
});

describe("memory tiers", () => {
  it("keeps request ≤ limit for every tier", () => {
    for (const memory of ["2G", "4G", "6G", "8G"] as const) {
      const r = resourcesForMemory(memory);
      expect(parseInt(r.memoryRequest)).toBeLessThanOrEqual(parseInt(r.memoryLimit));
      expect(parseInt(r.cpuRequest)).toBeLessThanOrEqual(parseInt(r.cpuLimit));
      // The JVM heap must fit under the container limit with headroom.
      expect(parseInt(r.memoryLimit)).toBeGreaterThan(parseInt(memory));
    }
  });

  it("defaults memory by software group", () => {
    expect(defaultMemoryFor("VANILLA")).toBe("2G");
    expect(defaultMemoryFor("PAPER")).toBe("2G");
    expect(defaultMemoryFor("FORGE")).toBe("4G");
    expect(defaultMemoryFor("MODPACK")).toBe("6G");
  });
});

describe("contentKindFor", () => {
  it("maps software groups to search kinds", () => {
    expect(contentKindFor("VANILLA")).toBeNull();
    expect(contentKindFor("PAPER")).toBe("plugin");
    expect(contentKindFor("PURPUR")).toBe("plugin");
    expect(contentKindFor("FABRIC")).toBe("mod");
    expect(contentKindFor("NEOFORGE")).toBe("mod");
    expect(contentKindFor("MODPACK")).toBe("modpack");
  });
});
