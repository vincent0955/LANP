namespace GameDashboard.Api.GameTemplates;

/// <summary>
/// Maps a Minecraft Java deploy's effective config to the itzg/minecraft-server
/// image tag whose bundled JVM can actually run that Minecraft version — old
/// versions crash on new JVMs, and new versions refuse to start on old JVMs
/// (UnsupportedClassVersionError). See docs/minecraft-server-types.md ("Java
/// version matrix"). Applied by ContainerSpecBuilder for TemplateKind.MinecraftJava.
/// </summary>
public static class MinecraftJavaImage
{
    public const string Java8 = "itzg/minecraft-server:java8";
    public const string Java17 = "itzg/minecraft-server:java17";
    public const string Java21 = "itzg/minecraft-server:java21";
    public const string Java25 = "itzg/minecraft-server:java25";

    /// <summary>
    /// Resolves the image for the merged (defaults + overrides) config of a deploy.
    /// LATEST and anything unparseable (snapshots) get the newest JVM — LATEST
    /// resolves to current Minecraft, which requires it (26.x is compiled for
    /// Java 25). A Modrinth modpack pins its own Minecraft version, which the
    /// deploy path looks up on Modrinth and passes as
    /// <paramref name="modpackMinecraftVersion"/>; when that lookup failed
    /// (offline, URL/file pack) the pack runs java21 — hit live 2026-07-18:
    /// a 26.x-era Fabric pack refused to start on java21
    /// (UnsupportedClassVersionError), so guessing is no longer safe.
    /// </summary>
    public static string Resolve(
        IReadOnlyDictionary<string, string> config, string? modpackMinecraftVersion = null)
    {
        if (config.TryGetValue("MODRINTH_MODPACK", out var pack) && !string.IsNullOrWhiteSpace(pack))
        {
            return modpackMinecraftVersion is null
                ? Java21
                : ForVersion(modpackMinecraftVersion);
        }

        return config.TryGetValue("VERSION", out var version) ? ForVersion(version) : Java25;
    }

    /// <summary>The version→JVM matrix for one concrete Minecraft version string.</summary>
    public static string ForVersion(string version)
    {
        // Year-based scheme ("26.2"), used since Minecraft dropped 1.x
        // numbering: current-era versions all need the newest JVM.
        var parts = version.Trim().Split('.');
        if (parts.Length >= 2 && parts[0] != "1" && int.TryParse(parts[0], out var year) && year >= 25)
        {
            return Java25;
        }

        if (!TryParseLegacy(version, out var minor, out var patch))
        {
            return Java25; // LATEST, snapshots ("24w14a"), blank — run current Minecraft's JVM
        }

        return minor switch
        {
            <= 16 => Java8,
            <= 19 => Java17,
            20 when patch <= 4 => Java17,
            20 or 21 => Java21,
            // Hypothetical 1.x releases beyond 1.21 would postdate Java 21;
            // the newest JVM is the safe default for anything that new.
            _ => Java25
        };
    }

    /// <summary>Parses legacy release versions of the form "1.X" or "1.X.Y".</summary>
    private static bool TryParseLegacy(string version, out int minor, out int patch)
    {
        minor = 0;
        patch = 0;

        var parts = version.Trim().Split('.');
        if (parts.Length is < 2 or > 3 || parts[0] != "1" || !int.TryParse(parts[1], out minor))
        {
            return false;
        }

        if (parts.Length == 3 && !int.TryParse(parts[2], out patch))
        {
            return false;
        }

        return true;
    }
}
