import { useEffect, useState } from "react";
import { Wifi } from "lucide-react";
import { useForwardingGuide, useNetworkInfo, useReachabilityTest } from "@/api/queries";
import { CopyButton } from "@/components/CopyButton";
import { useConnectivity } from "@/lib/connectivity";
import { cn } from "@/lib/utils";
import { primaryBtn } from "./ui";
import type { PortReachability, ServerDetail, ServerReachability } from "@/api/types";

type Step = "choice" | "local" | "firewall" | "portforward" | "test";

const PUBLIC_STEPS: Step[] = ["firewall", "portforward", "test"];

interface Props {
  server: ServerDetail;
  open: boolean;
  onClose: () => void;
}

/**
 * Guided connection-setup tutorial (design handoff → Tutorial wizard): local
 * path is a dead end by design ("you're good to go"), the public path walks
 * firewall → router port-forward → an outside-in reachability test. A full pass
 * marks this machine "Verified" (persisted; the port range covers all servers).
 */
export function ConnectionWizard({ server, open, onClose }: Props) {
  const [step, setStep] = useState<Step>("choice");
  const networkInfo = useNetworkInfo().data;
  const guide = useForwardingGuide(server.name).data;
  const test = useReachabilityTest(server.name);
  const setVerified = useConnectivity((s) => s.setVerified);

  // Every open starts the tutorial from its first screen with a fresh test.
  const { reset } = test;
  useEffect(() => {
    if (open) {
      setStep("choice");
      reset();
    }
  }, [open, reset]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  const joinPort = server.ports.find((p) => !p.name.toLowerCase().includes("rcon"));
  const lanAddress = networkInfo?.lanAddresses[0] ?? "localhost";
  const localJoin = joinPort ? `${lanAddress}:${joinPort.nodePort}` : lanAddress;
  const publicJoin =
    joinPort && networkInfo?.publicAddress
      ? `${networkInfo.publicAddress}:${joinPort.nodePort}`
      : null;
  const portRange = networkInfo
    ? `${networkInfo.nodePortRangeStart} – ${networkInfo.nodePortRangeEnd}`
    : "…";
  const portRangeCopy = networkInfo
    ? `${networkInfo.nodePortRangeStart}-${networkInfo.nodePortRangeEnd}`
    : "";
  const firewallCommands =
    guide?.firewallCommands && guide.firewallCommands.length > 0
      ? guide.firewallCommands
      : networkInfo
        ? [
            `netsh advfirewall firewall add rule name="LANP" dir=in action=allow protocol=TCP localport=${networkInfo.nodePortRangeStart}-${networkInfo.nodePortRangeEnd}`,
            `netsh advfirewall firewall add rule name="LANP UDP" dir=in action=allow protocol=UDP localport=${networkInfo.nodePortRangeStart}-${networkInfo.nodePortRangeEnd}`,
          ]
        : [];

  const stepIdx = PUBLIC_STEPS.indexOf(step);
  const result = test.data;
  const passed = result !== undefined && reachabilityPassed(result);

  const goBack = () => {
    if (step === "local" || step === "firewall") setStep("choice");
    else if (step === "portforward") setStep("firewall");
    else if (step === "test") setStep("portforward");
  };

  const runTest = () => {
    if (test.isPending) return;
    test.mutate(undefined, {
      onSuccess: (r) => {
        if (reachabilityPassed(r)) setVerified(true);
      },
    });
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-[rgba(16,25,35,0.45)] p-6"
      onClick={onClose}
    >
      <div
        className="w-[640px] max-w-full overflow-hidden rounded-xl bg-white shadow-[0_24px_64px_rgba(16,49,74,0.25)]"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Header */}
        <div className="flex items-center gap-3 border-b border-[#eef3f7] px-[26px] py-5">
          {step !== "choice" && (
            <button
              type="button"
              onClick={goBack}
              className="cursor-pointer text-[13px] font-semibold text-[#8795a3] transition-colors hover:text-foreground"
            >
              ← Back
            </button>
          )}
          <span className="text-[15px] font-bold">Connection setup</span>
          {stepIdx >= 0 && (
            <div className="ml-2 flex items-center gap-1.5">
              {PUBLIC_STEPS.map((s, i) => (
                <span
                  key={s}
                  className={cn(
                    "h-1.5 rounded-[3px]",
                    i <= stepIdx ? "bg-primary" : "bg-[#e3eaf1]",
                    i === stepIdx ? "w-[22px]" : "w-2.5",
                  )}
                />
              ))}
            </div>
          )}
          <button
            type="button"
            onClick={onClose}
            className="ml-auto flex size-[30px] cursor-pointer items-center justify-center rounded-[6px] text-[15px] text-[#8795a3] transition-colors hover:bg-secondary hover:text-foreground"
          >
            ✕
          </button>
        </div>

        {/* Choice */}
        {step === "choice" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[22px] font-extrabold tracking-[-0.01em]">
              Who's joining your server?
            </div>
            <div className="mt-1.5 text-[14.5px] text-muted-foreground">
              This decides whether there's any setup at all.
            </div>
            <div className="mt-[22px] grid grid-cols-2 gap-3.5">
              <button
                type="button"
                onClick={() => setStep("local")}
                className="cursor-pointer rounded-[10px] border-[1.5px] border-[#e3eaf1] p-[22px] text-left transition-colors hover:border-primary hover:bg-accent"
              >
                <div className="text-[26px]">🏠</div>
                <div className="mt-2.5 text-base font-bold">Same house or network</div>
                <div className="mt-[5px] text-[13px] leading-normal text-muted-foreground">
                  Roommates, family, LAN party. Everyone is on your Wi‑Fi.
                </div>
              </button>
              <button
                type="button"
                onClick={() => setStep("firewall")}
                className="cursor-pointer rounded-[10px] border-[1.5px] border-[#e3eaf1] p-[22px] text-left transition-colors hover:border-primary hover:bg-accent"
              >
                <div className="text-[26px]">🌍</div>
                <div className="mt-2.5 text-base font-bold">Friends over the internet</div>
                <div className="mt-[5px] text-[13px] leading-normal text-muted-foreground">
                  Anyone, anywhere. Needs a one‑time router setup (~5 min).
                </div>
              </button>
            </div>
          </div>
        )}

        {/* Local — all good */}
        {step === "local" && (
          <div className="px-[26px] pb-[34px] pt-10 text-center">
            <div className="mx-auto mb-4 flex size-[68px] items-center justify-center rounded-full bg-[#e4f7ee] text-[30px] text-[#1d7a51]">
              ✓
            </div>
            <div className="text-2xl font-extrabold tracking-[-0.01em]">You're good to go</div>
            <div className="mx-auto mt-2 max-w-[400px] text-[14.5px] leading-[1.55] text-muted-foreground">
              Local play needs zero setup. Just share your local address with anyone on your
              network:
            </div>
            <div className="mt-[18px] inline-flex items-center gap-3 rounded-lg border bg-[#fbfdfe] px-5 py-[13px]">
              <span className="font-mono text-base font-semibold">{localJoin}</span>
              <CopyButton text={localJoin} />
            </div>
            <div className="mt-[26px]">
              <button type="button" className={`${primaryBtn} px-[30px] py-[13px] text-[13.5px]`} onClick={onClose}>
                Done
              </button>
            </div>
          </div>
        )}

        {/* Public step 1 — firewall */}
        {step === "firewall" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
              Step 1 of 3
            </div>
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Let the game through your firewall
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              Windows blocks incoming connections by default. Run this once as administrator — it
              opens LANP's ports ({portRange}) for every server, now and later.
            </div>
            <div className="mt-[18px] flex items-center gap-3.5 rounded-lg bg-[#10161f] px-[18px] py-4">
              <div className="flex-1 overflow-x-auto whitespace-nowrap font-mono text-[12.5px] leading-[1.6] text-[#a8e6ff]">
                {firewallCommands.map((command) => (
                  <div key={command}>{command}</div>
                ))}
              </div>
              <CopyButton dark text={firewallCommands.join("\n")} />
            </div>
            <div className="mt-3 text-[13px] text-[#8795a3]">
              Right‑click Command Prompt → “Run as administrator”, then paste. On Mac and Linux this
              step is usually not needed.
            </div>
            <div className="mt-6 flex justify-end">
              <button
                type="button"
                className={`${primaryBtn} px-7 py-[13px] text-[13.5px]`}
                onClick={() => setStep("portforward")}
              >
                Done — next step →
              </button>
            </div>
          </div>
        )}

        {/* Public step 2 — port forward */}
        {step === "portforward" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
              Step 2 of 3
            </div>
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Forward the ports on your router
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              Tell your router to send game traffic to this PC. In your router's admin page, find
              “Port forwarding” and add one rule:
            </div>
            <div className="mt-[18px] overflow-hidden rounded-lg border">
              {[
                { k: "External ports", v: portRange, copy: portRangeCopy },
                { k: "Internal ports", v: `${portRange} (same)`, copy: null },
                { k: "Protocol", v: "TCP & UDP", copy: null },
                { k: "Forward to (this PC)", v: lanAddress, copy: lanAddress },
              ].map((row) => (
                <div
                  key={row.k}
                  className="flex items-center justify-between border-b border-[#eef3f7] bg-white px-[18px] py-3 text-sm last:border-0"
                >
                  <span className="text-muted-foreground">{row.k}</span>
                  <span className="flex items-center gap-2.5">
                    <span className="font-mono text-[13.5px] font-semibold">{row.v}</span>
                    {row.copy && <CopyButton text={row.copy} className="px-2.5 text-[11.5px]" />}
                  </span>
                </div>
              ))}
            </div>
            <div className="mt-3 text-[13px] text-[#8795a3]">
              Router page is usually at <span className="font-mono text-xs">192.168.1.1</span> or{" "}
              <span className="font-mono text-xs">10.0.0.1</span>. Look for Port Forwarding under
              Advanced or NAT settings.
            </div>
            <div className="mt-6 flex justify-end">
              <button
                type="button"
                className={`${primaryBtn} px-7 py-[13px] text-[13.5px]`}
                onClick={() => setStep("test")}
              >
                Done — test it →
              </button>
            </div>
          </div>
        )}

        {/* Public step 3 — test */}
        {step === "test" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
              Step 3 of 3
            </div>
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Test your connection
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              We'll check from the outside whether players on the internet can reach this server.
            </div>

            <div className="mt-[18px] flex flex-col gap-2.5">
              {(result?.ports ?? guide?.rules ?? server.ports).map((port) => (
                <TestRow
                  key={`${port.name}-${port.protocol}-${portNumber(port)}`}
                  name={port.name}
                  protocol={port.protocol}
                  port={portNumber(port)}
                  running={test.isPending}
                  result={result?.ports.find(
                    (p) =>
                      p.name === port.name &&
                      p.protocol === port.protocol &&
                      p.port === portNumber(port),
                  )}
                />
              ))}
            </div>

            {result?.cgnatDetected && result.cgnatDetail && (
              <div className="mt-4 rounded-lg border border-[#eccfcd] bg-[#fdf6f5] px-[18px] py-3.5 text-[13.5px] text-[#b0433f]">
                <span className="font-bold">Carrier-Grade NAT detected.</span> {result.cgnatDetail}
              </div>
            )}

            {passed && (
              <div className="mt-4 flex items-center gap-3 rounded-lg border border-[#bfe8d2] bg-[#e4f7ee] px-[18px] py-3.5">
                <span className="text-[17px]">🎉</span>
                <span className="text-sm font-semibold text-[#1d7a51]">
                  Ready to go! Share your public address:{" "}
                  <span className="font-mono text-[13px]">{publicJoin ?? "—"}</span>
                </span>
              </div>
            )}

            <div className="mt-6 flex justify-end gap-2.5">
              {passed ? (
                <button
                  type="button"
                  className={`${primaryBtn} px-7 py-[13px] text-[13.5px]`}
                  onClick={onClose}
                >
                  Finish
                </button>
              ) : (
                <button
                  type="button"
                  className={cn(
                    primaryBtn,
                    "px-7 py-[13px] text-[13.5px]",
                    test.isPending && "bg-secondary text-[#8795a3]",
                  )}
                  onClick={runTest}
                  disabled={test.isPending}
                >
                  <Wifi className="size-4" />
                  {test.isPending ? "Testing…" : result ? "Test again" : "Test connection"}
                </button>
              )}
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

function portNumber(port: { port: number } | { nodePort: number }): number {
  return "port" in port ? port.port : port.nodePort;
}

/**
 * A pass that unlocks "Verified ✓": nothing refused, and at least one port
 * confirmed reachable. UDP can't be probed with a handshake, so an Unverified
 * UDP port that is listening locally doesn't block the pass.
 */
function reachabilityPassed(result: ServerReachability): boolean {
  if (result.cgnatDetected) return false;
  const ports = result.ports;
  if (ports.length === 0 || ports.some((p) => p.external === "Closed")) return false;
  const blockingUnverified = ports.some(
    (p) => p.external === "Unverified" && !(p.protocol.toUpperCase() === "UDP" && p.locallyListening),
  );
  return !blockingUnverified && ports.some((p) => p.external === "Open");
}

function TestRow({
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
