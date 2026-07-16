using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Read-only metadata for the Minecraft deploy experience: version lists per
/// server-software type and Modrinth content search. Every upstream source is
/// keyless (zero-setup constraint — see docs/minecraft-server-types.md); results
/// are cached in-memory so the deploy dialog never hammers upstream APIs.
/// Upstream failures throw <see cref="MinecraftMetadataUnavailableException"/> —
/// the frontend degrades gracefully (free-text version field, paste-a-slug).
/// </summary>
public interface IMinecraftMetadataService
{
    /// <param name="type">vanilla | paper | purpur | fabric | quilt | forge | neoforge</param>
    Task<MinecraftVersionsResponse> GetVersionsAsync(string type, CancellationToken ct);

    /// <param name="kind">mod | plugin | modpack</param>
    Task<ModrinthSearchResponse> SearchContentAsync(
        string? query, string kind, string? loader, string? mcVersion, CancellationToken ct);
}

/// <summary>Upstream metadata source unreachable or returned garbage; maps to 503.</summary>
public sealed class MinecraftMetadataUnavailableException : Exception
{
    public MinecraftMetadataUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
