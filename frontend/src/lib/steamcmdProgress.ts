// Best-effort parse of steamcmd's download progress from raw log lines, e.g.
//  " Update state (0x61) downloading, progress: 42.42 (1923505432 / 4534391958)"
// Only the downloading phase is reported as a percentage — steamcmd restarts
// its counter for preallocating/verifying, which would make a bar jump around.
// Lines from images that don't pass steamcmd output through simply never match
// (see design.md §4); callers must treat null as "no progress information".
const DOWNLOADING = /update state \(0x\d+\) downloading, progress:\s*(\d+(?:\.\d+)?)/i;

export function parseSteamcmdProgress(line: string): number | null {
  const match = DOWNLOADING.exec(line);
  if (!match) return null;
  const value = Number.parseFloat(match[1]);
  // Some steamcmd versions overshoot 100 briefly; clamp instead of hiding.
  return Number.isFinite(value) ? Math.min(value, 100) : null;
}
