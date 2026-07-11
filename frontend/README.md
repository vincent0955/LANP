# Game Server Dashboard (frontend)

Windows desktop app (Tauri 2 + React + TypeScript) for the GameDashboard.Api
backend in `../backend`. Design and task breakdown live in
`../.kiro/specs/game-dashboard-frontend/`.

## Prerequisites

- Node.js LTS
- Rust toolchain + VS 2022 C++ Build Tools (only for the Tauri shell;
  browser-based dev needs Node only)
- The backend running: `dotnet run` in `../backend/GameDashboard.Api`
  (binds to http://127.0.0.1:5000)

## Development

```bash
npm install
npm run dev          # browser dev at http://localhost:5173
npm run tauri dev    # same app inside the real Tauri window
```

Both origins (`http://localhost:5173`, `http://tauri.localhost`) are in the
backend's CORS allowlist (`Dashboard:AllowedCorsOrigins` in appsettings.json).

## Checks

```bash
npm run typecheck
npm run lint
npm run test         # vitest unit tests
```

## Packaging

```bash
npm run tauri build
```

Produces an MSI and an NSIS installer under
`src-tauri/target/release/bundle/`, plus a portable
`src-tauri/target/release/app.exe`.

## Architecture notes

- `src/api/` — hand-written DTO mirrors of the backend records + fetch wrapper
  that turns RFC 7807 ProblemDetails into typed `ApiError`s.
- `src/realtime/` — one SignalR connection; REST responses are the snapshot,
  hub events are deltas patched into the TanStack Query cache; on reconnect the
  app re-subscribes hub groups and refetches. Logs live in a capped ring-buffer
  store outside the query cache.
- Settings (backend URL, optional `X-Api-Token`) persist via localStorage,
  which WebView2 keeps in the app's data directory.
