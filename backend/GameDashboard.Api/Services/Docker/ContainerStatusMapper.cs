using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Single source of truth for deriving a <see cref="ServerStatus"/> from a
/// container's state + the backend-side TCP readiness probe, shared by
/// DockerService (REST reads) and ContainerWatchService (push events) so both
/// always agree. Pure logic (docs/docker-migration.md → Status derivation).
///
/// Rules:
///   no container, deploy in flight        → Pending (Error if the deploy failed)
///   created / removing                    → Pending
///   running, TCP probe passing (or no
///     TCP port to probe)                  → Running
///   running, probe not passing yet        → Pending (first-boot download)
///   restarting                            → Error (crash loop — restart policy
///                                            is unless-stopped, so Docker only
///                                            restarts a container that died)
///   exited / dead                         → Stopped (unless-stopped ⇒ exited
///                                            means the user stopped it)
///   paused / anything else                → Unknown
/// </summary>
public static class ContainerStatusMapper
{
    public static ServerStatus Map(string? state, bool tcpReady) => state switch
    {
        "created" or "removing" => ServerStatus.Pending,
        "running" when tcpReady => ServerStatus.Running,
        "running" => ServerStatus.Pending,
        "restarting" => ServerStatus.Error,
        "exited" or "dead" => ServerStatus.Stopped,
        _ => ServerStatus.Unknown,
    };

    /// <summary>
    /// The replicas value reported over the wire (kept from the k8s era:
    /// 1 = should be running, 0 = stopped).
    /// </summary>
    public static int Replicas(string? state) => state switch
    {
        "running" or "restarting" or "created" or "paused" => 1,
        _ => 0,
    };
}
