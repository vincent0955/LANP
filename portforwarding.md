# Port Forwarding

Most of this is now handled **in the app**. Each server's **Overview** tab has a
**"Let friends join"** panel that generates the exact firewall and router
instructions for that server's real ports, and a **Test connectivity** button
that checks whether players on the internet can actually reach it (including
CGNAT detection). The **Setup** screen shows your LAN/public address and the
one-time forward range. Prefer those — they always match the ports the app
actually allocated.

This document covers the background concepts.

## How the app allocates ports

The dashboard publishes every server's host ports from a single small window,
**30000–30049 (TCP + UDP)**. Forward that whole range once to this PC on your
router and every server you deploy — now and later — is reachable, with no
per-server router trips. The exact per-server rules are generated in the app.

## Step 1 — Windows Firewall

Open the ports once as administrator. The app's per-server panel emits the exact
`New-NetFirewallRule` command for each server; the range-wide equivalent is:

```powershell
New-NetFirewallRule -DisplayName "GameDashboard UDP" -Direction Inbound -Protocol UDP -LocalPort 30000-30049 -Action Allow
New-NetFirewallRule -DisplayName "GameDashboard TCP" -Direction Inbound -Protocol TCP -LocalPort 30000-30049 -Action Allow
```

Under the bundled WSL runtime with mirrored networking, the Hyper-V firewall must
also allow inbound — the Setup screen surfaces those commands when relevant.

## Step 2 — Router Port Forwarding

Log into your router (usually `192.168.1.1` / `192.168.0.1`), find **Port
Forwarding**, and forward external ports **30000–30049** (TCP and UDP) to this
PC's LAN IP (shown on the Setup screen), internal ports **30000–30049**.

## Step 3 — Find your public IP / verify

The Setup screen shows your public IP; each server's **Test connectivity** button
verifies reachability from outside. If your IP changes often, a free DDNS service
([DuckDNS](https://www.duckdns.org), [No-IP](https://www.noip.com)) keeps a stable
address.

## CGNAT

If forwarding is configured correctly but outside connections still fail, your
ISP may use **Carrier-Grade NAT** — you don't have a real public IP and router
forwarding can't work. The app flags this automatically (public IP in
100.64.0.0/10 or a private range). Workarounds:

- Ask your ISP for a dedicated public IP.
- Use a tunnel such as [Tailscale](https://tailscale.com) to connect friends
  directly without port forwarding.
