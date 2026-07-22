# Pre-UI functional gaps — status & remaining work

Tracks the five workstreams scoped in `.claude/plans/`. WS1 and WS2 are
implemented and tested. WS3–WS5 require a live machine (running Docker engine,
multi-GB game-image pulls, or a GitHub release) and are documented here with
exactly what remains and how to finish them.

## ✅ WS1 — World backups (done)

Game-agnostic: every server keeps all state in one volume `{name}-data`, so a
backup is a gzip tar of that volume via a throwaway `busybox` helper streamed
over the Docker API (works across the WSL2 boundary). Manual export/import +
scheduled auto-backups with retention. See `BackupService`,
`BackupSchedulerService`, `BackupSettingsStore`, the Backups tab, and the
Settings schedule card.

## ✅ WS2 — Port-forwarding detect / verify / guide (done)

`NetworkDiagnosticsService`: CGNAT detection, per-port local-listening check +
best-effort keyless outside-in probe (check-host.net, degrades to "unverified"),
and per-server firewall/router guide generation. Server Overview →
"Let friends join"; Setup → Network card. The app never changes the network
itself. `portforwarding.md` rewritten to point at the in-app feature.

## ✅/⚠️ WS3 — App-managed RCON passwords for LinuxGSM templates (spike complete; no clean fix)

**Both the cheap path AND the file-seed path turned out not to be viable
uniformly (verified live 2026-07-21 against a running `gameservermanagers/gameserver:ins` container).**

1. **Env injection — dead.** Our LinuxGSM templates use
   `gameservermanagers/gameserver:<tag>` (the *docker-gameserver* image), which
   is **file-config driven only** — no `rconpassword` env var. (The `LGSM_*` env
   convention belongs to the *separate, experimental* `linuxgsm-docker` image.)
2. **File-seed — not uniform.** LinuxGSM *does* generate its config structure
   early (`/data/config-lgsm/<game>/{_default,common,<instance>}.cfg` appear
   before the game download finishes), so seeding a file is timing-feasible.
   **But** Insurgency's `_default.cfg` has **no `rconpassword` LinuxGSM variable
   at all** — its `startparameters` use `+servercfgfile ${servercfg}`, i.e. RCON
   is a *game* cvar (`rcon_password`) in the game's own `server.cfg` under
   `serverfiles/…/cfg/`, which only exists *after* the multi-GB install. So the
   seed target is per-game and post-install, not a single uniform file.

Consequence: there is **no clean, uniform way** to make the app own RCON for the
LinuxGSM games; it would be bespoke per game (and for Source games like
Insurgency, a post-install write into the game's `server.cfg`). Wiring anything
into the templates now would falsely show a working "Managed" RCON password, so
they are **deliberately left unwired**.

**Shipped (safe seam):** `RconService.ResolveRconStoreKey` no longer hardcodes
`CS2_RCONPW`; it derives the RCON store key from the server's template
(`ManagedSecretKeys`). Templates that legitimately wire a managed RCON key light
up automatically; LinuxGSM templates declare none and keep failing soft — no
false claims. **Recommendation: treat per-game LinuxGSM RCON as out of scope**
unless a specific game proves easy (has a real LinuxGSM `rconpassword` var).

## ⚠️/✅ WS4 — Catalog live-deploy verification (partially done live 2026-07-21)

Verified against the running engine. Two real breakages found and fixed —
neither was catchable by tag-checking alone (all 20 image tags resolve fine):

| Template | Result |
|---|---|
| Terraria | ✅ boots ("Server started") |
| Minecraft Bedrock | ✅ boots (healthy) |
| Factorio | ❌→✅ **FIXED**: `GENERATE_NEW_SAVE=true` exits 1 without `SAVE_NAME`; added `SAVE_NAME=world` (verified running) |
| Terraria tModLoader | ❌ **REMOVED**: image exits immediately without a `MODPACK` + pre-staged mod files in the volume — no zero-config path |
| Insurgency | ✅ LinuxGSM steamcmd install proceeds + container healthy (validates the whole `gameservermanagers/gameserver` path) |

**Still to verify (heavy Wine/steamcmd installs — long + tens of GB):** V Rising,
Sons of the Forest, Hytale, Conan Exiles. Images all resolve (manifest-inspect
OK); full boot not completed here. The **10 other LinuxGSM games** (TF2, Rust,
Valheim, Project Zomboid, ARK, 7 Days to Die, L4D2, Garry's Mod, Palworld,
Satisfactory) share Insurgency's exact image + anonymous-steamcmd mechanism,
which is now validated end-to-end — remaining per-game risk is only whether each
shortname installs anonymously (a LinuxGSM-internal concern).

To finish: deploy each remaining one (via the app or `POST /api/servers`), watch
it reach Running (or healthy Pending during download), fix config in
`CuratedGameTemplates.cs` if needed.

## ✅/⚠️ WS5 — Bundled runtime tarball (rebuilt to v4; only the release upload remains)

**Done 2026-07-21:** the tarball
`scripts/runtime/gamedashboard-wsl-rootfs.tar.gz` was **rebuilt to
runtime-version 4** and put in place (the old v3 is backed up alongside it as
`gamedashboard-wsl-rootfs.tar.gz.v3.bak` — delete when happy). Verified the new
tarball: version `4`, 80 MB, contains `dockerd` + `etc/wsl.conf` +
`start-dockerd.sh` with the `loopback0` DNAT-bypass rule baked into the boot
script. This unblocks `npm run tauri build` (prepare-sidecar bundles it) and
gives fresh installs v4 from first boot.

Build ran inside an `alpine:3.21` container against the running engine (the
bundled WSL engine can't bind-mount Windows paths, so the script was
`docker cp`'d in and the result `docker cp`'d out; note the script needed its
CRLF stripped first). `build-wsl-distro.sh` already carried `RUNTIME_VERSION=4`.

**Remaining (needs repo release perms — your action):** upload the v4 tarball as
the GitHub release asset `RuntimeOptions.DistroDownloadUrl` points at
(`.../releases/latest/download/gamedashboard-wsl-rootfs.tar.gz`), and confirm the
URL matches your remote. This is only the download *fallback* for dev/no-bundle
installs — the installer prefers the bundled copy, which is now v4.

**Optional:** re-import the running distro to v4 (it's currently v3, functional —
the app re-ensures the loopback0 rule at runtime). The app can offer this via its
runtime-version check; not required.
