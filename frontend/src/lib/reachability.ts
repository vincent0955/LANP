import type { PortReachability } from "@/api/types";

/**
 * A pass that unlocks "Verified ✓": nothing refused, and at least one port
 * confirmed reachable. UDP can't be probed with a handshake, so an Unverified
 * UDP port that is listening locally doesn't block the pass. Shared by the
 * per-server wizard and the whole-range setup wizard, which both persist the
 * same machine-wide flag.
 */
export function reachabilityPassed(result: {
  cgnatDetected: boolean;
  ports: PortReachability[];
}): boolean {
  if (result.cgnatDetected) return false;
  const ports = result.ports;
  if (ports.length === 0 || ports.some((p) => p.external === "Closed")) return false;
  const blockingUnverified = ports.some(
    (p) =>
      p.external === "Unverified" && !(p.protocol.toUpperCase() === "UDP" && p.locallyListening),
  );
  return !blockingUnverified && ports.some((p) => p.external === "Open");
}
