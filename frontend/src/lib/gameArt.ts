// Real cover art + logos for the catalog, with a gradient "box art" fallback from
// the design handoff (deterministic gradient + initials) for anything unmapped or
// when an image fails to load. All image sources are zero-setup (no API keys):
// Steam's public CDN for games on Steam, and the curated dashboard-icons logo set
// for the few that aren't. See gameImages() below.

/** Cover art (banner) + square art (icon) for one game. */
export interface GameImages {
  /** Wide cover art for banner slots (rendered object-cover). Null → use icon/gradient. */
  banner: string | null;
  /** Square-ish art for the icon slot (and as a banner badge when banner is null). */
  icon: string | null;
  /** True when `icon` is a transparent logo (render contained on a gradient) rather
   *  than photographic box art (render cover-cropped). */
  logo: boolean;
}

const STEAM_CDN = "https://cdn.cloudflare.steamstatic.com/steam/apps";
const ICON_CDN = "https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/webp";

// Steam art keyed by the STORE appid — note this differs from the dedicated-server
// SteamAppId a template deploys with (e.g. CS2 store 730 vs. no server appid, TF2
// store 440 vs. server 232250). header.jpg is the wide banner; library_600x900.jpg
// is the portrait box art we crop square for the icon.
const steam = (storeAppId: number): GameImages => ({
  banner: `${STEAM_CDN}/${storeAppId}/header.jpg`,
  icon: `${STEAM_CDN}/${storeAppId}/library_600x900.jpg`,
  logo: false,
});

// Logo-only art for non-Steam games: no wide banner, so the icon logo doubles as a
// centered badge on the gradient in banner slots.
const logo = (name: string): GameImages => ({
  banner: null,
  icon: `${ICON_CDN}/${name}.webp`,
  logo: true,
});

// Keyed by the catalog image tag (see backend CuratedGameTemplates). Games absent
// here fall back to the gradient + initials tile.
const GAME_IMAGES: Record<string, GameImages> = {
  "joedwards32/cs2:latest": steam(730),
  "gameservermanagers/gameserver:ins": steam(222880),
  "gameservermanagers/gameserver:tf2": steam(440),
  "gameservermanagers/gameserver:rust": steam(252490),
  "gameservermanagers/gameserver:vh": steam(892970),
  "gameservermanagers/gameserver:pz": steam(108600),
  "gameservermanagers/gameserver:ark": steam(346110),
  "passivelemon/terraria-docker:latest": steam(105600),
  "gameservermanagers/gameserver:sdtd": steam(251570),
  "gameservermanagers/gameserver:l4d2": steam(550),
  "gameservermanagers/gameserver:gmod": steam(4000),
  "gameservermanagers/gameserver:pw": steam(1623730),
  "trueosiris/vrising:latest": steam(1604030),
  "gameservermanagers/gameserver:sf": steam(526870),
  "jammsen/sons-of-the-forest-dedicated-server:latest": steam(1326470),
  "factoriotools/factorio:stable": steam(427520),
  "ghcr.io/balnaimi/conan-exiles-server:latest": steam(440900),
  // Not on Steam — curated logos.
  "itzg/minecraft-server:latest": logo("minecraft"),
  "itzg/minecraft-bedrock-server:latest": logo("minecraft"),
  "indifferentbroccoli/hytale-server-docker:latest": logo("hytale"),
};

const NO_IMAGES: GameImages = { banner: null, icon: null, logo: false };

/**
 * Art for a game, resolved from its container image tag. Handles Minecraft's
 * per-Java-version variant tags (e.g. `itzg/minecraft-server:java21`), which a
 * deployed server runs instead of the catalog `:latest` tag.
 */
export function gameImages(imageTag: string): GameImages {
  const exact = GAME_IMAGES[imageTag];
  if (exact) return exact;
  if (imageTag.startsWith("itzg/minecraft-bedrock-server"))
    return GAME_IMAGES["itzg/minecraft-bedrock-server:latest"];
  if (imageTag.startsWith("itzg/minecraft-server"))
    return GAME_IMAGES["itzg/minecraft-server:latest"];
  return NO_IMAGES;
}

const ARTS = [
  "linear-gradient(135deg, #2b6cb0, #4fd1c5)",
  "linear-gradient(135deg, #6b46c1, #d53f8c)",
  "linear-gradient(135deg, #c05621, #ecc94b)",
  "linear-gradient(135deg, #276749, #68d391)",
  "linear-gradient(135deg, #2c5282, #90cdf4)",
  "linear-gradient(135deg, #97266d, #f687b3)",
  "linear-gradient(135deg, #4a5568, #a0aec0)",
  "linear-gradient(135deg, #285e61, #4fd1c5)",
];

/** "Minecraft (Java)" → "MC", "7 Days to Die" → "7D". */
export function gameInitials(name: string): string {
  const words = name
    .replace(/[^A-Za-z0-9 ]/g, "")
    .split(" ")
    .filter(Boolean);
  return (
    words
      .slice(0, 2)
      .map((word) => word[0])
      .join("")
      .toUpperCase() || "?"
  );
}

// Games the handoff shows with a specific gradient; everything else hashes.
const NAMED_ART: [keyword: string, index: number][] = [
  ["minecraft", 0],
  ["counter-strike", 3],
  ["terraria", 2],
];

/** Stable gradient per game name, so a game keeps its art across screens. */
export function gameArt(name: string): string {
  const lower = name.toLowerCase();
  const named = NAMED_ART.find(([keyword]) => lower.includes(keyword));
  if (named) return ARTS[named[1]];
  let hash = 0;
  for (let i = 0; i < name.length; i++) hash = (hash * 31 + name.charCodeAt(i)) >>> 0;
  return ARTS[hash % ARTS.length];
}

/** Rough uptime for a card sub-line, e.g. "up 3 hours". Null if not sensible. */
export function formatUptime(iso: string): string | null {
  const start = new Date(iso).getTime();
  if (!Number.isFinite(start)) return null;
  const secs = Math.floor((Date.now() - start) / 1000);
  if (secs < 0) return null;
  const units: [size: number, label: string][] = [
    [86400, "day"],
    [3600, "hour"],
    [60, "minute"],
  ];
  for (const [size, label] of units) {
    if (secs >= size) {
      const n = Math.floor(secs / size);
      return `up ${n} ${label}${n === 1 ? "" : "s"}`;
    }
  }
  return "up just now";
}
