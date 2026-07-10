namespace GameDashboard.Api.Services;

/// <summary>
/// A single running server's state as input to the auto-scale decision.
/// See requirements.md → Req 12.2-12.3.
/// </summary>
public record ServerScaleCandidate(
    string Name,
    bool HasActivePlayers,
    DateTimeOffset LastActiveAt);

/// <summary>
/// The outcome of one auto-scale evaluation: which servers (if any) to scale down,
/// and why. Pure data — no side effects.
/// </summary>
public record AutoScaleDecision(
    IReadOnlyList<string> ServersToScaleDown,
    string Reason);

/// <summary>
/// Pure decision logic for automatic resource-based scaling. No Kubernetes client,
/// no RCON, no metrics client — takes already-gathered inputs and decides what (if
/// anything) should be scaled down. This separation mirrors DeploymentBuilderService
/// (Phase 3): the risky/novel logic is isolated and fully unit-testable without a
/// live cluster.
///
/// See design.md → AutoScaleService; requirements.md → Req 12.
/// </summary>
public static class AutoScaleDecisionEngine
{
    /// <summary>
    /// Result of a pre-deploy/start capacity check (Req 12.4).
    /// </summary>
    public enum CapacityCheckResult
    {
        /// <summary>Projected usage fits comfortably within capacity.</summary>
        Ok,

        /// <summary>Projected usage would exceed capacity, but the caller may proceed anyway.</summary>
        Warn,

        /// <summary>Metrics/capacity are unknown; the check could not be evaluated.</summary>
        Unknown
    }

    /// <summary>
    /// Checks whether starting/deploying a server with the given memory request
    /// would overcommit the node, given current usage and capacity.
    ///
    /// Returns <see cref="CapacityCheckResult.Unknown"/> rather than blocking when
    /// metrics are unavailable — with no usage data, there's nothing meaningful to
    /// reject or warn about, so deploys are not blocked just because metrics-server
    /// happens to be absent (a common, legitimate state on a local cluster).
    /// </summary>
    public static CapacityCheckResult CheckCapacity(
        bool metricsAvailable,
        double currentMemoryUsedBytes,
        double memoryCapacityBytes,
        double additionalMemoryRequestBytes)
    {
        if (!metricsAvailable || memoryCapacityBytes <= 0)
        {
            return CapacityCheckResult.Unknown;
        }

        var projected = currentMemoryUsedBytes + additionalMemoryRequestBytes;
        return projected > memoryCapacityBytes ? CapacityCheckResult.Warn : CapacityCheckResult.Ok;
    }

    /// <summary>
    /// Decides which running, player-less servers (if any) should be scaled down.
    ///
    /// Rules (Req 12.2, 12.3):
    ///  - If metrics are unavailable, never scale down (no data to justify acting on).
    ///  - If memory usage is at or below the high-water threshold, do nothing.
    ///  - Otherwise, scale down empty servers one at a time, least-recently-active
    ///    first, stopping as soon as usage would drop back to/under the threshold
    ///    or there are no more empty servers to scale down.
    ///  - A server with active players is never a candidate, regardless of pressure.
    /// </summary>
    public static AutoScaleDecision Decide(
        bool metricsAvailable,
        double memoryUsedBytes,
        double memoryCapacityBytes,
        double memoryHighWaterPercent,
        IReadOnlyList<ServerScaleCandidate> runningServers,
        IReadOnlyDictionary<string, double>? estimatedMemoryReleaseBytesPerServer = null)
    {
        if (!metricsAvailable)
        {
            return new AutoScaleDecision(
                Array.Empty<string>(),
                "Metrics are unavailable; auto-scale will not act without usage data.");
        }

        if (memoryCapacityBytes <= 0)
        {
            return new AutoScaleDecision(Array.Empty<string>(), "Node memory capacity is unknown.");
        }

        var usedPercent = memoryUsedBytes / memoryCapacityBytes * 100.0;
        if (usedPercent < memoryHighWaterPercent)
        {
            return new AutoScaleDecision(
                Array.Empty<string>(),
                $"Memory usage ({usedPercent:F1}%) is below the high-water threshold ({memoryHighWaterPercent:F1}%).");
        }

        var candidates = runningServers
            .Where(s => !s.HasActivePlayers)
            .OrderBy(s => s.LastActiveAt)
            .ToList();

        if (candidates.Count == 0)
        {
            return new AutoScaleDecision(
                Array.Empty<string>(),
                $"Memory usage ({usedPercent:F1}%) exceeds the high-water threshold " +
                $"({memoryHighWaterPercent:F1}%), but no empty servers are available to scale down.");
        }

        // Scale down empty servers, least-recently-active first, until projected
        // usage falls back under the threshold or candidates are exhausted.
        var toScaleDown = new List<string>();
        var projectedUsed = memoryUsedBytes;

        foreach (var candidate in candidates)
        {
            toScaleDown.Add(candidate.Name);

            var release = estimatedMemoryReleaseBytesPerServer?.GetValueOrDefault(candidate.Name) ?? 0;
            projectedUsed = Math.Max(0, projectedUsed - release);

            var projectedPercent = projectedUsed / memoryCapacityBytes * 100.0;
            if (projectedPercent < memoryHighWaterPercent)
            {
                break;
            }
        }

        return new AutoScaleDecision(
            toScaleDown,
            $"Memory usage ({usedPercent:F1}%) exceeded the high-water threshold " +
            $"({memoryHighWaterPercent:F1}%); scaling down {toScaleDown.Count} empty server(s), least-recently-active first.");
    }
}
