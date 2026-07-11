import { create } from "zustand";
import type { DownloadProgressMessage } from "@/api/types";

interface ProgressState {
  /** Latest DownloadProgress per server; written by realtime/connection.ts. */
  byServer: Record<string, DownloadProgressMessage>;
  record: (message: DownloadProgressMessage) => void;
}

export const useProgressStore = create<ProgressState>((set) => ({
  byServer: {},
  record: (message) =>
    set((state) => ({ byServer: { ...state.byServer, [message.serverName]: message } })),
}));
