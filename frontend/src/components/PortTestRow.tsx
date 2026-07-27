import type { PortReachability } from "@/api/types";

/**
 * One row of a connection test: a port with its outside-in verdict. Shared by
 * the per-server connection wizard and the Setup screen's whole-range wizard.
 */
export function PortTestRow({
  name,
  protocol,
  port,
  running,
  result,
}: {
  name: string;
  protocol: string;
  port: number;
  running: boolean;
  result: PortReachability | undefined;
}) {
  const view = running
    ? {
        icon: "⋯",
        iconBg: "#e8f5ff",
        iconFg: "#2596d1",
        border: "#c9e8fa",
        bg: "#f5fbff",
        msg: "Checking from the internet…",
        msgColor: "#2596d1",
      }
    : result === undefined
      ? {
          icon: "·",
          iconBg: "#eef3f7",
          iconFg: "#8795a3",
          border: "#e3eaf1",
          bg: "#fbfdfe",
          msg: "Not tested yet",
          msgColor: "#8795a3",
        }
      : result.external === "Open"
        ? {
            icon: "✓",
            iconBg: "#e4f7ee",
            iconFg: "#1d7a51",
            border: "#bfe8d2",
            bg: "#f4fbf7",
            msg: "Reachable from the internet",
            msgColor: "#1d7a51",
          }
        : result.external === "Closed"
          ? {
              icon: "✕",
              iconBg: "#fbe4e2",
              iconFg: "#b0433f",
              border: "#eccfcd",
              bg: "#fdf6f5",
              msg: result.detail ?? "Can't be reached — double-check the port forwarding rule.",
              msgColor: "#b0433f",
            }
          : {
              icon: "?",
              iconBg: "#fff3d6",
              iconFg: "#8a6d1f",
              border: "#f3e2bd",
              bg: "#fff8ec",
              msg:
                result.detail ??
                (protocol.toUpperCase() === "UDP"
                  ? "UDP can't be probed directly — it usually works when the TCP test passes."
                  : "Couldn't verify from the internet."),
              msgColor: "#8a6d1f",
            };

  return (
    <div
      className="flex items-center gap-3.5 rounded-lg border px-[18px] py-3.5"
      style={{ borderColor: view.border, background: view.bg }}
    >
      <span
        className="flex size-6 shrink-0 items-center justify-center rounded-full text-xs"
        style={{ background: view.iconBg, color: view.iconFg }}
      >
        {view.icon}
      </span>
      <div className="min-w-0">
        <div className="flex items-baseline gap-2">
          <span className="text-[14.5px] font-bold">{name}</span>
          <span className="font-mono text-xs text-[#8795a3]">
            {protocol.toUpperCase()} {port}
          </span>
        </div>
        <div className="mt-0.5 text-[13px]" style={{ color: view.msgColor }}>
          {view.msg}
        </div>
      </div>
    </div>
  );
}
