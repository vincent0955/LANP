namespace GameDashboard.Api.Models;

/// <summary>Outcome of an outside-in reachability probe for one port (WS2).</summary>
public enum ReachabilityState
{
    /// <summary>An external node reached the port — players on the internet can connect.</summary>
    Open,

    /// <summary>External nodes could not reach the port — usually a missing router/firewall rule.</summary>
    Closed,

    /// <summary>Could not be determined from outside (probe disabled/unreachable, or a UDP port).</summary>
    Unverified,
}

/// <summary>
/// Reachability of one published port (WS2). <see cref="LocallyListening"/> is a
/// reliable local signal (the port is published and answering on this machine);
/// <see cref="External"/> is the best-effort outside-in result.
/// </summary>
public sealed record PortReachability(
    string Name,
    string Protocol,
    int Port,
    bool LocallyListening,
    ReachabilityState External,
    string? Detail);

/// <summary>
/// A server's connectivity diagnosis (WS2): whether the host is behind CGNAT
/// (which makes port forwarding impossible) and the per-port reachability.
/// </summary>
public sealed record ServerReachability(
    string ServerName,
    string? PublicAddress,
    bool CgnatDetected,
    string? CgnatDetail,
    IReadOnlyList<PortReachability> Ports);

/// <summary>
/// Machine-wide diagnosis of the whole forwardable port window, for users who
/// forward the entire range once instead of server by server. Only a sample of
/// the window is probed (NetworkDiagnosticsService): a router range rule either
/// covers every port in it or none, so the sample settles it.
/// </summary>
public sealed record RangeReachability(
    string? PublicAddress,
    bool CgnatDetected,
    string? CgnatDetail,
    int RangeStart,
    int RangeEnd,
    IReadOnlyList<PortReachability> Ports);

/// <summary>One row of the forwarding table: the same host port, TCP and/or UDP.</summary>
public sealed record ForwardingRule(string Name, string Protocol, int Port);

/// <summary>
/// Exact, per-server instructions for letting friends join (WS2), generated from
/// the server's actually-allocated host ports — replaces the stale hand-written
/// portforwarding.md. FirewallCommands are copyable admin PowerShell; router
/// rules forward each external port to the same internal port on the LAN IP.
/// </summary>
public sealed record ForwardingGuide(
    string ServerName,
    IReadOnlyList<string> LanAddresses,
    string? PublicAddress,
    IReadOnlyList<ForwardingRule> Rules,
    IReadOnlyList<string> FirewallCommands,
    string? Note);
