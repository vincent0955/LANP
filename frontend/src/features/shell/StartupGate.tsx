import { useEffect, useState, type ReactNode } from "react";
import { Loader2 } from "lucide-react";
import { useRuntimeStatus } from "@/api/queries";

// Cap the splash so a runtime that never comes up (broken install) still hands
// control back to the app, where the Setup screen explains what to fix. Within
// this window the engine boot is treated as transient and hidden behind the
// splash; past it, whatever warning the app shows is legitimate, not a flash.
const MAX_STARTUP_WAIT_MS = 25_000;

/**
 * Minimalistic splash shown only while the bundled runtime is booting on
 * startup. RuntimeAutoStartService brings the engine up in the background, and
 * for those few seconds `engineReachable` is false — long enough for the
 * Servers page to flash a "could not load" warning. This gate covers exactly
 * that window and then reveals the app.
 */
export function StartupGate({ children }: { children: ReactNode }) {
  const runtime = useRuntimeStatus();
  const [expired, setExpired] = useState(false);

  useEffect(() => {
    const timer = setTimeout(() => setExpired(true), MAX_STARTUP_WAIT_MS);
    return () => clearTimeout(timer);
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

  // Show the splash while we don't yet know the runtime state, or while it's
  // actively booting — but never past the cap.
  if (!expired && (runtime.isPending || booting)) {
    return <StartupScreen />;
  }

  return <>{children}</>;
}

function StartupScreen() {
  return (
    <div className="flex h-screen flex-col items-center justify-center gap-6 bg-background text-foreground">
      <div className="flex size-14 items-center justify-center rounded-2xl bg-primary">
        <svg width="30" height="26" viewBox="10 14 64 55" fill="#10314a" aria-hidden>
          <rect x="10" y="14" width="52" height="13" rx="6.5" opacity="0.4" />
          <rect x="10" y="35" width="52" height="13" rx="6.5" opacity="0.7" />
          <path d="M10 62.5 C10 58.9 12.9 56 16.5 56 L56 56 L74 62.5 L56 69 L16.5 69 C12.9 69 10 66.1 10 62.5 Z" />
        </svg>
      </div>
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Loader2 className="size-4 animate-spin" />
        <span>Starting the container runtime…</span>
      </div>
    </div>
  );
}
