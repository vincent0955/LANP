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
    // "created" with no deploy in flight is a container that was created but
    // deliberately never started (a required secret is unset, or a config edit
    // recreated a stopped server) — it reads Stopped, not a forever-Pending.
    [InlineData("created", false, ServerStatus.Stopped)]
    [InlineData("created", true, ServerStatus.Stopped)]
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
    // While a deploy is in flight, the brief window between `docker create` and
    // `docker start` still reads Pending so the card never flashes Stopped.
    [InlineData("created", ServerStatus.Pending)]
    [InlineData("running", ServerStatus.Running)] // running always wins once ready
    public void Created_During_A_Deploy_Stays_Pending(string? state, ServerStatus expected)
    {
        Assert.Equal(expected, ContainerStatusMapper.Map(state, tcpReady: true, deployInFlight: true));
    }

    [Theory]
    [InlineData("running", 1)]
    [InlineData("restarting", 1)]
    [InlineData("paused", 1)]
    [InlineData("created", 0)] // never started ⇒ Stopped; a Stop button here is a no-op
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
