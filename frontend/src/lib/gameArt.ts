// Gradient "box art" tiles from the design handoff: no image assets — each game
// gets a deterministic gradient plus its initials rendered on top.

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
