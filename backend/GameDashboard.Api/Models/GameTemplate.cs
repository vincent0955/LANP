using System.Text.Json.Serialization;

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
    IReadOnlyDictionary<string, string> SecretKeyRefs,
    /// <summary>
    /// Discriminator for templates that get a specialized deploy experience in the
    /// frontend and specialized manifest handling (e.g. the Minecraft Java version →
    /// image matrix). Defaults keep every existing template Generic.
    /// </summary>
    TemplateKind Kind = TemplateKind.Generic,
    /// <summary>
    /// Additional image tags that resolve to this template in catalog lookups.
    /// Deploys may run a variant image (Minecraft's per-Java-version tags) and
    /// existing deployments may reference retired tags; both must keep resolving.
    /// </summary>
    IReadOnlyList<string>? ImageTagAliases = null,
    /// <summary>
    /// The subset of <see cref="SecretKeyRefs"/> store keys the app generates and
    /// owns itself (e.g. RCON passwords) rather than asking the user for. These
    /// are auto-filled with a strong random value on first need, so a server whose
    /// only secrets are managed starts with no manual entry. User-supplied secrets
    /// (e.g. a Steam GSLT) stay out of this set and remain the start gate.
    /// </summary>
    IReadOnlyList<string>? ManagedSecretKeys = null)
{
    /// <summary>Non-null view of <see cref="ManagedSecretKeys"/> (server-side only).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ManagedSecretKeysOrEmpty =>
        ManagedSecretKeys ?? Array.Empty<string>();
}

/// <summary>Serialized as a string (global JsonStringEnumConverter), e.g. "MinecraftJava".</summary>
public enum TemplateKind
{
    Generic,
    MinecraftJava
}
