import { create } from "zustand";

export interface ActivityEntry {
  timestamp: string;
  message: string;
}

/** Session-scoped feed; the "Server created" row comes from REST data instead. */
const MAX_ENTRIES = 30;

interface ActivityState {
  byServer: Record<string, ActivityEntry[]>;
}

export const useActivityStore = create<ActivityState>(() => ({ byServer: {} }));

/** Called from realtime/connection.ts hub handlers; newest entry first. */
export function recordActivity(serverName: string, message: string, timestamp: string) {
  useActivityStore.setState((state) => {
    const current = state.byServer[serverName] ?? [];
    // Status events can repeat (e.g. periodic reconciliation); don't stutter.
    if (current[0]?.message === message) return state;
    const list = [{ timestamp, message }, ...current].slice(0, MAX_ENTRIES);
    return { byServer: { ...state.byServer, [serverName]: list } };
  });
}
