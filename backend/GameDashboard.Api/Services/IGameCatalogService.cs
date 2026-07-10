using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Serves the curated game catalog. See design.md → GameCatalogService;
/// requirements.md → Req 11.
///
/// Synchronous and dependency-free by design: the catalog is a static, in-code
/// list of ~15 popular games (no remote fetch, no cache/TTL, no network failure
/// mode to handle). Deferred: pulling the full LinuxGSM `serverlist.csv` catalog.
/// </summary>
public interface IGameCatalogService
{
    /// <summary>
    /// Returns catalog entries, optionally filtered by a case-insensitive substring
    /// match against display name.
    /// </summary>
    IReadOnlyList<GameTemplate> GetGames(string? search);

    /// <summary>Looks up a single catalog entry by its image tag.</summary>
    GameTemplate? GetGameByTag(string tag);
}
