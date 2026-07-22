import { create } from "zustand";
import { persist } from "zustand/middleware";

// "Internet play verified" is a property of this machine's router/firewall setup:
// the forwarded port range covers every server, so the flag is persisted per
// machine, not per server (design handoff → Interactions & Behavior).
interface ConnectivityState {
  verified: boolean;
  setVerified: (verified: boolean) => void;
}

export const useConnectivity = create<ConnectivityState>()(
  persist((set) => ({ verified: false, setVerified: (verified) => set({ verified }) }), {
    name: "lanp-connectivity",
  }),
);
