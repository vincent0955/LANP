using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using k8s.Models;

namespace GameDashboard.Tests.RealTime;

/// <summary>
/// Covers ServerStatusMapper — the single status-derivation implementation shared
/// by KubernetesService (REST reads) and PodWatchService (watch-driven events),
/// so the two can no longer drift apart. The watch connection itself and
/// backoff/reconnect loop require a live cluster and are exercised in the live
/// verification pass.
///
/// Key rule since readiness probes were added to generated deployments:
/// phase Running with containers not-ready means "starting up" (Pending) — on a
/// first deploy that is the in-container game download — unless a container is in
/// a recognized failure state, which is a real Error.
/// </summary>
public class PodWatchServiceStatusMappingTests
{
    [Theory]
    [InlineData(0, "Running", true, ServerStatus.Stopped)]
    [InlineData(1, "Running", true, ServerStatus.Running)]
    [InlineData(1, "Running", false, ServerStatus.Pending)] // probe not passing yet
    [InlineData(1, "Pending", true, ServerStatus.Pending)]
    [InlineData(1, "Pending", false, ServerStatus.Pending)]
    [InlineData(1, "Failed", true, ServerStatus.Error)]
    public void Maps_Replicas_Phase_And_Readiness(
        int replicas, string phase, bool containersReady, ServerStatus expected)
    {
        var pod = PodWith(phase, containersReady);

        Assert.Equal(expected, ServerStatusMapper.Map(replicas, pod));
    }

    [Theory]
    [InlineData("CrashLoopBackOff")]
    [InlineData("ImagePullBackOff")]
    [InlineData("ErrImagePull")]
    [InlineData("InvalidImageName")]
    [InlineData("CreateContainerConfigError")]
    public void Failing_Container_Maps_To_Error_Even_While_Phase_Says_Running_Or_Pending(string reason)
    {
        foreach (var phase in new[] { "Running", "Pending" })
        {
            var pod = PodWith(phase, containersReady: false, waitingReason: reason);

            Assert.Equal(ServerStatus.Error, ServerStatusMapper.Map(1, pod));
        }
    }

    [Theory]
    [InlineData("ContainerCreating")]
    [InlineData("PodInitializing")]
    public void Benign_Waiting_Reasons_Stay_Pending(string reason)
    {
        var pod = PodWith("Pending", containersReady: false, waitingReason: reason);

        Assert.Equal(ServerStatus.Pending, ServerStatusMapper.Map(1, pod));
    }

    [Fact]
    public void No_Pod_Yet_Is_Pending()
    {
        Assert.Equal(ServerStatus.Pending, ServerStatusMapper.Map(1, null));
    }

    [Fact]
    public void Pod_Without_Status_Is_Unknown()
    {
        Assert.Equal(ServerStatus.Unknown, ServerStatusMapper.Map(1, new V1Pod { Status = null }));
    }

    private static V1Pod PodWith(string phase, bool containersReady, string? waitingReason = null)
    {
        return new V1Pod
        {
            Status = new V1PodStatus
            {
                Phase = phase,
                ContainerStatuses = new List<V1ContainerStatus>
                {
                    new()
                    {
                        Ready = containersReady,
                        State = waitingReason is null
                            ? null
                            : new V1ContainerState
                            {
                                Waiting = new V1ContainerStateWaiting { Reason = waitingReason }
                            }
                    }
                }
            }
        };
    }
}
