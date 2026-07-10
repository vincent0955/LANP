using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Pure logic: turns a <see cref="GameTemplate"/> + <see cref="DeployServerRequest"/>
/// into the set of Kubernetes manifest objects needed to deploy a server. No I/O —
/// deliberately kept free of any Kubernetes client dependency so it can be fully
/// unit tested without a cluster.
///
/// See design.md → DeploymentBuilderService; requirements.md → Req 3, Req 8, Req 14.
/// </summary>
public interface IDeploymentBuilder
{
    /// <summary>
    /// Builds the Deployment/Service/PVC/ConfigMap for a new server.
    /// </summary>
    /// <param name="template">The game template supplying defaults.</param>
    /// <param name="request">The deploy request (name, overrides).</param>
    /// <param name="usedNodePorts">
    /// NodePorts already claimed by other Services in the cluster; the builder must
    /// assign new, non-conflicting NodePorts from outside this set.
    /// </param>
    /// <param name="namespaceName">Kubernetes namespace all objects are created in.</param>
    /// <param name="secretName">Name of the Secret sensitive config keys are sourced from.</param>
    ServerManifestSet Build(
        GameTemplate template,
        DeployServerRequest request,
        IReadOnlySet<int> usedNodePorts,
        string namespaceName,
        string secretName);
}
