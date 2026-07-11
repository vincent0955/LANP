using GameDashboard.Api.Models;
using k8s.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Single source of truth for deriving a <see cref="ServerStatus"/> from
/// deployment replicas + pod state, shared by KubernetesService (REST reads) and
/// PodWatchService (watch-driven events) so both always agree.
///
/// Rules (Req 2.3, design.md — updated for readiness probes):
///   replicas == 0                                     → Stopped
///   no pod yet                                        → Pending
///   pod phase Pending, container failing               → Error
///   pod phase Pending otherwise                        → Pending
///   pod phase Running, all containers ready            → Running
///   pod phase Running, not ready, container failing    → Error
///   pod phase Running, not ready otherwise             → Pending
///     (readiness probe not passing yet — e.g. the game is still being
///      downloaded by steamcmd on first deploy)
///   pod phase Failed                                   → Error
///   anything else                                      → Unknown
/// </summary>
public static class ServerStatusMapper
{
    /// <summary>
    /// Waiting reasons that mean a container is genuinely broken, as opposed to
    /// merely still starting up. Distinguishes "probe hasn't passed yet" (Pending)
    /// from crash loops and unpullable images (Error).
    /// </summary>
    private static readonly string[] FailureWaitingReasons =
    {
        "CrashLoopBackOff",
        "ImagePullBackOff",
        "ErrImagePull",
        "InvalidImageName",
        "CreateContainerConfigError",
        "CreateContainerError",
        "RunContainerError",
    };

    public static ServerStatus Map(int replicas, V1Pod? pod)
    {
        if (replicas == 0)
        {
            return ServerStatus.Stopped;
        }

        if (pod is null)
        {
            return ServerStatus.Pending;
        }

        var phase = pod.Status?.Phase;
        return phase switch
        {
            "Running" when AllContainersReady(pod) => ServerStatus.Running,
            "Running" => HasFailingContainer(pod) ? ServerStatus.Error : ServerStatus.Pending,
            "Pending" => HasFailingContainer(pod) ? ServerStatus.Error : ServerStatus.Pending,
            "Failed" => ServerStatus.Error,
            _ => ServerStatus.Unknown
        };
    }

    private static bool AllContainersReady(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        return statuses is { Count: > 0 } && statuses.All(c => c.Ready);
    }

    private static bool HasFailingContainer(V1Pod pod)
    {
        var statuses = pod.Status?.ContainerStatuses;
        if (statuses is null)
        {
            return false;
        }

        return statuses.Any(c =>
            c.State?.Waiting?.Reason is { } reason &&
            FailureWaitingReasons.Contains(reason, StringComparer.Ordinal));
    }
}
