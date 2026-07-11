import { describe, expect, it } from "vitest";
import { isValidServerName, suggestServerName } from "./serverName";

describe("isValidServerName", () => {
  it.each(["cs2", "my-server-1", "a", "0abc", "x".repeat(63)])("accepts %s", (name) => {
    expect(isValidServerName(name)).toBe(true);
  });

  it.each([
    "",
    "My-Server", // uppercase
    "-leading",
    "trailing-",
    "under_score",
    "dot.name",
    "spaced name",
    "x".repeat(64), // too long
  ])("rejects %j", (name) => {
    expect(isValidServerName(name)).toBe(false);
  });
});

describe("suggestServerName", () => {
  it("kebab-cases display names", () => {
    expect(suggestServerName("Counter-Strike 2")).toBe("counter-strike-2");
    expect(suggestServerName("ARK: Survival Evolved")).toBe("ark-survival-evolved");
    expect(suggestServerName("Minecraft (Java Edition)")).toBe("minecraft-java-edition");
  });

  it("always yields a valid name", () => {
    for (const input of ["7 Days to Die", "!!!", "Garry's Mod", "V Rising"]) {
      expect(isValidServerName(suggestServerName(input))).toBe(true);
    }
  });
});
