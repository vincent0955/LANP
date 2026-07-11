import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { appendLogLine, useLogStore } from "./logStore";

describe("log buffer", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useLogStore.setState({ buffers: {} });
  });

  afterEach(() => {
    vi.runOnlyPendingTimers();
    vi.useRealTimers();
    useLogStore.setState({ buffers: {} });
  });

  const line = (serverName: string, text: string) => ({
    serverName,
    line: text,
    timestamp: new Date().toISOString(),
  });

  it("batches appends into one store write per flush window", () => {
    appendLogLine(line("a", "one"));
    appendLogLine(line("a", "two"));
    appendLogLine(line("b", "other"));

    // Nothing lands until the flush timer fires.
    expect(useLogStore.getState().buffers["a"]).toBeUndefined();

    vi.advanceTimersByTime(150);

    expect(useLogStore.getState().buffers["a"]?.map((e) => e.line)).toEqual(["one", "two"]);
    expect(useLogStore.getState().buffers["b"]?.map((e) => e.line)).toEqual(["other"]);
  });

  it("caps each server's buffer as a ring (old lines fall off the front)", () => {
    for (let i = 0; i < 5200; i++) {
      appendLogLine(line("a", `line-${i}`));
      if (i % 500 === 0) vi.advanceTimersByTime(150);
    }
    vi.advanceTimersByTime(150);

    const buffer = useLogStore.getState().buffers["a"];
    expect(buffer.length).toBe(5000);
    expect(buffer[0].line).toBe("line-200");
    expect(buffer[buffer.length - 1].line).toBe("line-5199");
  });
});
