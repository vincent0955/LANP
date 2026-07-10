namespace GameDashboard.Api.Models;

/// <summary>
/// A deployable game definition. Curated templates (CS2, Insurgency, Minecraft) are
/// hand-authored to mirror the hand-written manifests under k8s/. Non-curated entries
/// from the LinuxGSM catalog (Phase 7) get generic defaults instead of these rich ones.
/// See requirements.md → Req 3, Req 11; design.md → GameCatalogService.
/// </summary>
public record GameTemplate(
    string DisplayName,
    string ImageTag,
    int? SteamAppId,
    string DataMountPath,
    long DefaultStorageBytes,
    IReadOnlyList<TemplatePort> DefaultPorts,
    ResourceSpec DefaultResources,
    IReadOnlyDictionary<string, string> DefaultConfig,
    /// <summary>
    /// Config keys whose values must come from the game-secrets Secret via
    /// secretKeyRef rather than the ConfigMap (Req 6.4, Req 14.3). The dictionary
    /// value is the key name inside the Secret (may differ from the env var name).
    /// </summary>
    IReadOnlyDictionary<string, string> SecretKeyRefs);
