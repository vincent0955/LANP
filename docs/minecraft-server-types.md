# Spec: Minecraft server types, mods, and modpacks

Status: draft for review · 2026-07-15

## Summary

Replace the current two-template Minecraft setup (vanilla + "paste a Modrinth
modpack URL") with an AMP-style deploy experience:

1. **Server software dropdown** — Vanilla, Paper, Purpur, Fabric, Quilt, Forge,
   NeoForge, or a Modrinth modpack.
2. **Version dropdown** — real versions fetched live per server type, defaulting
   to latest release.
3. **Add content** — search Modrinth from inside the deploy dialog and stack any
   combination of compatible mods or plugins onto the server before it deploys.

Guiding constraint (from discussion): **zero-setup**. No API keys, no accounts,
no tokens to configure. Every metadata source in this spec is keyless. Services
that require credentials (CurseForge's API) are explicitly deferred.

## Why this is mostly UI work

Everything rides on infrastructure that already exists:

- Both Minecraft templates already use `itzg/minecraft-server`, which natively
  installs any server type from env vars (`TYPE`, `VERSION`), downloads
  individual mods/plugins from Modrinth (`MODRINTH_PROJECTS`), and installs
  whole modpacks (`MODRINTH_MODPACK`). No init containers, no custom images,
  no install scripts — the container is the installer.
- `DeployServerRequest.ConfigOverrides` already flows arbitrary key/values into
  the server's ConfigMap as env vars (`DeploymentBuilderService.Build`).
- `PUT /api/servers/{name}/config` (`ServersController.UpdateConfig`) already
  exists, which gives Phase 4 (post-deploy mod management) a foundation.

So the work is: a Minecraft-aware deploy dialog, two small read-only backend
endpoints (version lists + Modrinth search proxy), and template plumbing.

## Comparison context (what the incumbents do)

- **AMP**: first-class "Server Type" dropdown + version picker; the panel
  downloads the loader itself. No modpack/mod-repo integration (open feature
  request since 2022). We copy the dropdown UX.
- **Pterodactyl**: data-driven "eggs" whose install scripts parse
  `manifest.json` / `modrinth.index.json` and fetch everything. Powerful but
  requires egg-authoring; we get the same outcome via itzg env vars.
- **GameAP**: thin "game mod" records pointing at pre-built archives; docs
  steer nontrivial cases at importing Pterodactyl eggs. Nothing to copy.

We take AMP's UX with Pterodactyl-class content reach, minus their setup cost.

## UX

### Deploy dialog (Minecraft only)

The generic `DeployDialog` grows a Minecraft-specific panel above the raw
config list. Non-Minecraft templates are unaffected.

```
Server name  [mc-fabric-1_______________]

Server software   Version
[ Fabric      v ] [ 1.21.7 (latest) v ]

Add content                                 3 added
[ Search Modrinth: mods for Fabric 1.21.7… 🔍 ]
  ┌──────────────────────────────────────────┐
  │ Sodium        ✓ added        [Remove]    │
  │ Lithium       ✓ added        [Remove]    │
  │ Fabric API    ✓ added (auto) [Remove]    │
  └──────────────────────────────────────────┘

Memory [6G v]        ▸ Advanced configuration
```

- **Server software** groups: `Vanilla` · `Plugins — Paper, Purpur` ·
  `Mods — Fabric, Quilt, Forge, NeoForge` · `Modpack (Modrinth)`. The group
  determines what "Add content" searches (plugins vs mods vs nothing for
  vanilla v1).
- **Version**: populated from the backend per selected type; first entry
  "Latest release" (maps to `VERSION=LATEST`, i.e. no override). Snapshots
  hidden behind a toggle. If the version fetch fails, degrade to a free-text
  field — deploys must never be blocked by a metadata outage.
- **Add content**: debounced search against the backend proxy, pre-filtered to
  the selected loader + game version, so nothing incompatible is ever offered.
  Selected projects render as a removable list. Switching server software
  clears selections that no longer apply (with a confirm if non-empty).
- **Modpack** type: the content search becomes a modpack search (same Modrinth
  API, `project_type=modpack`); picking one sets `MODRINTH_MODPACK`. Pasting a
  URL/slug still works. Extra individual mods on top of a pack are allowed —
  itzg applies `MODRINTH_PROJECTS` alongside the pack.
- **Memory** becomes a first-class dropdown (it's the one knob every Minecraft
  admin touches); everything else stays under the existing generic config list,
  collapsed as "Advanced configuration".

### Quickstart

`src/features/quickstart` currently deep-links the two Minecraft templates; it
should link the single merged template, optionally passing a preselected server
type (e.g. a "Modded (Fabric)" quickstart card → type pre-set to Fabric).

## Mapping to itzg env vars

| UI selection | ConfigOverrides emitted |
|---|---|
| Vanilla | `TYPE=VANILLA`, `VERSION=<mc>` |
| Paper / Purpur | `TYPE=PAPER\|PURPUR`, `VERSION=<mc>` |
| Fabric / Quilt | `TYPE=FABRIC\|QUILT`, `VERSION=<mc>` (loader version left to latest) |
| Forge / NeoForge | `TYPE=FORGE\|NEOFORGE`, `VERSION=<mc>` (loader build left to latest promoted) |
| Modpack | `MOD_PLATFORM=MODRINTH`, `MODRINTH_MODPACK=<slug>` (no `TYPE`/`VERSION` — the pack pins both) |
| Added content | `MODRINTH_PROJECTS=slug,slug,…` + `MODRINTH_DOWNLOAD_DEPENDENCIES=required` |

`MODRINTH_DOWNLOAD_DEPENDENCIES=required` makes itzg pull hard dependencies
(e.g. Fabric API) automatically — the UI should still show auto-added deps in
the list (flagged "auto") so users aren't surprised, but correctness doesn't
depend on the UI knowing the dependency graph.

**v1 pins nothing** (slugs without version IDs): itzg resolves the newest
compatible file at each container start. Simple, and restarts pick up mod
fixes. Reproducible pinning (`slug:versionId`) is a Phase 4 concern.

## Backend

### 1. Version catalog endpoint

`GET /api/minecraft/versions?type={vanilla|paper|purpur|fabric|quilt|forge|neoforge}`
→ `{ latest: "1.21.7", versions: ["1.21.7", "1.21.6", …] }`

Keyless upstream sources, fetched server-side and cached in-memory ~30 min
(stale-while-revalidate: serve the cached list if a refresh fails):

| Type | Source |
|---|---|
| Vanilla, Fabric, Quilt | Mojang `piston-meta.mojang.com/mc/game/version_manifest_v2.json` (release entries) |
| Paper | `api.papermc.io/v2/projects/paper` |
| Purpur | `api.purpurmc.org/v2/purpur` |
| Forge | `maven.minecraftforge.net/…/promotions_slim.json` (versions with a `recommended`/`latest` promotion) |
| NeoForge | `maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml` (mapped to MC versions) |

The dropdown lists **Minecraft versions**, not loader builds — loader build
selection is deliberately out of scope (itzg picks the right build), which is
what keeps this AMP-simple.

### 2. Modrinth search proxy

`GET /api/minecraft/content/search?q=…&kind={mod|plugin|modpack}&loader=…&mcVersion=…`
→ trimmed Modrinth `/v2/search` results (slug, title, description, icon,
downloads, project_type).

Proxied rather than called from the browser so that: the required descriptive
`User-Agent` (Modrinth ToS) is set once server-side; responses are cached
(Modrinth rate limit is 300 req/min/IP); and the keyless-services policy stays
enforceable in one place. Facets: `project_type`, `categories:<loader>`,
`versions:<mcVersion>`; `kind=plugin` searches `project_type=plugin` with
`categories:paper|purpur`.

### 3. Template model changes

- **Merge** `Minecraft` and `MinecraftModded` into one curated template,
  `Minecraft (Java Edition)`; modpack becomes a server-software choice. The
  old templates' image tags must keep resolving for existing deployments (see
  Java variants below — lookup becomes alias-aware).
- Add a discriminator the frontend can branch on — smallest viable:
  `GameTemplate.Kind` enum (`Generic` default, `MinecraftJava`), serialized in
  the catalog response. This is also the seam any future game-specific deploy
  panel (e.g. tModLoader) plugs into.

### 4. Java version matrix

Old Minecraft versions crash on new JVMs, and `itzg` publishes per-Java tags.
The backend owns a version→image-tag matrix applied at deploy time:

| MC version | Image tag |
|---|---|
| ≤ 1.16.5 | `itzg/minecraft-server:java8` |
| 1.17.x | `itzg/minecraft-server:java17` |
| 1.18 – 1.20.4 | `itzg/minecraft-server:java17` |
| 1.20.5+ / LATEST / modpacks | `itzg/minecraft-server:java21` |

Because `GameCatalogService` keys templates by exact image tag, the template
gains an `ImageTagAliases` set and lookup checks aliases too. This
incidentally fixes today's wart where vanilla (`:latest`) and modded
(`:java21`) had to be separate templates *because* the catalog key is the
image tag. (Modpacks: v1 keeps java21-only, same limitation as today —
documented, not solved.)

## Failure modes and risks

- **Readiness probe vs long installs.** The deploy builder points a TCP probe
  at the first TCP port; a big Forge pack can take many minutes on first boot.
  Mitigation: the merged template must carry a generous `startupProbe` (or
  equivalent failure threshold) — measure with a large pack (e.g. ~5 min
  target) during implementation.
- **Metadata outages.** Version dropdown degrades to free text; search shows
  "Modrinth unreachable — you can still paste a project slug". Deploy path has
  zero runtime dependency on either endpoint.
- **Incompatible combinations.** Prevented structurally: search is pre-filtered
  by loader + version, and type switches clear stale selections. No validation
  matrix to maintain.
- **Changing server software on an existing server** (e.g. Paper → Fabric on
  the same PVC) corrupts the data dir in the general case. Out of scope: the
  edit-config path should not expose the type dropdown for existing servers in
  v1. Related to the known keep-data PVC reuse issue in tasks.md.

## Explicitly deferred (and why)

- **CurseForge** — requires an API key (violates zero-setup) and some authors
  block third-party downloads. Revisit only if a keyless path appears.
- **Spigot** — itzg builds it via BuildTools at boot (slow, flaky); Paper is a
  drop-in superset.
- **Loader build pinning, mod version pinning** — Phase 4.
- **Bedrock add-ons, datapacks, custom-jar upload** — later phases at most.

## Phasing

1. **Server software + version dropdowns.** Template merge, `Kind`
   discriminator, `ImageTagAliases`, versions endpoint + Java matrix,
   Minecraft deploy panel, startup-probe fix. *Ship: AMP parity.*
2. **Content search.** Modrinth proxy endpoint, add-content UI,
   `MODRINTH_PROJECTS` wiring. *Ship: "install anything and combine things."*
3. **Modpack browser.** Same search UI with `project_type=modpack`; replaces
   the paste-a-URL field (which remains as fallback).
4. **Post-deploy content management.** Server detail tab listing installed
   content (read from ConfigMap), add/remove → `UpdateConfig` + restart;
   version pinning for reproducibility.

## Open questions

1. Existing deployed servers reference the old template image tags — confirm
   alias lookup covers every live deployment before deleting the
   `MinecraftModded` template constant, or keep it hidden-but-resolvable.
2. Memory dropdown default: keep 6G for mod loaders but drop to 2–3G for
   Vanilla/Paper (today's split templates did exactly this)? Proposed: memory
   default follows server-software group.
3. Should Phase 2 show auto-added dependencies at all, or silently trust
   `MODRINTH_DOWNLOAD_DEPENDENCIES`? Proposed: show, flagged "auto".
