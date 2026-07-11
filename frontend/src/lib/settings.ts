import { create } from "zustand";
import { persist } from "zustand/middleware";

export const DEFAULT_BASE_URL = "http://127.0.0.1:5000";

interface SettingsState {
  /** Backend base URL, no trailing slash. */
  baseUrl: string;
  /**
   * Optional shared-secret sent as X-Api-Token. Only needed when the backend is
   * bound to a non-loopback address; empty string means "don't send the header".
   */
  apiToken: string;
  setBaseUrl: (url: string) => void;
  setApiToken: (token: string) => void;
}

// Persisted via localStorage rather than @tauri-apps/plugin-store: WebView2
// persists localStorage in the app's data directory, and this way the exact same
// code path works in the packaged app and in browser-based Vite dev.
export const useSettings = create<SettingsState>()(
  persist(
    (set) => ({
      baseUrl: DEFAULT_BASE_URL,
      apiToken: "",
      setBaseUrl: (url) => set({ baseUrl: url.replace(/\/+$/, "") || DEFAULT_BASE_URL }),
      setApiToken: (token) => set({ apiToken: token.trim() }),
    }),
    { name: "game-dashboard-settings" },
  ),
);
