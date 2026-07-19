namespace GameDashboard.Api.Models;

/// <summary>
/// Version list for one Minecraft server-software type, newest first.
/// Served by GET /api/minecraft/versions (docs/minecraft-server-types.md).
/// </summary>
public record MinecraftVersionsResponse(string? Latest, IReadOnlyList<string> Versions);

/// <summary>A trimmed Modrinth search hit — just what the deploy dialog renders.</summary>
public record ModrinthProjectHit(
    string Slug,
    string Title,
    string Description,
    string? IconUrl,
    long Downloads,
    string ProjectType,
    IReadOnlyList<string> Loaders);

public record ModrinthSearchResponse(IReadOnlyList<ModrinthProjectHit> Hits);
