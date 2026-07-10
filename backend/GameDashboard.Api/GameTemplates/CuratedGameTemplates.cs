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
            new TemplatePort("game-tcp", "TCP", 27015),
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

    public static readonly GameTemplate Insurgency = new(
        DisplayName: "Insurgency (2014)",
        ImageTag: "gameservermanagers/gameserver:vints",
        SteamAppId: 17700,
        DataMountPath: "/data",
        DefaultStorageBytes: 15L * 1024 * 1024 * 1024, // 15Gi
        DefaultPorts: new[]
        {
            new TemplatePort("game-udp", "UDP", 27015),
            new TemplatePort("game-tcp", "TCP", 27015),
            new TemplatePort("sourcetv", "UDP", 27020)
        },
        DefaultResources: new ResourceSpec(
            CpuRequest: "500m", CpuLimit: "2000m",
            MemoryRequest: "1Gi", MemoryLimit: "2Gi"),
        DefaultConfig: new Dictionary<string, string>
        {
            ["STEAMAPPID"] = "17700",
            ["GAME_MAP"] = "ministry",
            ["GAME_MAXPLAYERS"] = "16"
        },
        // The vints/LinuxGSM image manages RCON via its own config files rather than
        // an env var, so there is no secretKeyRef wiring for this template today.
        SecretKeyRefs: new Dictionary<string, string>());

    public static readonly GameTemplate Minecraft = new(
        DisplayName: "Minecraft (Java Edition)",
        ImageTag: "itzg/minecraft-server:latest",
        SteamAppId: null,
        DataMountPath: "/data",
        DefaultStorageBytes: 10L * 1024 * 1024 * 1024, // 10Gi
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
            ["TYPE"] = "VANILLA",
            ["VERSION"] = "LATEST",
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
            Terraria, SevenDaysToDie, Left4Dead2, GarrysMod, Palworld, VRising, Satisfactory
        };

        return templates.ToDictionary(t => t.ImageTag, t => t);
    }

    // --- Additional popular LinuxGSM-backed games (Req 11: ~15-game curated catalog) ---
    //
    // These use the shared gameservermanagers/gameserver:{tag} LinuxGSM image, same as
    // Insurgency's "vints" tag. Each installs its game via steamcmd on first start.
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
            new TemplatePort("game-tcp", "TCP", 27015)
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

    public static readonly GameTemplate Terraria = new(
        DisplayName: "Terraria",
        ImageTag: "gameservermanagers/gameserver:terraria",
        SteamAppId: null,
        DataMountPath: "/data",
        DefaultStorageBytes: 2L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "TCP", 7777)
        },
        DefaultResources: new ResourceSpec("500m", "1000m", "1Gi", "2Gi"),
        DefaultConfig: new Dictionary<string, string>(),
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
            new TemplatePort("game-tcp", "TCP", 27015)
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
            new TemplatePort("game-tcp", "TCP", 27015)
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

    public static readonly GameTemplate VRising = new(
        DisplayName: "V Rising",
        ImageTag: "gameservermanagers/gameserver:vpmc",
        SteamAppId: 1604030,
        DataMountPath: "/data",
        DefaultStorageBytes: 10L * 1024 * 1024 * 1024,
        DefaultPorts: new[]
        {
            new TemplatePort("game", "UDP", 9876),
            new TemplatePort("query", "UDP", 9877)
        },
        DefaultResources: new ResourceSpec("1000m", "2000m", "3Gi", "6Gi"),
        DefaultConfig: new Dictionary<string, string>(),
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
}
