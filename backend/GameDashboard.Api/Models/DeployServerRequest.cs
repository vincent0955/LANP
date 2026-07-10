namespace GameDashboard.Api.Models;

/// <summary>
/// Request to deploy a new game server. Full validation and manifest generation
/// arrive in Phase 3 (DeploymentBuilderService) / Phase 4 (DeployServerAsync).
/// Defined now so IKubernetesService's signature matches the design doc.
/// </summary>
public record DeployServerRequest(
    string Name,
    string ImageTag,
    ResourceSpec? Resources,
    IDictionary<string, string>? ConfigOverrides);
