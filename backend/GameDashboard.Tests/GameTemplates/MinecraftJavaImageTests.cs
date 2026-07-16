using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;

namespace GameDashboard.Tests.GameTemplates;

/// <summary>
/// The Minecraft-version → JVM image matrix (docs/minecraft-server-types.md):
/// old Minecraft versions crash on new JVMs, so the deploy builder must swap in
/// the itzg tag whose bundled Java matches the requested version.
/// </summary>
public class MinecraftJavaImageTests
{
    private static string Resolve(params (string Key, string Value)[] config) =>
        MinecraftJavaImage.Resolve(config.ToDictionary(c => c.Key, c => c.Value));

    [Theory]
    [InlineData("1.8.9")]
    [InlineData("1.12.2")]
    [InlineData("1.16.5")]
    public void Versions_Through_1_16_Get_Java8(string version) =>
        Assert.Equal(MinecraftJavaImage.Java8, Resolve(("VERSION", version)));

    [Theory]
    [InlineData("1.17")]
    [InlineData("1.17.1")]
    [InlineData("1.18.2")]
    [InlineData("1.19.4")]
    [InlineData("1.20")]
    [InlineData("1.20.4")]
    public void Versions_1_17_Through_1_20_4_Get_Java17(string version) =>
        Assert.Equal(MinecraftJavaImage.Java17, Resolve(("VERSION", version)));

    [Theory]
    [InlineData("1.20.5")]
    [InlineData("1.20.6")]
    [InlineData("1.21")]
    [InlineData("1.21.7")]
    public void Versions_From_1_20_5_Get_Java21(string version) =>
        Assert.Equal(MinecraftJavaImage.Java21, Resolve(("VERSION", version)));

    [Theory]
    [InlineData("LATEST")]
    [InlineData("latest")]
    [InlineData("24w14a")] // snapshot — unparseable, run the modern JVM
    [InlineData("")]
    public void Latest_And_Unparseable_Versions_Get_Java21(string version) =>
        Assert.Equal(MinecraftJavaImage.Java21, Resolve(("VERSION", version)));

    [Fact]
    public void No_Version_Key_Gets_Java21() =>
        Assert.Equal(MinecraftJavaImage.Java21, Resolve(("TYPE", "PAPER")));

    [Fact]
    public void Modpack_Always_Gets_Java21_Even_With_An_Old_Version_Key() =>
        Assert.Equal(MinecraftJavaImage.Java21,
            Resolve(("MODRINTH_MODPACK", "some-pack"), ("VERSION", "1.12.2")));

    [Fact]
    public void Blank_Modpack_Value_Falls_Through_To_The_Version() =>
        Assert.Equal(MinecraftJavaImage.Java8,
            Resolve(("MODRINTH_MODPACK", " "), ("VERSION", "1.12.2")));

    // --- wiring through the deploy builder ---

    private static readonly IReadOnlySet<int> NoUsedPorts = new HashSet<int>();

    private static string BuiltImage(IDictionary<string, string>? overrides)
    {
        var template = CuratedGameTemplates.Minecraft;
        var request = new DeployServerRequest("mc-test", template.ImageTag, null, overrides);
        var result = new DeploymentBuilderService().Build(
            template, request, NoUsedPorts, "game-servers", "game-secrets");
        return result.Deployment.Spec.Template.Spec.Containers[0].Image;
    }

    [Fact]
    public void Build_Uses_Java21_For_Default_Minecraft_Deploy() =>
        Assert.Equal(MinecraftJavaImage.Java21, BuiltImage(null));

    [Fact]
    public void Build_Uses_Java8_For_Old_Version_Override() =>
        Assert.Equal(MinecraftJavaImage.Java8,
            BuiltImage(new Dictionary<string, string> { ["VERSION"] = "1.12.2" }));

    [Fact]
    public void Build_Uses_Java17_For_Mid_Version_Override() =>
        Assert.Equal(MinecraftJavaImage.Java17,
            BuiltImage(new Dictionary<string, string> { ["TYPE"] = "FORGE", ["VERSION"] = "1.20.1" }));

    [Fact]
    public void Build_Keeps_Template_Image_For_Generic_Templates()
    {
        var template = CuratedGameTemplates.Cs2;
        var request = new DeployServerRequest("cs2-test", template.ImageTag, null,
            new Dictionary<string, string> { ["VERSION"] = "1.12.2" });
        var result = new DeploymentBuilderService().Build(
            template, request, NoUsedPorts, "game-servers", "game-secrets");

        Assert.Equal(template.ImageTag, result.Deployment.Spec.Template.Spec.Containers[0].Image);
    }

    [Fact]
    public void Build_Passes_Modpack_Overrides_Into_ConfigMap_Unchanged()
    {
        var template = CuratedGameTemplates.Minecraft;
        var overrides = new Dictionary<string, string>
        {
            ["MOD_PLATFORM"] = "MODRINTH",
            ["MODRINTH_MODPACK"] = "cobblemon",
            ["MODRINTH_PROJECTS"] = "sodium,lithium",
            ["MODRINTH_DOWNLOAD_DEPENDENCIES"] = "required"
        };
        var request = new DeployServerRequest("mc-pack", template.ImageTag, null, overrides);
        var result = new DeploymentBuilderService().Build(
            template, request, NoUsedPorts, "game-servers", "game-secrets");

        Assert.Equal("cobblemon", result.ConfigMap.Data["MODRINTH_MODPACK"]);
        Assert.Equal("sodium,lithium", result.ConfigMap.Data["MODRINTH_PROJECTS"]);
        // Template defaults must still be merged in alongside the extras.
        Assert.Equal("TRUE", result.ConfigMap.Data["EULA"]);
        Assert.Equal(MinecraftJavaImage.Java21, result.Deployment.Spec.Template.Spec.Containers[0].Image);
    }
}
