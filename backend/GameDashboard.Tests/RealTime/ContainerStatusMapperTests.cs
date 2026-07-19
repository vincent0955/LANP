using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Tests.RealTime;

/// <summary>
/// Covers ContainerStatusMapper — the single status-derivation implementation
/// shared by DockerService (REST reads) and ContainerWatchService (push
/// events), so the two can never drift apart. Replaces the k8s-era
/// ServerStatusMapper tests; see docs/docker-migration.md → Status derivation.
///
/// Key rule carried over from the readiness-probe era: state "running" with the
/// backend's TCP probe not passing means "starting up" (Pending) — on a first
/// deploy that is the in-container game download — never Running. Crash loops
/// surface as "restarting" (the restart policy is unless-stopped, so Docker
/// only restarts a container that died on its own), which maps to Error.
/// </summary>
public class ContainerStatusMapperTests
{
    [Theory]
    [InlineData("created", false, ServerStatus.Pending)]
    [InlineData("created", true, ServerStatus.Pending)]
    [InlineData("removing", false, ServerStatus.Pending)]
    [InlineData("running", true, ServerStatus.Running)]
    [InlineData("running", false, ServerStatus.Pending)] // probe not passing yet
    [InlineData("restarting", false, ServerStatus.Error)] // crash loop
    [InlineData("restarting", true, ServerStatus.Error)]
    [InlineData("exited", false, ServerStatus.Stopped)] // unless-stopped ⇒ user stopped it
    [InlineData("dead", false, ServerStatus.Stopped)]
    [InlineData("paused", false, ServerStatus.Unknown)]
    [InlineData(null, false, ServerStatus.Unknown)]
    [InlineData("garbage", false, ServerStatus.Unknown)]
    public void Maps_Container_State_And_Readiness(string? state, bool tcpReady, ServerStatus expected)
    {
        Assert.Equal(expected, ContainerStatusMapper.Map(state, tcpReady));
    }

    [Theory]
    [InlineData("running", 1)]
    [InlineData("restarting", 1)]
    [InlineData("created", 1)]
    [InlineData("paused", 1)]
    [InlineData("exited", 0)]
    [InlineData("dead", 0)]
    [InlineData("removing", 0)]
    [InlineData(null, 0)]
    public void Replicas_Keep_The_K8s_Era_Wire_Semantics(string? state, int expected)
    {
        // 1 = should be running, 0 = stopped — the frontend's toggle state.
        Assert.Equal(expected, ContainerStatusMapper.Replicas(state));
    }
}
