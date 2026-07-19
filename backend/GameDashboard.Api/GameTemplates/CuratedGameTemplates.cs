using GameDashboard.Api.Models;

namespace GameDashboard.Api.GameTemplates;

/// <summary>
/// Hand-authored templates for the three games already deployed under k8s/. These
/// mirror the real manifests exactly (ports, resource limits, mount paths, config
/// keys, secret wiring) so DeploymentBuilderService produces equivalent output for
/// servers created through the dashboard as for the hand-written YAML.
///
/// See requirements.md → Req 3, Req 11. This is the seed for GameCatalogService's
/// curated overlay (Phase 7) — non-curated LinuxGSM catalog entries get generic
/// defaults instead.
/// </summary>
public static class CuratedGameTemplates
{
    private const string SecretName = "game-secrets";

    public static readonly GameTemplate Cs2 = new(
        DisplayName: "Counter-Strike 2",
        ImageTag: "joedwards32/cs2:latest",
        SteamAppId: 730,
        DataMountPath: "/home/steam/cs2-dedicated",
        DefaultStorageBytes: 30L * 1024 * 1024 * 1024, // 30Gi
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("rcon", "TCP", 27015),
            new TemplatePort("sourcetv", "UDP", 27020)
        },
        DefaultResources: new ResourceSpec(
            CpuRequest: "1000m", CpuLimit: "4000m",
            MemoryRequest: "2Gi", MemoryLimit: "4Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["CS2_SERVERNAME"] = "My CS2 Server",
            ["CS2_PW"] = "",
            ["CS2_MAXPLAYERS"] = "16",
            ["CS2_STARTMAP"] = "de_dust2",
            ["CS2_MAPGROUP"] = "mg_active",
            ["CS2_GAMEALIAS"] = "casual",
            ["CS2_LAN"] = "0",
            ["CS2_CHEATS"] = "0",
            ["CS2_LOG"] = "on",
            ["CS2_ADDITIONAL_ARGS"] = ""
        },
        SecretKeyRefs: new Dictionary<string, string>
        {
            ["SRCDS_TOKEN"] = "SRCDS_TOKEN",
            ["CS2_RCONPW"] = "CS2_RCONPW"
        });

    // Fixed 2026-07-11: previously pointed at gameservermanagers/gameserver:vints,
    // but LinuxGSM's "vints" shortname is Vintage Story — the correct Insurgency
    // (2014) shortname is "ins" (dedicated server app 237410, anonymous install).
    // LinuxGSM configures the game via files under /data, not env vars, hence the
    // empty DefaultConfig. Not yet verified with a live deploy.
    public static readonly GameTemplate Insurgency = new(
        DisplayName: "Insurgency (2014)",
        ImageTag: "gameservermanagers/gameserver:ins",
        SteamAppId: 237410,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024, // 15Gi
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("rcon", "TCP", 27015),
            new TemplatePort("sourcetv", "UDP", 27020)
        },
        DefaultResources: new ResourceSpec(
            CpuRequest: "500m", CpuLimit: "2000m",
            MemoryRequest: "1Gi", MemoryLimit: "2Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        // The LinuxGSM image manages RCON via its own config files rather than
        // an env var, so there is no secretKeyRef wiring for this template today.
        SecretKeyRefs: new Dictionary<string, string>());

    // One template covers vanilla, plugin servers (Paper/Purpur), mod loaders
    // (Fabric/Quilt/Forge/NeoForge), and Modrinth modpacks — the itzg entrypoint
    // installs whichever server software TYPE/VERSION (or MODRINTH_MODPACK) asks
    // for on first boot. See docs/minecraft-server-types.md. Deliberately no
    // TYPE/VERSION keys in DefaultConfig: the Minecraft deploy form sends them as
    // overrides only when they differ from itzg's own defaults (VANILLA/LATEST),
    // and a modpack deploy must not carry a TYPE at all (the pack pins the
    // loader). The container image is NOT this catalog-key tag: the deploy
    // builder swaps in the per-Java-version tag via MinecraftJavaImage, and
    // ImageTagAliases keeps those variant tags (and existing deployments, incl.
    // the retired modded template's java21) resolving back to this template.
    public static readonly GameTemplate Minecraft = new(
        DisplayName: "Minecraft (Java Edition)",
        ImageTag: "itzg/minecraft-server:latest",
        SteamAppId: null,
        DataMountPath: "/data",
        DefaultStorageBytes: 20L * 1024 * 1024 * 1024, // 20Gi — modpacks are 5-15Gi before world data
        DefaultPorts: new[]
        {
            new TemplatePort("game", "TCP", 25565),
            new TemplatePort("rcon", "TCP", 25575)
        },
        DefaultResources: new ResourceSpec(
            CpuRequest: "500m", CpuLimit: "2000m",
            MemoryRequest: "2Gi", MemoryLimit: "3Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["EULA"] = "TRUE",
            ["MEMORY"] = "2G",
            ["MAX_PLAYERS"] = "10",
            ["MOTD"] = "Local Minecraft Server",
            ["DIFFICULTY"] = "normal",
            ["MODE"] = "survival",
            ["ENABLE_RCON"] = "true",
            ["RCON_PORT"] = "25575"
        },
        SecretKeyRefs: new Dictionary<string, string>
        {
            ["RCON_PASSWORD"] = "RCON_PASSWORD"
        },
        Kind: TemplateKind.MinecraftJava,
        ImageTagAliases: new[]
        {
            MinecraftJavaImage.Java8,
            MinecraftJavaImage.Java17,
            MinecraftJavaImage.Java21,
            MinecraftJavaImage.Java25
        });

    /// <summary>All curated templates, keyed by image tag for catalog lookup.</summary>
    public static IReadOnlyDictionary<string, GameTemplate> All => _all.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, GameTemplate>> _all = new(BuildAll);

    private static IReadOnlyDictionary<string, GameTemplate> BuildAll()
    {
        var templates = new[]
        {
            Cs2, Insurgency, Minecraft,
            TeamFortress2, Rust, Valheim, ProjectZomboid, ArkSurvivalEvolved,
            Terraria, SevenDaysToDie, Left4Dead2, GarrysMod, Palworld, VRising, Satisfactory,
            MinecraftBedrock, Hytale, SonsOfTheForest, Factorio, TerrariaTModLoader, ConanExiles
        };

        return templates.ToDictionary(t => t.ImageTag, t => t);
    }

    /// <summary>
    /// Alias-aware lookup: resolves a template by its primary image tag or any of
    /// its ImageTagAliases (deployed servers may run a variant image, e.g.
    /// Minecraft's per-Java tags). Primary tags win on collision.
    /// </summary>
    public static GameTemplate? ResolveByTag(string tag) =>
        _byAnyTag.Value.TryGetValue(tag, out var template) ? template : null;

    private static readonly Lazy<IReadOnlyDictionary<string, GameTemplate>> _byAnyTag = new(() =>
    {
        var map = new Dictionary<string, GameTemplate>(All);
        foreach (var template in All.Values)
        {
            foreach (var alias in template.ImageTagAliases ?? Array.Empty<string>())
            {
                map.TryAdd(alias, template);
            }
        }
        return map;
    });

    // --- Additional popular LinuxGSM-backed games (Req 11: ~15-game curated catalog) ---
    //
    // These use the shared gameservermanagers/gameserver:{tag} LinuxGSM image, same as
    // Insurgency's "ins" tag. Each installs its game via anonymous steamcmd on first
    // start (games that can't install anonymously, like Terraria, use other images).
    // Ports/resources are reasonable defaults for a small personal server; users can
    // override resources per-deployment via DeployServerRequest.

    public static readonly GameTemplate TeamFortress2 = new(
        DisplayName: "Team Fortress 2",
        ImageTag: "gameservermanagers/gameserver:tf2",
        SteamAppId: 232250,
        DataMountPath: "/data",
        DefaultStorageBytes: 20L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("rcon", "TCP", 27015)
        },
        DefaultResources: new ResourceSpec("500m", "2000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Rust = new(
        DisplayName: "Rust",
        ImageTag: "gameservermanagers/gameserver:rust",
        SteamAppId: 258550,
        DataMountPath: "/data",
        DefaultStorageBytes: 25L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 28015),
            new TemplatePort("rcon", "TCP", 28016)
        },
        DefaultResources: new ResourceSpec("2000m", "4000m", "4Gi", "8Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Valheim = new(
        DisplayName: "Valheim",
        ImageTag: "gameservermanagers/gameserver:vh",
        SteamAppId: 896660,
        DataMountPath: "/data",
        DefaultStorageBytes: 5L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 2456)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "2Gi", "4Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate ProjectZomboid = new(
        DisplayName: "Project Zomboid",
        ImageTag: "gameservermanagers/gameserver:pz",
        SteamAppId: 380870,
        DataMountPath: "/data",
        DefaultStorageBytes: 10L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 16261),
            new TemplatePort("rcon", "TCP", 27015)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "2Gi", "4Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate ArkSurvivalEvolved = new(
        DisplayName: "ARK: Survival Evolved",
        ImageTag: "gameservermanagers/gameserver:ark",
        SteamAppId: 376030,
        DataMountPath: "/data",
        DefaultStorageBytes: 40L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 7777),
            new TemplatePort("query", "UDP", 27015)
        },
        DefaultResources: new ResourceSpec("2000m", "4000m", "6Gi", "10Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    // Fixed 2026-07-11: the LinuxGSM image can't install Terraria — its server
    // files are not available via anonymous steamcmd (requires a Steam login that
    // owns the game). This image runs the official vanilla server binaries from
    // terraria.org instead: env-driven, auto-creates the world on first boot
    // (AUTOCREATE: 1=small 2=medium 3=large), single config/world volume.
    public static readonly GameTemplate Terraria = new(
        DisplayName: "Terraria",
        ImageTag: "passivelemon/terraria-docker:latest",
        SteamAppId: null,
        DataMountPath: "/opt/terraria/config",
        DefaultStorageBytes: 2L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "TCP", 7777)
        },
        DefaultResources: new ResourceSpec("500m", "1000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["WORLDNAME"] = "world",
            ["AUTOCREATE"] = "2",
            ["MAXPLAYERS"] = "8",
            ["PASSWORD"] = ""
        },
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate SevenDaysToDie = new(
        DisplayName: "7 Days to Die",
        ImageTag: "gameservermanagers/gameserver:sdtd",
        SteamAppId: 294420,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 26900),
            new TemplatePort("query", "UDP", 26901)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "3Gi", "6Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Left4Dead2 = new(
        DisplayName: "Left 4 Dead 2",
        ImageTag: "gameservermanagers/gameserver:l4d2",
        SteamAppId: 222860,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("rcon", "TCP", 27015)
        },
        DefaultResources: new ResourceSpec("500m", "2000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate GarrysMod = new(
        DisplayName: "Garry's Mod",
        ImageTag: "gameservermanagers/gameserver:gmod",
        SteamAppId: 4020,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("rcon", "TCP", 27015)
        },
        DefaultResources: new ResourceSpec("500m", "2000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Palworld = new(
        DisplayName: "Palworld",
        ImageTag: "gameservermanagers/gameserver:pw",
        SteamAppId: 2394010,
        DataMountPath: "/data",
        DefaultStorageBytes: 20L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 8211)
        },
        DefaultResources: new ResourceSpec("2000m", "4000m", "6Gi", "10Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    // Fixed 2026-07-11: previously pointed at gameservermanagers/gameserver:vpmc,
    // but LinuxGSM's "vpmc" shortname is Velocity Proxy MC (a Minecraft proxy),
    // not V Rising. The V Rising dedicated server is Windows-only, so this image
    // runs it under Wine; it downloads via anonymous steamcmd (app 1829350).
    // Mounting the PVC at /mnt/vrising covers both the server-files and
    // persistentdata subdirectories the image uses. Not yet verified with a live
    // deploy.
    public static readonly GameTemplate VRising = new(
        DisplayName: "V Rising",
        ImageTag: "trueosiris/vrising:latest",
        SteamAppId: 1829350,
        DataMountPath: "/mnt/vrising",
        DefaultStorageBytes: 10L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 9876),
            new TemplatePort("query", "UDP", 9877)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "3Gi", "6Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["SERVERNAME"] = "V Rising Server",
            ["WORLDNAME"] = "world1"
        },
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Satisfactory = new(
        DisplayName: "Satisfactory",
        ImageTag: "gameservermanagers/gameserver:sf",
        SteamAppId: 1690800,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 7777)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "4Gi", "8Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    // --- Added 2026-07-12 (user-requested catalog expansion) ---
    //
    // Each image below was checked for active maintenance before inclusion; none
    // has been live-deploy verified yet. Researched but deliberately excluded:
    // Dune Awakening (Funcom self-hosting is a Windows Hyper-V VM appliance, no
    // Docker), CS:GO (cm2network/csgo archived after CS2's release; CS2 template
    // covers it), ARMA 3 (every image requires a real Steam login — conflicts
    // with the no-Steam-credentials-in-cluster policy), Space Engineers (existing
    // images are file-config driven, need multiple mounts, and look stale).

    // Bedrock has no RCON protocol and its single port is UDP, so there is no
    // readiness probe and no RCON tab for this template.
    public static readonly GameTemplate MinecraftBedrock = new(
        DisplayName: "Minecraft (Bedrock Edition)",
        ImageTag: "itzg/minecraft-bedrock-server:latest",
        SteamAppId: null,
        DataMountPath: "/data",
        DefaultStorageBytes: 5L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 19132)
        },
        DefaultResources: new ResourceSpec("500m", "2000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["EULA"] = "TRUE",
            ["SERVER_NAME"] = "Bedrock Server",
            ["GAMEMODE"] = "survival",
            ["DIFFICULTY"] = "normal",
            ["MAX_PLAYERS"] = "10",
            ["VERSION"] = "LATEST"
        },
        SecretKeyRefs: new Dictionary<string, string>());

    // First boot requires a one-time browser login: the container prints an
    // OAuth URL to stdout (visible in the app's Logs tab); after the user signs
    // in with their Hytale account, tokens persist in the data volume and later
    // starts are unattended. Game traffic is QUIC over UDP.
    public static readonly GameTemplate Hytale = new(
        DisplayName: "Hytale",
        ImageTag: "indifferentbroccoli/hytale-server-docker:latest",
        SteamAppId: null,
        DataMountPath: "/home/hytale/server-files",
        DefaultStorageBytes: 10L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 5520)
        },
        DefaultResources: new ResourceSpec("1000m", "4000m", "3Gi", "6Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["SERVER_NAME"] = "Hytale Server",
            ["MAX_PLAYERS"] = "20",
            ["VIEW_DISTANCE"] = "12",
            // Keep the JVM heap under the container memory limit (image default is 8G).
            ["MAX_MEMORY"] = "3G"
        },
        SecretKeyRefs: new Dictionary<string, string>());

    // Windows server binaries run under Wine; installs via anonymous steamcmd on
    // first start. Gameplay settings live in userdata/dedicatedserver.cfg inside
    // the data volume, not env vars, hence the empty DefaultConfig.
    public static readonly GameTemplate SonsOfTheForest = new(
        DisplayName: "Sons of the Forest",
        ImageTag: "jammsen/sons-of-the-forest-dedicated-server:latest",
        SteamAppId: 2465200,
        DataMountPath: "/sonsoftheforest",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 8766),
            new TemplatePort("query", "UDP", 27016),
            new TemplatePort("blobsync", "UDP", 9700)
        },
        DefaultResources: new ResourceSpec("2000m", "4000m", "4Gi", "8Gi"),
        DefaultConfig: new Dictionary<string, string>(),
        SecretKeyRefs: new Dictionary<string, string>());

    // Official headless server (not steamcmd-installed). The image writes a
    // random RCON password to <volume>/config/rconpw on first boot — the app
    // can't read it, so the RCON tab fails soft for Factorio (see tasks.md
    // known-issue 5: app-managed RCON passwords for every template).
    public static readonly GameTemplate Factorio = new(
        DisplayName: "Factorio",
        ImageTag: "factoriotools/factorio:stable",
        SteamAppId: null,
        DataMountPath: "/factorio",
        DefaultStorageBytes: 5L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 34197),
            new TemplatePort("rcon", "TCP", 27015)
        },
        DefaultResources: new ResourceSpec("500m", "2000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["GENERATE_NEW_SAVE"] = "true",
            ["LOAD_LATEST_SAVE"] = "true"
        },
        SecretKeyRefs: new Dictionary<string, string>());

    // Same maintainer/repo as the vanilla Terraria template (the standalone
    // tmodloader1.4-docker image was merged into terraria-docker in 2025). To
    // load mods, drop a modpack folder into ModPacks/ inside the data volume and
    // set a MODPACK config override with its name; without one it runs plain tML.
    public static readonly GameTemplate TerrariaTModLoader = new(
        DisplayName: "Terraria (tModLoader)",
        ImageTag: "passivelemon/terraria-docker:tmodloader-latest",
        SteamAppId: null,
        DataMountPath: "/opt/terraria/config",
        DefaultStorageBytes: 4L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "TCP", 7777)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "2Gi", "4Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["WORLDNAME"] = "world",
            ["AUTOCREATE"] = "2",
            ["MAXPLAYERS"] = "8",
            ["PASSWORD"] = ""
        },
        SecretKeyRefs: new Dictionary<string, string>());

    // Windows/UE server under Wine, installed via anonymous steamcmd. The image's
    // compose file splits /conanexiles and /conanexiles/ConanSandbox/Saved into
    // two volumes, but Saved is a subdirectory — one PVC at /conanexiles persists
    // both the ~30Gi install and the world database.
    public static readonly GameTemplate ConanExiles = new(
        DisplayName: "Conan Exiles",
        ImageTag: "ghcr.io/balnaimi/conan-exiles-server:latest",
        SteamAppId: 443030,
        DataMountPath: "/conanexiles",
        DefaultStorageBytes: 40L * 1024 * 1024 * 1024,
        // No rcon port here even though the image supports TCP 25575: RCON is
        // optional in this image, and the deploy builder targets the first TCP
        // port with the readiness probe — if RCON weren't enabled, the server
        // would sit at Pending forever. UDP-only means no probe, like Palworld.
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 7777),
            new TemplatePort("raw", "UDP", 7778),
            new TemplatePort("query", "UDP", 27015)
        },
        DefaultResources: new ResourceSpec("2000m", "4000m", "4Gi", "8Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["SERVER_NAME"] = "Conan Exiles Server",
            ["SERVER_TYPE"] = "pve",
            ["MAX_PLAYERS"] = "10"
        },
        SecretKeyRefs: new Dictionary<string, string>());
}
