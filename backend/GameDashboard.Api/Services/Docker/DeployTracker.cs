using System.Collections.Concurrent;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// In-memory registry of deploys whose container doesn't exist yet
/// (docs/docker-migration.md → Deploy is asynchronous): `docker create` needs
/// the image locally and first-time pulls take minutes, so DeployServerAsync
/// registers the server here and returns immediately while a background task
/// does pull → volume → create → start. List/Get merge these entries so the
/// server is visible (Pending) the moment deploy returns; a failed background
/// deploy flips the entry to Error with a message until the user deletes or
/// redeploys it. Entries do not survive a backend restart — the server simply
/// never appears and can be redeployed.
/// </summary>
public sealed class DeployTracker
{
    public sealed record PendingDeploy(
        string Name,
        string CatalogImageTag,
        string Image,
        IReadOnlyList<PortMapping> Ports,
        ResourceSpec Resources,
        IReadOnlyDictionary<string, string> Config,
        DateTime CreatedAt,
        bool Failed = false,
        string? Error = null);

    private readonly ConcurrentDictionary<string, PendingDeploy> _deploys =
        new(StringComparer.Ordinal);

    public IReadOnlyCollection<PendingDeploy> All => _deploys.Values.ToList();

    public PendingDeploy? Get(string name) =>
        _deploys.TryGetValue(name, out var deploy) ? deploy : null;

    /// <summary>
    /// Registers a new in-flight deploy. Returns false when one is already in
    /// flight (still Pending) for this name; a previously *failed* entry is
    /// replaced so the user can retry without deleting first.
    /// </summary>
    public bool TryRegister(PendingDeploy deploy)
    {
        while (true)
        {
            if (_deploys.TryAdd(deploy.Name, deploy))
            {
                return true;
            }

            if (!_deploys.TryGetValue(deploy.Name, out var existing))
            {
                continue; // raced with a removal; retry the add
            }

            if (!existing.Failed)
            {
                return false;
            }

            if (_deploys.TryUpdate(deploy.Name, deploy, existing))
            {
                return true;
            }
        }
    }

    public void MarkFailed(string name, string error)
    {
        if (_deploys.TryGetValue(name, out var existing))
        {
            _deploys.TryUpdate(name, existing with { Failed = true, Error = error }, existing);
        }
    }

    public void Remove(string name) => _deploys.TryRemove(name, out _);

    public ServerSummary ToSummary(PendingDeploy deploy) =>
        new(
            Name: deploy.Name,
            Game: deploy.Name,
            Image: deploy.Image,
            Status: deploy.Failed ? ServerStatus.Error : ServerStatus.Pending,
            Replicas: 1,
            CreatedAt: deploy.CreatedAt);

    public ServerDetail ToDetail(PendingDeploy deploy) =>
        new(
            Name: deploy.Name,
            Game: deploy.Name,
            Image: deploy.Image,
            Status: deploy.Failed ? ServerStatus.Error : ServerStatus.Pending,
            Replicas: 1,
            CreatedAt: deploy.CreatedAt,
            Ports: deploy.Ports,
            Resources: deploy.Resources,
            Players: null);
}
