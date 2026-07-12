namespace GameDashboard.Api.Models;

/// <summary>
/// The host machine's shareable addresses, used by the frontend to render copyable
/// "ip:port" join addresses next to each game server's node ports. LanAddresses are
/// the LAN IPv4s in preference order (empty when no LAN adapter is up).
/// PublicAddress is the internet-facing IPv4 — only reachable by players after the
/// user forwards the node port on their router — or null when the lookup fails.
/// NodePortRangeStart/End is the dashboard's whole allocation window: forwarding
/// that range once covers every current and future server (see
/// DeploymentBuilderService.NodePortRangeStart).
/// </summary>
public sealed record NetworkInfo(
    IReadOnlyList<string> LanAddresses,
    string? PublicAddress,
    int NodePortRangeStart,
    int NodePortRangeEnd);
