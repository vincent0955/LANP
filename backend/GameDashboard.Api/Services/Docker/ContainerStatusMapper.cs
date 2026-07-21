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
///   created, deploy in flight             → Pending (the transient window
///                                            between `docker create` and start)
///   created, no deploy in flight          → Stopped (created but deliberately
///                                            not started — e.g. a required
///                                            secret is still unset, or a config
///                                            edit recreated a stopped server;
///                                            it will never start on its own)
///   removing                              → Pending
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
    public static ServerStatus Map(string? state, bool tcpReady, bool deployInFlight = false) => state switch
    {
        "created" => deployInFlight ? ServerStatus.Pending : ServerStatus.Stopped,
        "removing" => ServerStatus.Pending,
        "running" when tcpReady => ServerStatus.Running,
        "running" => ServerStatus.Pending,
        "restarting" => ServerStatus.Error,
        "exited" or "dead" => ServerStatus.Stopped,
        _ => ServerStatus.Unknown,
    };

    /// <summary>
    /// The replicas value reported over the wire (kept from the k8s era:
    /// 1 = should be running, 0 = stopped) — this is the frontend's Start/Stop
    /// toggle state. A "created" container has never started, so it is 0
    /// (Stopped): reporting 1 would show a Stop button whose scale-to-0 is a
    /// no-op on a non-running container, leaving the server stuck.
    /// </summary>
    public static int Replicas(string? state) => state switch
    {
        "running" or "restarting" or "paused" => 1,
        _ => 0,
    };
}
