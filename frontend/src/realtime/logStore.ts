import { create } from "zustand";
import type { LogLineMessage } from "@/api/types";

export interface LogEntry {
  line: string;
  timestamp: string;
}

/** Cap per server: old lines fall off the front (backend replays 200 on subscribe). */
const MAX_LINES = 5000;
/** Batch window: steamcmd can spam hundreds of lines/sec; one store write per flush. */
const FLUSH_MS = 100;

interface LogState {
  buffers: Record<string, LogEntry[]>;
  clear: (serverName: string) => void;
}

export const useLogStore = create<LogState>((set) => ({
  buffers: {},
  clear: (serverName) =>
    set((state) => ({ buffers: { ...state.buffers, [serverName]: [] } })),
}));

// --- Append batching (module-level, outside React) ---

const pending = new Map<string, LogEntry[]>();
let flushTimer: ReturnType<typeof setTimeout> | null = null;

export function appendLogLine(message: LogLineMessage) {
  const list = pending.get(message.serverName) ?? [];
  list.push({ line: message.line, timestamp: message.timestamp });
  pending.set(message.serverName, list);
  flushTimer ??= setTimeout(flush, FLUSH_MS);
}

function flush() {
  flushTimer = null;
  if (pending.size === 0) return;
  useLogStore.setState((state) => {
    const buffers = { ...state.buffers };
    for (const [server, lines] of pending) {
      const merged = [...(buffers[server] ?? []), ...lines];
      buffers[server] = merged.length > MAX_LINES ? merged.slice(-MAX_LINES) : merged;
    }
    return { buffers };
  });
  pending.clear();
}

// --- Subscription ref-counting ---
// The backend stops the underlying kubectl log stream when its last subscriber
// leaves, so Subscribe/Unsubscribe must be balanced. Ref-counting lets two
// viewers of the same server share one hub subscription.

const refCounts = new Map<string, number>();

type HubInvoker = (method: "SubscribeLogs" | "UnsubscribeLogs", serverName: string) => Promise<void>;
let invoker: HubInvoker | null = null;

/** Wired once by realtime/connection.ts. */
export function setLogHubInvoker(fn: HubInvoker) {
  invoker = fn;
}

export function subscribeLogs(serverName: string) {
  const count = (refCounts.get(serverName) ?? 0) + 1;
  refCounts.set(serverName, count);
  if (count === 1) {
    // Fresh subscription: drop any stale buffer so the backend's 200-line replay
    // doesn't duplicate lines from a previous viewing session.
    useLogStore.getState().clear(serverName);
    void invoker?.("SubscribeLogs", serverName).catch(() => {});
  }
}

export function unsubscribeLogs(serverName: string) {
  const count = (refCounts.get(serverName) ?? 0) - 1;
  if (count <= 0) {
    refCounts.delete(serverName);
    void invoker?.("UnsubscribeLogs", serverName).catch(() => {});
  } else {
    refCounts.set(serverName, count);
  }
}

/** Servers with live viewers — used to resubscribe after a reconnect. */
export function activeLogSubscriptions(): string[] {
  return [...refCounts.keys()];
}

/** On reconnect the backend replays history; clear buffers to avoid duplicates. */
export function resetBuffersForResubscribe() {
  const { clear } = useLogStore.getState();
  for (const server of refCounts.keys()) clear(server);
}
