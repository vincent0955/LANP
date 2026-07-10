using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IGameCatalogService"/>. Wraps <see cref="CuratedGameTemplates.All"/>.
/// </summary>
public sealed class GameCatalogService : IGameCatalogService
{
    public IReadOnlyList<GameTemplate> GetGames(string? search)
    {
        var all = CuratedGameTemplates.All.Values;

        if (string.IsNullOrWhiteSpace(search))
        {
            return all.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return all
            .Where(t => t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameTemplate? GetGameByTag(string tag) =>
        CuratedGameTemplates.All.TryGetValue(tag, out var template) ? template : null;
}
