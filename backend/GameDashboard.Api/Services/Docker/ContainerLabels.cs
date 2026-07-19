namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Label schema for dashboard-managed containers (docs/docker-migration.md →
/// Container labels). Labels are written once at create; anything mutable
/// (last-active) lives in files, because Docker labels are immutable without
/// recreating the container.
/// </summary>
public static class ContainerLabels
{
    public const string Managed = "game-dashboard.io/managed";
    public const string ManagedValue = "true";

    /// <summary>Display/game name (the server name; k8s used the "app" label).</summary>
    public const string Game = "game-dashboard.io/game";

    /// <summary>The catalog image tag the server was deployed from (may differ from the running image for alias/matrix templates).</summary>
    public const string ImageTag = "game-dashboard.io/image-tag";

    /// <summary>JSON dictionary of the plaintext config (env) keys — the readable source of truth for GetConfig/UpdateConfig.</summary>
    public const string Config = "game-dashboard.io/config";

    /// <summary>JSON PortMapping[] — preserves port *names*, which Docker has no concept of.</summary>
    public const string Ports = "game-dashboard.io/ports";

    /// <summary>JSON ResourceSpec — preserves the original request/limit strings for display.</summary>
    public const string Resources = "game-dashboard.io/resources";

    /// <summary>Container path of the persistent data volume mount.</summary>
    public const string DataMount = "game-dashboard.io/data-mount";

    /// <summary>Requested storage size (informational — named volumes are unbounded).</summary>
    public const string StorageBytes = "game-dashboard.io/storage-bytes";

    /// <summary>JSON env-name → secret-store-key map, so config-update recreation can re-inject secrets without a template lookup.</summary>
    public const string SecretKeys = "game-dashboard.io/secret-keys";

    /// <summary>Original deploy time (round-trip "o" format). Carried through config-change recreations so CreatedAt is stable.</summary>
    public const string CreatedAt = "game-dashboard.io/created-at";

    /// <summary>Filter string for list calls.</summary>
    public const string ManagedFilter = $"{Managed}={ManagedValue}";
}
