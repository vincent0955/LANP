using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Queries live player/map info and sends admin commands to a running game server
/// via the RCON protocol. See design.md → RconService; requirements.md → Req 10.
///
/// RCON is inherently unreliable: the port may not be open yet, the password may be
/// wrong, the server may be mid-startup. Every method degrades gracefully rather
/// than throwing into request paths that enrich read-only views (Req 10.3) —
/// <see cref="QueryPlayerInfoAsync"/> returns null on any failure. Only the explicit
/// command endpoint (<see cref="SendCommandAsync"/>) surfaces failures, since a user
/// deliberately invoking a command needs to know if it didn't work.
/// </summary>
public interface IRconService
{
    /// <summary>
    /// Queries current player count, max players, and current map (where the game
    /// exposes it) for a running server. Returns null if the server is not running,
    /// RCON is unreachable, times out, or the response cannot be parsed — never
    /// throws.
    /// </summary>
    Task<PlayerInfo?> QueryPlayerInfoAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// Sends a raw admin command via RCON and returns the server's text response.
    /// Throws <see cref="RconUnavailableException"/> if the connection or
    /// authentication fails.
    /// </summary>
    Task<string> SendCommandAsync(string serverName, string command, CancellationToken ct);
}
