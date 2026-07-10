using GameDashboard.Api.Models;
using GameDashboard.Api.RealTime;
using k8s.Models;

namespace GameDashboard.Tests.RealTime;

/// <summary>
/// Covers PodWatchService's status-derivation rule (mirrors KubernetesService's
/// MapStatus so watch-driven events and REST reads agree). The watch connection
/// itself and backoff/reconnect loop require a live cluster and are exercised in
/// the live verification pass.
/// </summary>
public class PodWatchServiceStatusMappingTests
{
    [Theory]
    [InlineData(0, "Running", true, ServerStatus.Stopped)]
    [InlineData(1, "Running", true, ServerStatus.Running)]
    [InlineData(1, "Running", false, ServerStatus.Error)]
    [InlineData(1, "Pending", true, ServerStatus.Pending)]
    [InlineData(1, "Failed", true, ServerStatus.Error)]
    public void MapStatus_Matches_KubernetesService_Rules(
        int replicas, string phase, bool containersReady, ServerStatus expected)
    {
        var pod = new V1Pod
        {
            Status = new V1PodStatus
            {
                Phase = phase,
                ContainerStatuses = new List<V1ContainerStatus> { new() { Ready = containersReady } }
            }
        };

        var result = InvokeMapStatus(replicas, pod);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MapStatus_Returns_Unknown_When_Pod_Has_No_Status()
    {
        var pod = new V1Pod { Status = null };

        var result = InvokeMapStatus(1, pod);

        Assert.Equal(ServerStatus.Unknown, result);
    }

    private static ServerStatus InvokeMapStatus(int replicas, V1Pod pod)
    {
        var method = typeof(PodWatchService).GetMethod(
            "MapStatus",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (ServerStatus)method.Invoke(null, new object[] { replicas, pod })!;
    }
}
