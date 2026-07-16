import { NavLink, Outlet } from "react-router-dom";
import { Gamepad2, LayoutGrid, Rocket, Settings, Wrench } from "lucide-react";
import { cn } from "@/lib/utils";
import { useHealth } from "@/api/queries";
import { useConnectionStore } from "@/realtime/connectionStore";

const navItems = [
  { to: "/", label: "Servers", icon: LayoutGrid, end: true },
  { to: "/quick-start", label: "Quick Start", icon: Rocket, end: false },
  { to: "/library", label: "Game Library", icon: Gamepad2, end: false },
  { to: "/setup", label: "Setup", icon: Wrench, end: false },
  { to: "/settings", label: "Settings", icon: Settings, end: false },
];

function ConnectionIndicator() {
  const health = useHealth();
  const hubState = useConnectionStore((s) => s.hubState);

  const restOk = health.isSuccess;
  const hubOk = hubState === "connected";

  const dot = (ok: boolean, pending: boolean) =>
    cn("size-2 rounded-full", ok ? "bg-emerald-500" : pending ? "bg-amber-500 animate-pulse" : "bg-destructive");

  return (
    <div className="space-y-1.5 border-t px-4 py-3 text-xs text-muted-foreground">
      <div className="flex items-center gap-2">
        <span className={dot(restOk, health.isPending)} />
        <span>API {restOk ? "connected" : health.isPending ? "connecting…" : "unreachable"}</span>
      </div>
      <div className="flex items-center gap-2">
        <span className={dot(hubOk, hubState === "connecting" || hubState === "reconnecting")} />
        <span>Live updates {hubOk ? "on" : hubState}</span>
      </div>
    </div>
  );
}

export function AppShell() {
  return (
    <div className="flex h-screen overflow-hidden bg-background text-foreground">
      <aside className="flex w-56 shrink-0 flex-col border-r bg-sidebar text-sidebar-foreground">
        <div className="flex items-center gap-2 px-4 py-4">
          <img src="/logo.svg" alt="" className="size-6" />
          <span className="text-sm font-semibold tracking-wide">LANP</span>
        </div>
        <nav className="flex-1 space-y-1 px-2">
          {navItems.map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              className={({ isActive }) =>
                cn(
                  "flex items-center gap-2 rounded-md px-3 py-2 text-sm transition-colors",
                  isActive
                    ? "bg-sidebar-accent text-sidebar-accent-foreground font-medium"
                    : "text-muted-foreground hover:bg-sidebar-accent/50 hover:text-sidebar-accent-foreground",
                )
              }
            >
              <Icon className="size-4" />
              {label}
            </NavLink>
          ))}
        </nav>
        <ConnectionIndicator />
      </aside>
      <main className="flex-1 overflow-y-auto">
        <Outlet />
      </main>
    </div>
  );
}
