import { invoke } from "@tauri-apps/api/core";

/**
 * Relaunches the desktop app (used after the container-engine setting changes,
 * which the backend only applies at boot). The Rust side stops the running
 * servers and the old engine first, then restarts the process.
 *
 * Returns false when there's nothing to restart — the UI running in a plain
 * browser during development — so callers can fall back to telling the user to
 * restart it themselves rather than silently doing nothing.
 */
export async function restartApp(): Promise<boolean> {
  // Present in every Tauri v2 webview and nowhere else; `invoke` would throw
  // rather than no-op in a plain browser.
  if (!("__TAURI_INTERNALS__" in window)) {
    return false;
  }

  try {
    await invoke("restart_for_runtime_change");
    // Not normally reached: the process is replaced mid-call, so this promise
    // simply never settles on success.
    return true;
  } catch (error) {
    // The setting is already saved server-side, so a failed relaunch is
    // recoverable — the caller falls back to asking the user to restart.
    console.error("Automatic restart failed:", error);
    return false;
  }
}
