using k8s.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// The full set of Kubernetes objects needed to deploy one game server.
/// See design.md → DeploymentBuilderService.
/// </summary>
public record ServerManifestSet(
    V1Deployment Deployment,
    V1Service Service,
    V1PersistentVolumeClaim Pvc,
    V1ConfigMap ConfigMap);
