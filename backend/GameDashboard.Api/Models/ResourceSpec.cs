namespace GameDashboard.Api.Models;

/// <summary>
/// CPU/memory requests and limits for a game server container.
/// Values follow Kubernetes quantity strings (e.g. "500m", "2Gi").
/// </summary>
public record ResourceSpec(
    string CpuRequest,
    string CpuLimit,
    string MemoryRequest,
    string MemoryLimit);

/// <summary>
/// A single container port exposed via a NodePort Service, once deployed
/// (NodePort has been assigned).
/// </summary>
public record PortMapping(
    string Name,
    string Protocol,
    int ContainerPort,
    int NodePort);

/// <summary>
/// A port a game template needs exposed, before a NodePort has been assigned.
/// DeploymentBuilderService turns these into <see cref="PortMapping"/>s during Build.
/// </summary>
public record TemplatePort(
    string Name,
    string Protocol,
    int ContainerPort);
