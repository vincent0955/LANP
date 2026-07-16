import { Link } from "react-router-dom";
import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { useNetworkInfo } from "@/api/queries";

/** Inline mono value with click-to-copy, for IPs, ports, and URLs. External URLs
 *  are rendered as chips rather than links: the Tauri shell has no open-url
 *  permission, so an <a target="_blank"> would silently do nothing. */
function CopyChip({ value }: { value: string }) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success("Copied", { description: value });
    } catch {
      toast.error("Couldn't access the clipboard", { description: value });
    }
  };
  return (
    <button
      type="button"
      onClick={copy}
      title="Copy to clipboard"
      className="inline-flex items-center gap-1.5 rounded-md border px-2 py-0.5 font-mono text-xs hover:bg-accent"
    >
      {value}
      <Copy className="size-3 text-muted-foreground" />
    </button>
  );
}

/** A multi-line command block with a copy button. */
function CommandBlock({ command }: { command: string }) {
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(command);
      toast.success("Command copied");
    } catch {
      toast.error("Couldn't access the clipboard");
    }
  };
  return (
    <div className="relative rounded-md border bg-muted/50">
      <pre className="overflow-x-auto p-3 pr-10 font-mono text-xs leading-relaxed">{command}</pre>
      <button
        type="button"
        onClick={copy}
        title="Copy command"
        className="absolute right-2 top-2 rounded-md border bg-background p-1.5 hover:bg-accent"
      >
        <Copy className="size-3.5 text-muted-foreground" />
      </button>
    </div>
  );
}

function Step({
  number,
  title,
  children,
}: {
  number: number;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-3 text-base">
          <span className="flex size-6 shrink-0 items-center justify-center rounded-full bg-primary font-mono text-xs text-primary-foreground">
            {number}
          </span>
          {title}
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 pl-15 text-sm text-muted-foreground [&_p]:leading-relaxed">
        {children}
      </CardContent>
    </Card>
  );
}

export function QuickStartPage() {
  const networkInfo = useNetworkInfo().data;
  const lanAddress = networkInfo?.lanAddresses[0] ?? "<this-PC's-IP>";
  const publicAddress = networkInfo?.publicAddress;
  const rangeStart = networkInfo?.nodePortRangeStart;
  const rangeEnd = networkInfo?.nodePortRangeEnd;
  const portRange = rangeStart && rangeEnd ? `${rangeStart}-${rangeEnd}` : "30000-30100";

  const firewallCommand = [
    `New-NetFirewallRule -DisplayName "Game Servers (TCP)" -Direction Inbound -Protocol TCP -LocalPort ${portRange} -Action Allow`,
    `New-NetFirewallRule -DisplayName "Game Servers (UDP)" -Direction Inbound -Protocol UDP -LocalPort ${portRange} -Action Allow`,
  ].join("\n");

  return (
    <div className="mx-auto max-w-2xl space-y-6 p-6">
      <div>
        <h1 className="text-xl font-semibold">Quick Start</h1>
        <p className="text-sm text-muted-foreground">
          From nothing to a server your friends can join — steps 3 to 5 are one-time setup that
          covers every server you'll ever deploy.
        </p>
      </div>

      <Step number={1} title="Deploy a server">
        <p>
          Open the <Link to="/library" className="text-foreground underline">Game Library</Link>,
          pick a game, and hit Deploy. The first start downloads the game inside the container, so
          give it a few minutes (modded Minecraft packs can take ten or more). The server shows{" "}
          <span className="font-medium text-foreground">Pending</span> until the game is actually
          accepting connections — watch the Logs tab if you're curious what it's doing.
        </p>
      </Step>

      <Step number={2} title="Play on your own network — no setup needed">
        <p>
          Once the server is <span className="font-medium text-foreground">Running</span>, its page
          shows copyable join addresses. On this PC, connect to{" "}
          <span className="font-mono text-foreground">localhost:&lt;node port&gt;</span>. Anyone on
          your Wi-Fi/LAN uses the LAN address (this PC is{" "}
          <CopyChip value={lanAddress} />
          ). Nothing below is required until you want friends{" "}
          <span className="font-medium text-foreground">outside your home</span> to join.
        </p>
      </Step>

      <Step number={3} title="Open Windows Firewall (one-time)">
        <p>
          Every server gets its ports from the range{" "}
          <span className="font-mono text-foreground">{portRange}</span>, so allowing that range
          once covers all current and future servers. Run this in PowerShell{" "}
          <span className="font-medium text-foreground">as Administrator</span>:
        </p>
        <CommandBlock command={firewallCommand} />
      </Step>

      <Step number={4} title="Forward the port range on your router (one-time)">
        <p>
          Log into your router's admin page — usually <CopyChip value="192.168.1.1" /> or{" "}
          <CopyChip value="192.168.0.1" /> in a browser — and find the{" "}
          <span className="font-medium text-foreground">Port Forwarding</span> section (sometimes
          under "NAT" or "Virtual Server"). Add one rule:
        </p>
        <ul className="list-disc space-y-1 pl-4">
          <li>
            External ports: <span className="font-mono text-foreground">{portRange}</span>, both{" "}
            <span className="font-medium text-foreground">TCP and UDP</span>
          </li>
          <li>
            Internal ports: the same <span className="font-mono text-foreground">{portRange}</span>
          </li>
          <li>
            Destination / internal IP: this PC — <CopyChip value={lanAddress} />
          </li>
        </ul>
        <p>
          Tip: if your router supports it, give this PC a static DHCP lease (same page or "Address
          Reservation") so its IP never changes and the rule keeps working.
        </p>
      </Step>

      <Step number={5} title="Share the address with your friends">
        <p>
          {publicAddress ? (
            <>
              Your public IP is <CopyChip value={publicAddress} />. Friends join with{" "}
              <span className="font-mono text-foreground">
                {publicAddress}:&lt;node port&gt;
              </span>{" "}
              — the exact ready-to-copy address is on each server's Overview page.
            </>
          ) : (
            <>
              Friends join with{" "}
              <span className="font-mono text-foreground">&lt;your public IP&gt;:&lt;node port&gt;</span>{" "}
              — the exact ready-to-copy address is on each server's Overview page once your public
              IP can be looked up.
            </>
          )}
        </p>
        <p>
          For modded Minecraft, friends also need the same modpack installed on their side — the
          easiest way is the free Modrinth App: install it, search the pack by name, hit Install,
          then add your server address in-game.
        </p>
      </Step>

    </div>
  );
}
