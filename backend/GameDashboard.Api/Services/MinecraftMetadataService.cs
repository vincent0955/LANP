using System.Text.Json;
using System.Xml.Linq;
using GameDashboard.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IMinecraftMetadataService"/>. Upstream sources (all keyless):
/// Mojang piston-meta (vanilla/fabric/quilt — loaders track vanilla versions),
/// PaperMC and PurpurMC APIs, the Forge and NeoForge mavens, and Modrinth search.
/// The version dropdown lists Minecraft versions, never loader builds — itzg
/// resolves the loader build itself, which is what keeps the UX AMP-simple.
/// </summary>
public sealed class MinecraftMetadataService : IMinecraftMetadataService
{
    private static readonly TimeSpan VersionsCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;

    public MinecraftMetadataService(HttpClient http, IMemoryCache cache)
    {
        _http = http;
        _cache = cache;
    }

    public async Task<MinecraftVersionsResponse> GetVersionsAsync(string type, CancellationToken ct)
    {
        var normalized = type.Trim().ToLowerInvariant();
        if (normalized is not ("vanilla" or "paper" or "purpur" or "fabric" or "quilt" or "forge" or "neoforge"))
        {
            throw new ArgumentException($"Unknown server software type '{type}'.", nameof(type));
        }

        return await GetCachedAsync($"mc-versions:{normalized}", VersionsCacheTtl, () => FetchVersionsAsync(normalized, ct));
    }

    public async Task<ModrinthSearchResponse> SearchContentAsync(
        string? query, string kind, string? loader, string? mcVersion, CancellationToken ct)
    {
        var normalizedKind = kind.Trim().ToLowerInvariant();
        if (normalizedKind is not ("mod" or "plugin" or "modpack"))
        {
            throw new ArgumentException($"Unknown content kind '{kind}'.", nameof(kind));
        }

        var url = BuildSearchUrl(query, normalizedKind, loader, mcVersion);
        return await GetCachedAsync($"mc-search:{url}", SearchCacheTtl, () => FetchSearchAsync(url, ct));
    }

    public async Task<string?> TryGetModpackMinecraftVersionAsync(string slug, CancellationToken ct)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        // Only bare slugs are resolvable; itzg also accepts URLs and local
        // files for MODRINTH_MODPACK, which we can't look up.
        if (normalized.Length == 0 || normalized.Contains('/'))
        {
            return null;
        }

        try
        {
            return await GetCachedAsync($"mc-modpack-version:{normalized}", VersionsCacheTtl,
                () => FetchModpackMinecraftVersionAsync(normalized, ct));
        }
        catch (MinecraftMetadataUnavailableException)
        {
            return null;
        }
    }

    private async Task<string?> FetchModpackMinecraftVersionAsync(string slug, CancellationToken ct)
    {
        // 404 for an unknown slug surfaces as MinecraftMetadataUnavailable via
        // EnsureSuccessStatusCode — the caller's null fallback covers it.
        using var doc = await GetJsonAsync(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slug)}/version", ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Entries come newest-first. itzg installs the newest release by
        // default, so prefer the first release entry; a pack with no releases
        // at all (only betas) falls back to the newest entry of any type.
        var entries = doc.RootElement.EnumerateArray().ToList();
        var installed = entries.FirstOrDefault(e =>
            e.TryGetProperty("version_type", out var t) && t.GetString() == "release");
        if (installed.ValueKind == JsonValueKind.Undefined)
        {
            installed = entries.FirstOrDefault();
        }

        if (installed.ValueKind != JsonValueKind.Object ||
            !installed.TryGetProperty("game_versions", out var gameVersions) ||
            gameVersions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // A pack version usually pins exactly one MC version; if it lists
        // several, the newest is the one the server actually runs.
        return gameVersions.EnumerateArray()
            .Select(v => v.GetString())
            .Where(v => v is not null)
            .Select(v => (Raw: v!, Parsed: Version.TryParse(v, out var p) ? p : null))
            .Where(v => v.Parsed is not null)
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Raw)
            .FirstOrDefault();
    }

    private async Task<T> GetCachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> fetch)
    {
        if (_cache.TryGetValue(key, out T? cached) && cached is not null)
        {
            return cached;
        }

        var value = await fetch();
        _cache.Set(key, value, ttl);
        return value;
    }

    // --- Version sources ---

    private Task<MinecraftVersionsResponse> FetchVersionsAsync(string type, CancellationToken ct) => type switch
    {
        "vanilla" or "fabric" or "quilt" => FetchMojangVersionsAsync(ct),
        "paper" => FetchPaperVersionsAsync(ct),
        "purpur" => FetchPurpurVersionsAsync(ct),
        "forge" => FetchForgeVersionsAsync(ct),
        _ => FetchNeoForgeVersionsAsync(ct)
    };

    private async Task<MinecraftVersionsResponse> FetchMojangVersionsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", ct);

        var latest = doc.RootElement.GetProperty("latest").GetProperty("release").GetString();
        var releases = doc.RootElement.GetProperty("versions").EnumerateArray()
            .Where(v => v.GetProperty("type").GetString() == "release")
            .Select(v => v.GetProperty("id").GetString()!)
            .ToList();

        return new MinecraftVersionsResponse(latest, releases);
    }

    /// <summary>
    /// Paper's Fill v3 API (the old v2 API returns 410 Gone since 2025): versions
    /// come as an object keyed by version group, newest group first, each holding
    /// [newest..oldest] builds including "-pre"/"-rc" entries we skip.
    /// </summary>
    private async Task<MinecraftVersionsResponse> FetchPaperVersionsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://fill.papermc.io/v3/projects/paper", ct);

        var versions = doc.RootElement.GetProperty("versions").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray())
            .Select(v => v.GetString()!)
            .Where(v => !v.Contains('-'))
            .ToList();

        return new MinecraftVersionsResponse(versions.FirstOrDefault(), versions);
    }

    /// <summary>PurpurMC: { "versions": [oldest..newest] }.</summary>
    private async Task<MinecraftVersionsResponse> FetchPurpurVersionsAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("https://api.purpurmc.org/v2/purpur", ct);

        var versions = doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(v => v.GetString()!)
            .Where(v => !v.Contains('-'))
            .Reverse()
            .ToList();

        return new MinecraftVersionsResponse(versions.FirstOrDefault(), versions);
    }

    private async Task<MinecraftVersionsResponse> FetchForgeVersionsAsync(CancellationToken ct)
    {
        // The old promotions_slim.json endpoint is 404 as of 2026; the standard
        // maven metadata lists every build as "<mcVersion>-<forgeBuild>".
        var xml = await GetStringAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);

        var versions = XDocument.Parse(xml)
            .Descendants("version")
            .Select(v => v.Value.Split('-')[0])
            .Distinct()
            .ToList();

        return SortedDescending(versions);
    }

    private async Task<MinecraftVersionsResponse> FetchNeoForgeVersionsAsync(CancellationToken ct)
    {
        // NeoForge's own build numbers encode the MC version two different ways
        // (classic "21.1.119" ↔ MC 1.21.1; year-based "26.2.x" ↔ MC 26.2), so we
        // generate every plausible mapping and keep only real Mojang releases.
        var mojang = await GetCachedAsync("mc-versions:vanilla", VersionsCacheTtl, () => FetchMojangVersionsAsync(ct));
        var knownReleases = mojang.Versions.ToHashSet(StringComparer.Ordinal);

        var xml = await GetStringAsync(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", ct);

        var versions = XDocument.Parse(xml)
            .Descendants("version")
            .SelectMany(v => MinecraftCandidatesForNeoForge(v.Value))
            .Where(knownReleases.Contains)
            .Distinct()
            .ToList();

        return SortedDescending(versions);
    }

    private static IEnumerable<string> MinecraftCandidatesForNeoForge(string neoVersion)
    {
        var value = neoVersion.Split('-')[0];

        // The very first NeoForge release used legacy "1.20.1-47.x" numbering.
        if (value.StartsWith("1."))
        {
            yield return value;
            yield break;
        }

        var parts = value.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            yield break;
        }

        // Classic scheme: NeoForge X.Y.Z ↔ MC 1.X.Y (Y=0 → MC 1.X).
        yield return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";

        // Year-based MC (late 2025 onward): the build tracks the MC version directly.
        if (major >= 22)
        {
            yield return $"{major}.{minor}";
            if (parts.Length >= 3 && int.TryParse(parts[2], out var patch) && patch > 0)
            {
                yield return $"{major}.{minor}.{patch}";
            }
        }
    }

    private static MinecraftVersionsResponse SortedDescending(List<string> versions)
    {
        var sorted = versions
            .Select(v => (Raw: v, Parsed: Version.TryParse(v, out var p) ? p : null))
            .Where(v => v.Parsed is not null)
            .OrderByDescending(v => v.Parsed)
            .Select(v => v.Raw)
            .ToList();

        return new MinecraftVersionsResponse(sorted.FirstOrDefault(), sorted);
    }

    // --- Modrinth search ---

    private static string BuildSearchUrl(string? query, string kind, string? loader, string? mcVersion)
    {
        var facetGroups = new List<string>();

        switch (kind)
        {
            case "modpack":
                facetGroups.Add("[\"project_type:modpack\"]");
                // No version facet — a pack pins its own MC version — but the
                // browser can narrow to one loader (e.g. Forge packs only).
                if (!string.IsNullOrWhiteSpace(loader))
                {
                    facetGroups.Add($"[\"categories:{loader.ToLowerInvariant()}\"]");
                }
                break;
            case "plugin":
                // Modrinth classifies plugins as mod-type projects with plugin-
                // loader categories; filter to the loaders our servers can run.
                facetGroups.Add("[\"project_type:mod\",\"project_type:plugin\"]");
                facetGroups.Add("[\"categories:paper\",\"categories:purpur\",\"categories:spigot\",\"categories:bukkit\"]");
                if (!string.IsNullOrWhiteSpace(mcVersion))
                {
                    facetGroups.Add($"[\"versions:{mcVersion}\"]");
                }
                break;
            default: // mod
                facetGroups.Add("[\"project_type:mod\"]");
                if (!string.IsNullOrWhiteSpace(loader))
                {
                    facetGroups.Add($"[\"categories:{loader.ToLowerInvariant()}\"]");
                }
                if (!string.IsNullOrWhiteSpace(mcVersion))
                {
                    facetGroups.Add($"[\"versions:{mcVersion}\"]");
                }
                break;
        }

        var facets = $"[{string.Join(",", facetGroups)}]";
        // Popularity is the sane ordering for browsing (empty query); relevance
        // once the user is actually searching.
        var index = string.IsNullOrWhiteSpace(query) ? "downloads" : "relevance";

        return "https://api.modrinth.com/v2/search" +
               $"?query={Uri.EscapeDataString(query ?? "")}" +
               $"&facets={Uri.EscapeDataString(facets)}" +
               $"&index={index}&limit=20";
    }

    // Mod-loader categories as Modrinth spells them; a hit's other categories
    // (themes like "adventure", plugin loaders) aren't loaders the deploy
    // dialog cares about.
    private static readonly string[] ModLoaderCategories = ["forge", "neoforge", "fabric", "quilt"];

    private async Task<ModrinthSearchResponse> FetchSearchAsync(string url, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(url, ct);

        var hits = doc.RootElement.GetProperty("hits").EnumerateArray()
            .Select(h => new ModrinthProjectHit(
                Slug: h.GetProperty("slug").GetString() ?? "",
                Title: h.GetProperty("title").GetString() ?? "",
                Description: h.GetProperty("description").GetString() ?? "",
                IconUrl: h.TryGetProperty("icon_url", out var icon) ? icon.GetString() : null,
                Downloads: h.TryGetProperty("downloads", out var dl) ? dl.GetInt64() : 0,
                ProjectType: h.GetProperty("project_type").GetString() ?? "mod",
                Loaders: ExtractLoaders(h)))
            .Where(h => h.Slug.Length > 0)
            .ToList();

        return new ModrinthSearchResponse(hits);
    }

    private static IReadOnlyList<string> ExtractLoaders(JsonElement hit)
    {
        if (!hit.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return categories.EnumerateArray()
            .Select(c => c.GetString())
            .Where(c => c is not null && ModLoaderCategories.Contains(c))
            .Select(c => c!)
            .ToList();
    }

    // --- HTTP plumbing ---

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var body = await GetStringAsync(url, ct);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MinecraftMetadataUnavailableException($"Unparseable response from {new Uri(url).Host}.", ex);
        }
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MinecraftMetadataUnavailableException($"Could not reach {new Uri(url).Host}.", ex);
        }
    }
}
