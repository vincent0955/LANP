namespace GameDashboard.Api.Models;

/// <summary>
/// Lightweight view of a deployed server, used for list endpoints.
/// See requirements.md → Req 2.
/// </summary>
public record ServerSummary(
    string Name,
    string Game,
    string Image,
    ServerStatus Status,
    int Replicas,
    DateTime CreatedAt);

/// <summary>
/// Full detail view of a single server, used for the get-by-name endpoint.
/// Player info is populated once RCON support lands (Phase 6); null until then.
/// </summary>
public record ServerDetail(
    string Name,
    string Game,
    string Image,
    ServerStatus Status,
    int Replicas,
    DateTime CreatedAt,
    IReadOnlyList<PortMapping> Ports,
    ResourceSpec? Resources,
    PlayerInfo? Players);

/// <summary>
/// Player/map info reported via RCON. Placeholder record until Phase 6 (RconService).
/// </summary>
public record PlayerInfo(int CurrentPlayers, int MaxPlayers, string? CurrentMap);
