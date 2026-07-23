import { useEffect, useState, type ReactNode } from "react";
import { useRuntimeStatus } from "@/api/queries";

// Cap the splash so a runtime that never comes up (broken install) still hands
// control back to the app, where the Setup screen explains what to fix. Within
// this window the engine boot is treated as transient and hidden behind the
// splash; past it, whatever warning the app shows is legitimate, not a flash.
const MAX_STARTUP_WAIT_MS = 25_000;

/**
 * Fades out the static boot splash defined in index.html once the runtime is
 * ready. The splash lives outside #root and paints before any JS loads, so
 * React never owns it — we just dismiss the one element it already rendered,
 * which is why the handoff has no flash (nothing is recreated).
 */
function dismissBootSplash() {
  const el = document.getElementById("boot-splash");
  if (!el) return;
  el.classList.add("boot-splash--hidden");
  // Drop it after the fade so it can't intercept clicks on the app beneath.
  window.setTimeout(() => el.remove(), 400);
}

/**
 * Keeps the boot splash up while the bundled runtime is booting on startup.
 * RuntimeAutoStartService brings the engine up in the background, and for those
 * few seconds `engineReachable` is false — long enough for the Servers page to
 * flash a "could not load" warning. The splash overlays the app until the
 * engine is reachable (or the cap elapses), then fades away.
 */
export function StartupGate({ children }: { children: ReactNode }) {
  const runtime = useRuntimeStatus();
  const [expired, setExpired] = useState(false);

  useEffect(() => {
    const timer = window.setTimeout(() => setExpired(true), MAX_STARTUP_WAIT_MS);
    return () => window.clearTimeout(timer);
  }, []);

  const status = runtime.data;
  const booting =
    status !== undefined &&
    status.platform === "windows" &&
    !status.engineReachable &&
    status.wslInstalled &&
    status.distroImported &&
    status.phase !== "Failed" &&
    status.phase !== "AwaitingReboot";

  // Hold the splash while we don't yet know the runtime state, or while it's
  // actively booting — but never past the cap. The app renders underneath the
  // whole time; the opaque overlay simply hides its startup flicker.
  const showSplash = !expired && (runtime.isPending || booting);

  useEffect(() => {
    if (!showSplash) dismissBootSplash();
  }, [showSplash]);

  return <>{children}</>;
}
