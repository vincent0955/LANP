import { create } from "zustand";

export type HubState = "connected" | "connecting" | "reconnecting" | "disconnected";

interface ConnectionState {
  hubState: HubState;
  setHubState: (state: HubState) => void;
}

// Written by the SignalR connection manager (realtime/connection.ts), read by
// the shell's connection indicator.
export const useConnectionStore = create<ConnectionState>((set) => ({
  hubState: "disconnected",
  setHubState: (hubState) => set({ hubState }),
}));
