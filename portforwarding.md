# Port Forwarding Setup Guide

This guide covers everything needed to expose the game servers to the public internet.

---

## Step 1 — Find the Server PC's Local IP

Run this on the server PC:

```powershell
ipconfig
```

Look for the **IPv4 Address** on your main network adapter (e.g. `192.168.1.50`). You'll need this for the router rules below.

---

## Step 2 — Windows Firewall

Run the following in **PowerShell as Administrator** on the server PC to open all game ports:

```powershell
New-NetFirewallRule -DisplayName "CS2 UDP" -Direction Inbound -Protocol UDP -LocalPort 30015,30020 -Action Allow
New-NetFirewallRule -DisplayName "CS2 TCP" -Direction Inbound -Protocol TCP -LocalPort 30016 -Action Allow
New-NetFirewallRule -DisplayName "Insurgency UDP" -Direction Inbound -Protocol UDP -LocalPort 30025,30027 -Action Allow
New-NetFirewallRule -DisplayName "Insurgency TCP" -Direction Inbound -Protocol TCP -LocalPort 30026 -Action Allow
New-NetFirewallRule -DisplayName "Minecraft TCP" -Direction Inbound -Protocol TCP -LocalPort 30565,30575 -Action Allow
```

---

## Step 3 — Router Port Forwarding

Log into your router admin panel (usually `192.168.1.1` or `192.168.0.1`) and find the **Port Forwarding** section.

Add the following rules, pointing to the server PC's local IP from Step 1:

| External Port | Protocol | Internal IP | Internal Port | Game |
|---|---|---|---|---|
| 27015 | UDP | `<server-PC-IP>` | 30015 | CS2 game traffic |
| 27015 | TCP | `<server-PC-IP>` | 30016 | CS2 game traffic |
| 27020 | UDP | `<server-PC-IP>` | 30020 | CS2 SourceTV |
| 27016 | UDP | `<server-PC-IP>` | 30025 | Insurgency game traffic |
| 27016 | TCP | `<server-PC-IP>` | 30026 | Insurgency game traffic |
| 25565 | TCP | `<server-PC-IP>` | 30565 | Minecraft |

> Note: CS2 and Insurgency both use port 27015 internally (standard Source engine port),
> but they are forwarded from different external ports (27015 and 27016) to avoid conflicts
> when both servers are running simultaneously.

---

## Step 4 — CS2 Steam Game Server Login Token (GSLT)

CS2 requires a valid GSLT to appear on the public server browser and accept connections via Steam's network.

1. Go to https://steamcommunity.com/dev/managegameservers
2. Create a new token with App ID `730` (CS2/CS:GO share this ID)
3. Set the token in your cluster:

```powershell
kubectl edit configmap cs2-config -n game-servers
# or update the game-secrets Secret if already created:
kubectl create secret generic game-secrets -n game-servers \
  --from-literal=SRCDS_TOKEN="<your-token>" \
  --from-literal=CS2_RCONPW="<your-rcon-password>" \
  --from-literal=RCON_PASSWORD="<your-mc-rcon-password>" \
  --dry-run=client -o yaml | kubectl apply -f -
```

Then restart CS2 to pick up the token:

```powershell
kubectl rollout restart deployment/cs2-server -n game-servers
```

---

## Step 5 — Find Your Public IP

```powershell
(Invoke-WebRequest -Uri "https://api.ipify.org").Content
```

> If your public IP changes frequently, consider setting up a free DDNS service such as
> [DuckDNS](https://www.duckdns.org) or [No-IP](https://www.noip.com) so your server
> address stays stable.

---

## Step 6 — Verify Port Forwarding is Working

Go to https://canyouseeme.org, enter port `25565`, and click **Check**.

- **Success** — port forwarding is working correctly
- **Error** — recheck router rules and firewall, confirm the Minecraft pod is running

---

## Connecting from Another Network

Once everything is set up, use your public IP to connect:

| Game | Connection |
|------|-----------|
| **Minecraft** | Add server `<PublicIP>:25565` in Multiplayer |
| **CS2** | Open console (`` ` ``), type `connect <PublicIP>:27015` |
| **Insurgency** | Open console, type `connect <PublicIP>:27016` |

---

## CGNAT Warning

If port forwarding is configured correctly but outside connections still fail, your ISP may be using **Carrier-Grade NAT (CGNAT)** — meaning you don't have a true public IP and port forwarding on your router won't work.

To check: compare the WAN IP shown in your router's status page against the result of `https://api.ipify.org`. If they differ, you're behind CGNAT.

Workarounds:
- Ask your ISP for a dedicated public IP (sometimes free, sometimes a small fee)
- Use a VPN tunnel such as [Tailscale](https://tailscale.com) to connect friends directly without port forwarding
