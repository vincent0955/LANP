# Game Dashboard Backend

A C# / ASP.NET Core backend that manages a Kubernetes-based game server cluster
(CS2, Insurgency, Minecraft, and 12 other curated titles) through a REST API and
a SignalR hub for real-time logs, status, and metrics.

See `.kiro/specs/game-dashboard-backend/` for the full requirements, design, and
task breakdown this was built from.

---

## Prerequisites

1. **.NET 10 SDK** — check with `dotnet --version`. Get it from
   [dotnet.microsoft.com](https://dotnet.microsoft.com/download) if missing.
2. **Docker Desktop with Kubernetes enabled** — Settings → Kubernetes → Enable
   Kubernetes → Apply & Restart. Verify with:
   ```powershell
   kubectl config current-context   # should print "docker-desktop"
   kubectl get nodes                # should show one Ready node
   ```
3. **The `game-servers` namespace and manifests already applied** — see the root
   `k8s/` directory and `portforwarding.md` for the underlying cluster setup this
   backend manages. The backend will create the namespace automatically on first
   run if it doesn't exist, but the curated game Deployments/Services/PVCs under
   `k8s/` are expected to already be applied for the three original games
   (CS2, Insurgency, Minecraft) if you want to manage those specific servers.
4. **The `game-secrets` Secret** — holds the CS2 Steam Game Server Login Token and
   RCON passwords. Create it via `POST /api/setup/secrets` once the backend is
   running (see below), or with `kubectl create secret generic game-secrets ...`
   directly.
5. *(Optional)* **metrics-server** — not installed by default on Docker Desktop.
   Without it, `/api/metrics` and automatic scaling report "unavailable" rather
   than failing; everything else works normally. To install it:
   ```powershell
   kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml
   # Docker Desktop's kubelet certs are self-signed, so metrics-server also needs:
   kubectl patch deployment metrics-server -n kube-system --type=json `
     -p '[{"op":"add","path":"/spec/template/spec/containers/0/args/-","value":"--kubelet-insecure-tls"}]'
   ```

---

## Running Locally

**Quickest path** — from the `backend/` directory, run the launch script, which
checks kubectl/cluster reachability first and prints the health/status URLs:

```powershell
cd backend
.\run-dev.ps1
```

Or run it directly with plain `dotnet run`:

```powershell
cd backend/GameDashboard.Api
dotnet run
```

The backend starts on `http://127.0.0.1:5000` (loopback only, by design — see
Security below). You should see:

```
Now listening on: http://127.0.0.1:5000
Hosting environment: Development
```

Verify it's healthy:

```powershell
curl http://127.0.0.1:5000/api/health
curl http://127.0.0.1:5000/api/setup/status
```

`api/setup/status` tells you exactly what's missing (secrets not configured,
metrics-server absent, etc.) as warnings — nothing here is a hard failure that
prevents the backend from running.

### First-time secret setup

If `secretsConfigured` is `false` in the setup status response, configure the
Secret once via the API (values never touch disk or logs on the backend side):

```powershell
$body = @{
    SRCDS_TOKEN   = "<your-steam-gslt-token>"
    CS2_RCONPW    = "<a-password-you-choose>"
    RCON_PASSWORD = "<a-password-you-choose>"
} | ConvertTo-Json

Invoke-WebRequest -Uri "http://127.0.0.1:5000/api/setup/secrets" -Method POST `
    -Body $body -ContentType "application/json"
```

---

## Configuration (`appsettings.json`)

All options live under the `Dashboard` section. Override any of them via
`appsettings.Development.json`, environment variables (`Dashboard__Namespace`,
etc.), or command-line arguments — standard ASP.NET Core configuration precedence
applies.

| Key | Default | Description |
|---|---|---|
| `Dashboard:Namespace` | `game-servers` | Kubernetes namespace all game servers live in. |
| `Dashboard:SecretName` | `game-secrets` | Name of the Secret holding RCON passwords / the CS2 GSLT token. |
| `Dashboard:BindAddress` | `127.0.0.1` | Address Kestrel binds to. Loopback-only by default. |
| `Dashboard:Port` | `5000` | Port Kestrel listens on. |
| `Dashboard:RequireAuthWhenExposed` | `true` | If `BindAddress` is non-loopback, require `Dashboard:ApiToken` and the `X-Api-Token` header on every request. |
| `Dashboard:ApiToken` | *(none)* | Shared-secret token. **Required** if `BindAddress` is non-loopback and `RequireAuthWhenExposed` is `true` — the app refuses to start otherwise. |
| `Dashboard:AutoScale:Enabled` | `true` | Whether the automatic scale-down background loop runs at all. |
| `Dashboard:AutoScale:IntervalSeconds` | `30` | How often the auto-scale loop evaluates. |
| `Dashboard:AutoScale:MemoryHighWaterPercent` | `80` | Node memory usage percentage above which empty servers are scaled down. Ignored entirely (no-op) if metrics-server is not installed. |
| `Dashboard:Metrics:PushIntervalSeconds` | `5` | How often `MetricsUpdate` is pushed to SignalR subscribers. |

### Exposing beyond localhost

By default the backend only accepts connections from the same machine. To reach
it from another device on your network (e.g. controlling the dashboard from a
laptop while the servers run on a spare PC), set a bind address and a token:

```json
{
  "Dashboard": {
    "BindAddress": "0.0.0.0",
    "ApiToken": "some-long-random-string-you-generate"
  }
}
```

Every request from a non-loopback client must then include the header
`X-Api-Token: some-long-random-string-you-generate`, or it is rejected with 401.
The app **will not start** if you set a non-loopback `BindAddress` without also
setting `ApiToken` — this is intentional, to prevent accidentally running the
dashboard unauthenticated on your network.

---

## Running the Tests

The test suite is split into fast unit tests (no dependencies) and integration/
contract tests (require a real, reachable Kubernetes cluster).

```powershell
cd backend

# Default: unit tests only (fast, no cluster required) — 232 tests
dotnet test --filter "Category!=Integration"

# Integration + contract tests (requires Docker Desktop with Kubernetes running) — 14 tests
# This deploys and tears down real, temporary servers in the game-servers namespace.
dotnet test --filter "Category=Integration"

# Everything
dotnet test
```

> Integration tests create and delete their own uniquely-named, temporary servers
> (e.g. `it-lifecycle-abc123`) — they do not touch your existing CS2/Insurgency/
> Minecraft deployments, and clean up after themselves even if a test fails
> (cleanup runs in a `finally` block).

---

## Project Structure

```
backend/
├── GameDashboard.Api/
│   ├── Controllers/        REST endpoints
│   ├── Hubs/                SignalR hub
│   ├── RealTime/            Background services + SignalR message contracts
│   ├── Services/            Business logic (Kubernetes, RCON, metrics, auto-scale, catalog)
│   ├── GameTemplates/       Curated game catalog definitions
│   ├── Models/              API/domain data models
│   ├── Middleware/          Exception handling, token auth
│   ├── Exceptions/          Domain-specific exception types
│   ├── Configuration/       Strongly-typed options
│   └── Program.cs
└── GameDashboard.Tests/
    ├── Services/, RealTime/, Middleware/, Configuration/, GameTemplates/   Unit tests
    └── Integration/         Integration + contract tests (require a real cluster)
```

---

## API Overview

See `.kiro/specs/game-dashboard-backend/design.md` → **API Surface** for the full
REST + SignalR contract. Quick reference:

```
GET    /api/health
GET    /api/setup/status
POST   /api/setup/secrets

GET    /api/servers
POST   /api/servers
GET    /api/servers/{name}
POST   /api/servers/{name}/scale
DELETE /api/servers/{name}
GET    /api/servers/{name}/config
PUT    /api/servers/{name}/config
POST   /api/servers/{name}/rcon

GET    /api/games
GET    /api/metrics

/hubs/dashboard   (SignalR: SubscribeLogs, UnsubscribeLogs, SubscribeEvents, SubscribeMetrics)
```
