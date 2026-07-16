namespace GameDashboard.Api.GameTemplates;

/// <summary>
/// Maps a Minecraft Java deploy's effective config to the itzg/minecraft-server
/// image tag whose bundled JVM can actually run that Minecraft version — old
/// versions crash on new JVMs. See docs/minecraft-server-types.md ("Java version
/// matrix"). Applied by DeploymentBuilderService for TemplateKind.MinecraftJava.
/// </summary>
public static class MinecraftJavaImage
{
    public const string Java8 = "itzg/minecraft-server:java8";
    public const string Java17 = "itzg/minecraft-server:java17";
    public const string Java21 = "itzg/minecraft-server:java21";

    /// <summary>
    /// Resolves the image for the merged (defaults + overrides) config of a deploy.
    /// Modpacks and anything unparseable (LATEST, snapshots) get java21 — the same
    /// java21-only limitation for packs the old dedicated modded template had.
    /// </summary>
    public static string Resolve(IReadOnlyDictionary<string, string> config)
    {
        // A Modrinth modpack pins its own Minecraft version; we can't know it
        // here, so run the modern JVM (packs for MC ≤1.20.4 needing an older
        // JVM are not covered — documented limitation).
        if (config.TryGetValue("MODRINTH_MODPACK", out var pack) && !string.IsNullOrWhiteSpace(pack))
        {
            return Java21;
        }

        if (!config.TryGetValue("VERSION", out var version) || !TryParse(version, out var minor, out var patch))
        {
            return Java21;
        }

        return minor switch
        {
            <= 16 => Java8,
            <= 19 => Java17,
            20 when patch <= 4 => Java17,
            _ => Java21
        };
    }

    /// <summary>Parses release versions of the form "1.X" or "1.X.Y".</summary>
    private static bool TryParse(string version, out int minor, out int patch)
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
