import { describe, expect, it } from "vitest";
import { parseSteamcmdProgress } from "./steamcmdProgress";

describe("parseSteamcmdProgress", () => {
  it("parses the downloading phase", () => {
    expect(
      parseSteamcmdProgress(
        " Update state (0x61) downloading, progress: 42.42 (1923505432 / 4534391958)",
      ),
    ).toBe(42.42);
  });

  it("ignores other update phases (their counters restart)", () => {
    expect(
      parseSteamcmdProgress(" Update state (0x11) preallocating, progress: 25.88 (…)"),
    ).toBeNull();
    expect(
      parseSteamcmdProgress(" Update state (0x81) verifying update, progress: 71.85 (…)"),
    ).toBeNull();
  });

  it("ignores unrelated lines that mention progress", () => {
    expect(parseSteamcmdProgress("[Worldgen] progress: 55")).toBeNull();
    expect(parseSteamcmdProgress("Starting server…")).toBeNull();
  });

  it("clamps steamcmd's occasional >100% readings", () => {
    expect(
      parseSteamcmdProgress(" Update state (0x61) downloading, progress: 104.36 (…)"),
    ).toBe(100);
  });
});
