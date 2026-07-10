using GameDashboard.Api.Services;

namespace GameDashboard.Tests.Services;

public class AutoScaleDecisionEngineTests
{
    private const double OneGb = 1024.0 * 1024 * 1024;

    private static ServerScaleCandidate Candidate(string name, bool hasPlayers, DateTimeOffset lastActive) =>
        new(name, hasPlayers, lastActive);

    // --- metrics unavailable: never act ---

    [Fact]
    public void Decide_Never_Scales_Down_When_Metrics_Unavailable()
    {
        var candidates = new[]
        {
            Candidate("empty-server", hasPlayers: false, DateTimeOffset.UtcNow.AddHours(-5))
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: false,
            memoryUsedBytes: 15 * OneGb,   // would be "over" if this were trusted
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Empty(decision.ServersToScaleDown);
        Assert.Contains("unavailable", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Never_Scales_Down_When_Capacity_Is_Zero_Or_Unknown()
    {
        var candidates = new[] { Candidate("empty-server", false, DateTimeOffset.UtcNow) };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 10 * OneGb,
            memoryCapacityBytes: 0,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Empty(decision.ServersToScaleDown);
    }

    // --- below threshold: no-op ---

    [Fact]
    public void Decide_Does_Nothing_When_Usage_Below_Threshold()
    {
        var candidates = new[] { Candidate("empty-server", false, DateTimeOffset.UtcNow) };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 8 * OneGb,     // 50%
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Empty(decision.ServersToScaleDown);
        Assert.Contains("below", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Acts_When_Usage_Exactly_At_Threshold()
    {
        // The no-op guard is a strict "usedPercent < threshold", so usage exactly
        // at the threshold is treated as "at or over" and does trigger action —
        // this is intentionally proactive rather than waiting to go strictly over.
        var candidates = new[] { Candidate("empty-server", false, DateTimeOffset.UtcNow) };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 12.8 * OneGb,  // exactly 80% of 16Gi
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Equal(new[] { "empty-server" }, decision.ServersToScaleDown);
    }

    // --- over threshold: never touch servers with players ---

    [Fact]
    public void Decide_Never_Scales_Down_A_Server_With_Active_Players()
    {
        var candidates = new[]
        {
            Candidate("busy-server", hasPlayers: true, DateTimeOffset.UtcNow.AddHours(-10))
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Empty(decision.ServersToScaleDown);
        Assert.Contains("no empty servers", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_Skips_Servers_With_Players_Even_When_They_Are_Least_Recently_Active()
    {
        var candidates = new[]
        {
            Candidate("busy-but-stale", hasPlayers: true, DateTimeOffset.UtcNow.AddDays(-30)),
            Candidate("empty-but-recent", hasPlayers: false, DateTimeOffset.UtcNow.AddMinutes(-5))
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Equal(new[] { "empty-but-recent" }, decision.ServersToScaleDown);
    }

    // --- over threshold: least-recently-active first ---

    [Fact]
    public void Decide_Scales_Down_Least_Recently_Active_Empty_Server_First()
    {
        var candidates = new[]
        {
            Candidate("recent", false, DateTimeOffset.UtcNow.AddMinutes(-10)),
            Candidate("stale", false, DateTimeOffset.UtcNow.AddHours(-6)),
            Candidate("mid", false, DateTimeOffset.UtcNow.AddHours(-1))
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        // Without per-server release estimates, the engine scales down one at a
        // time until it can't tell it's helped (release=0 means it never sees
        // improvement), so it will proceed through all candidates in order.
        Assert.Equal("stale", decision.ServersToScaleDown[0]);
    }

    [Fact]
    public void Decide_Stops_Once_Projected_Usage_Drops_Below_Threshold()
    {
        var candidates = new[]
        {
            Candidate("stale", false, DateTimeOffset.UtcNow.AddHours(-6)),
            Candidate("mid", false, DateTimeOffset.UtcNow.AddHours(-1)),
            Candidate("recent", false, DateTimeOffset.UtcNow.AddMinutes(-10))
        };

        var releases = new Dictionary<string, double>
        {
            ["stale"] = 4 * OneGb // scaling down "stale" alone drops usage from 94% to ~69%
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates,
            estimatedMemoryReleaseBytesPerServer: releases);

        Assert.Equal(new[] { "stale" }, decision.ServersToScaleDown);
    }

    [Fact]
    public void Decide_Continues_To_Second_Candidate_If_First_Is_Not_Enough()
    {
        var candidates = new[]
        {
            Candidate("stale", false, DateTimeOffset.UtcNow.AddHours(-6)),
            Candidate("mid", false, DateTimeOffset.UtcNow.AddHours(-1))
        };

        var releases = new Dictionary<string, double>
        {
            ["stale"] = 0.5 * OneGb, // not enough alone
            ["mid"] = 4 * OneGb
        };

        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates,
            estimatedMemoryReleaseBytesPerServer: releases);

        Assert.Equal(new[] { "stale", "mid" }, decision.ServersToScaleDown);
    }

    [Fact]
    public void Decide_Exhausts_All_Empty_Candidates_If_Never_Enough()
    {
        var candidates = new[]
        {
            Candidate("a", false, DateTimeOffset.UtcNow.AddHours(-3)),
            Candidate("b", false, DateTimeOffset.UtcNow.AddHours(-2)),
            Candidate("c", false, DateTimeOffset.UtcNow.AddHours(-1))
        };

        // No release estimates provided => projected usage never improves => engine
        // scales down every empty candidate as it keeps failing to drop below threshold.
        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: candidates);

        Assert.Equal(3, decision.ServersToScaleDown.Count);
    }

    [Fact]
    public void Decide_Empty_Server_List_Returns_No_Action()
    {
        var decision = AutoScaleDecisionEngine.Decide(
            metricsAvailable: true,
            memoryUsedBytes: 15 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            memoryHighWaterPercent: 80,
            runningServers: Array.Empty<ServerScaleCandidate>());

        Assert.Empty(decision.ServersToScaleDown);
    }

    // --- capacity check ---

    [Fact]
    public void CheckCapacity_Returns_Unknown_When_Metrics_Unavailable()
    {
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: false,
            currentMemoryUsedBytes: 10 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            additionalMemoryRequestBytes: 4 * OneGb);

        Assert.Equal(AutoScaleDecisionEngine.CapacityCheckResult.Unknown, result);
    }

    [Fact]
    public void CheckCapacity_Returns_Unknown_When_Capacity_Is_Zero()
    {
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: true,
            currentMemoryUsedBytes: 10 * OneGb,
            memoryCapacityBytes: 0,
            additionalMemoryRequestBytes: 4 * OneGb);

        Assert.Equal(AutoScaleDecisionEngine.CapacityCheckResult.Unknown, result);
    }

    [Fact]
    public void CheckCapacity_Returns_Ok_When_Projected_Usage_Fits()
    {
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: true,
            currentMemoryUsedBytes: 8 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            additionalMemoryRequestBytes: 4 * OneGb); // 12Gi projected, fits in 16Gi

        Assert.Equal(AutoScaleDecisionEngine.CapacityCheckResult.Ok, result);
    }

    [Fact]
    public void CheckCapacity_Returns_Warn_When_Projected_Usage_Exceeds_Capacity()
    {
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: true,
            currentMemoryUsedBytes: 14 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            additionalMemoryRequestBytes: 4 * OneGb); // 18Gi projected, exceeds 16Gi

        Assert.Equal(AutoScaleDecisionEngine.CapacityCheckResult.Warn, result);
    }

    [Fact]
    public void CheckCapacity_Returns_Ok_When_Projected_Usage_Exactly_Equals_Capacity()
    {
        var result = AutoScaleDecisionEngine.CheckCapacity(
            metricsAvailable: true,
            currentMemoryUsedBytes: 12 * OneGb,
            memoryCapacityBytes: 16 * OneGb,
            additionalMemoryRequestBytes: 4 * OneGb); // exactly 16Gi

        Assert.Equal(AutoScaleDecisionEngine.CapacityCheckResult.Ok, result);
    }
}
