import { NavLink, Outlet } from "react-router-dom";
import { useHealth } from "@/api/queries";
import { cn } from "@/lib/utils";

const navItems = [
  { to: "/", label: "Home", end: true },
  { to: "/library", label: "Library", end: false },
  { to: "/setup", label: "Setup", end: false },
  { to: "/settings", label: "Settings", end: false },
];

/** Sidebar per the design handoff: 232px, uppercase nav, health footer. */
export function AppShell() {
  const health = useHealth();
  const healthy = health.data?.clusterReachable === true;

  return (
    <div className="flex h-screen overflow-hidden bg-background text-foreground">
      <aside className="flex w-[232px] shrink-0 flex-col border-r bg-sidebar px-4 py-6 text-sidebar-foreground">
        <div className="flex items-center gap-[11px] px-2.5 pb-6 pt-1">
          <div className="flex size-[34px] shrink-0 items-center justify-center rounded-[9px] bg-primary">
            <svg width="20" height="17" viewBox="10 14 64 55" fill="#10314a" aria-hidden>
              <rect x="10" y="14" width="52" height="13" rx="6.5" opacity="0.4" />
              <rect x="10" y="35" width="52" height="13" rx="6.5" opacity="0.7" />
              <path d="M10 62.5 C10 58.9 12.9 56 16.5 56 L56 56 L74 62.5 L56 69 L16.5 69 C12.9 69 10 66.1 10 62.5 Z" />
            </svg>
          </div>
          <span className="text-[19px] font-extrabold tracking-[-0.02em]">LANP</span>
        </div>
        <nav className="flex flex-col gap-1">
          {navItems.map(({ to, label, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              className={({ isActive }) =>
                cn(
                  "rounded-lg px-3.5 py-[11px] text-[13px] font-semibold uppercase tracking-[0.06em] transition-colors",
                  isActive
                    ? "bg-primary text-primary-foreground"
                    : "text-muted-foreground hover:text-foreground",
                )
              }
            >
              {label}
            </NavLink>
          ))}
        </nav>
        <div className="mt-auto flex items-center gap-[9px] px-3.5 py-2.5 text-[12.5px] text-[#8795a3]">
          <span
            className={cn(
              "inline-block size-2 rounded-full",
              healthy ? "bg-[#2fb877]" : "bg-[#8795a3]",
            )}
          />
          {healthy ? "All systems go" : "Waiting for backend…"}
        </div>
      </aside>
      <main className="flex-1 overflow-y-auto">
        <Outlet />
      </main>
    </div>
  );
}
