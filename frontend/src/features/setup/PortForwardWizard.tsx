import { useCallback, useEffect, useState } from "react";
import { Wifi } from "lucide-react";
import { useNetworkInfo, useRangeReachabilityTest } from "@/api/queries";
import { CopyButton } from "@/components/CopyButton";
import { PortTestRow } from "@/components/PortTestRow";
import { useConnectivity } from "@/lib/connectivity";
import { reachabilityPassed } from "@/lib/reachability";
import { cn } from "@/lib/utils";
import { primaryBtn } from "@/features/servers/detail/ui";

export type PortForwardStep = "firewall" | "portforward" | "test";

const STEPS: PortForwardStep[] = ["firewall", "portforward", "test"];

interface Props {
  open: boolean;
  /** "firewall" for the full tutorial, "test" for the standalone test button. */
  initialStep: PortForwardStep;
  onClose: () => void;
}

/**
 * The per-server ConnectionWizard's public path, done once for the whole port
 * window instead of one server at a time: firewall → router forward → an
 * outside-in test of the range. Every server the app will ever create lands
 * inside this window, so a pass here means no server needs its own setup.
 */
export function PortForwardWizard({ open, initialStep, onClose }: Props) {
  const [step, setStep] = useState<PortForwardStep>(initialStep);
  const networkInfo = useNetworkInfo().data;
  const test = useRangeReachabilityTest();
  const setVerified = useConnectivity((s) => s.setVerified);

  // Every open restarts the wizard at its entry step with a fresh test. Entering
  // straight at the test step means the user pressed "Test public connection",
  // so run it immediately rather than making them press a second button.
  const { reset, mutate } = test;
  const runTest = useCallback(() => {
    mutate(undefined, {
      onSuccess: (r) => {
        if (reachabilityPassed(r)) setVerified(true);
      },
    });
  }, [mutate, setVerified]);

  useEffect(() => {
    if (!open) return;
    setStep(initialStep);
    reset();
    if (initialStep === "test") runTest();
  }, [open, initialStep, reset, runTest]);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  const lanAddress = networkInfo?.lanAddresses[0] ?? "localhost";
  const portRange = networkInfo
    ? `${networkInfo.nodePortRangeStart} – ${networkInfo.nodePortRangeEnd}`
    : "…";
  const portRangeCopy = networkInfo
    ? `${networkInfo.nodePortRangeStart}-${networkInfo.nodePortRangeEnd}`
    : "";
  const portCount = networkInfo
    ? networkInfo.nodePortRangeEnd - networkInfo.nodePortRangeStart + 1
    : 0;
  const firewallCommands = networkInfo
    ? [
        `netsh advfirewall firewall add rule name="LANP" dir=in action=allow protocol=TCP localport=${portRangeCopy}`,
        `netsh advfirewall firewall add rule name="LANP UDP" dir=in action=allow protocol=UDP localport=${portRangeCopy}`,
      ]
    : [];

  const stepIdx = STEPS.indexOf(step);
  const result = test.data;
  const passed = result !== undefined && reachabilityPassed(result);

  const goBack = () => {
    if (step === "portforward") setStep("firewall");
    else if (step === "test") setStep("portforward");
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
          {step !== initialStep && (
            <button
              type="button"
              onClick={goBack}
              className="cursor-pointer text-[13px] font-semibold text-[#8795a3] transition-colors hover:text-foreground"
            >
              ← Back
            </button>
          )}
          <span className="text-[15px] font-bold">
            {initialStep === "test" ? "Test public connection" : "Open all ports"}
          </span>
          {initialStep !== "test" && (
            <div className="ml-2 flex items-center gap-1.5">
              {STEPS.map((s, i) => (
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

        {/* Step 1 — firewall */}
        {step === "firewall" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
              Step 1 of 3
            </div>
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Let the games through your firewall
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              Windows blocks incoming connections by default. Run this once as administrator — it
              opens all {portCount} ports ({portRange}) the app ever uses, for every server you make
              now or later.
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

        {/* Step 2 — port forward */}
        {step === "portforward" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
              Step 2 of 3
            </div>
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Forward the whole range on your router
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              One rule covers every server. In your router's admin page, find “Port forwarding” and
              add a single range rule:
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
              <span className="font-mono text-xs">10.0.0.1</span>. If it only accepts one port per
              rule, add a rule for each port you actually use — the test below tells you either way.
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

        {/* Step 3 — test */}
        {step === "test" && (
          <div className="px-[26px] pb-[26px] pt-[30px]">
            {initialStep !== "test" && (
              <div className="text-[13px] font-bold uppercase tracking-[0.08em] text-[#2596d1]">
                Step 3 of 3
              </div>
            )}
            <div className="mt-1.5 text-[22px] font-extrabold tracking-[-0.01em]">
              Test your connection
            </div>
            <div className="mt-1.5 text-[14.5px] leading-[1.55] text-muted-foreground">
              We check the ends and the middle of the range from the outside — router rules cover a
              range as a whole, so those three answer for all {portCount || "of the"} ports. No
              server has to be running: the app holds each port open for the test.
            </div>

            <div className="mt-[18px] flex flex-col gap-2.5">
              {(result?.ports ?? placeholderPorts(networkInfo)).map((port) => (
                <PortTestRow
                  key={port.port}
                  name={port.name}
                  protocol={port.protocol}
                  port={port.port}
                  running={test.isPending}
                  result={result?.ports.find((p) => p.port === port.port)}
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
                  All set — every server you create is reachable at{" "}
                  <span className="font-mono text-[13px]">{result?.publicAddress ?? "—"}</span> on
                  its own port.
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

/**
 * The rows shown before the first test runs — the same sample the backend
 * probes (first, middle, last of the window), so the list doesn't reshuffle
 * once results arrive.
 */
function placeholderPorts(
  networkInfo: { nodePortRangeStart: number; nodePortRangeEnd: number } | undefined,
) {
  if (!networkInfo) return [];
  const { nodePortRangeStart: start, nodePortRangeEnd: end } = networkInfo;
  const middle = start + Math.floor((end - start) / 2);
  return [
    { name: "First port in range", port: start },
    { name: "Middle of range", port: middle },
    { name: "Last port in range", port: end },
  ]
    .filter((p, i, all) => all.findIndex((o) => o.port === p.port) === i)
    .map((p) => ({ ...p, protocol: "TCP" }));
}
