using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;

namespace GameDashboard.Tests.GameTemplates;

public class CuratedGameTemplatesTests
{
    public static IEnumerable<object[]> AllTemplates() =>
        CuratedGameTemplates.All.Values.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Has_At_Least_One_Port(GameTemplate template)
    {
        Assert.NotEmpty(template.DefaultPorts);
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Ports_Are_In_Valid_Range_And_Have_Names(GameTemplate template)
    {
        foreach (var port in template.DefaultPorts)
        {
            Assert.False(string.IsNullOrWhiteSpace(port.Name));
            Assert.InRange(port.ContainerPort, 1, 65535);
            Assert.True(port.Protocol == "TCP" || port.Protocol == "UDP");
        }
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Ports_Have_Unique_Names(GameTemplate template)
    {
        var names = template.DefaultPorts.Select(p => p.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Resource_Requests_Do_Not_Exceed_Limits(GameTemplate template)
    {
        var resources = template.DefaultResources;

        Assert.True(ParseCpu(resources.CpuRequest) <= ParseCpu(resources.CpuLimit),
            $"CPU request {resources.CpuRequest} exceeds limit {resources.CpuLimit}");

        Assert.True(ParseMemory(resources.MemoryRequest) <= ParseMemory(resources.MemoryLimit),
            $"Memory request {resources.MemoryRequest} exceeds limit {resources.MemoryLimit}");
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Has_NonEmpty_Image_And_DisplayName_And_MountPath(GameTemplate template)
    {
        Assert.False(string.IsNullOrWhiteSpace(template.ImageTag));
        Assert.False(string.IsNullOrWhiteSpace(template.DisplayName));
        Assert.False(string.IsNullOrWhiteSpace(template.DataMountPath));
        Assert.StartsWith("/", template.DataMountPath);
    }

    [Theory]
    [MemberData(nameof(AllTemplates))]
    public void Template_Storage_Is_Positive(GameTemplate template)
    {
        Assert.True(template.DefaultStorageBytes > 0);
    }

    [Fact]
    public void Cs2_Template_Wires_SRCDS_TOKEN_And_RCONPW_As_Secrets()
    {
        var cs2 = CuratedGameTemplates.Cs2;

        Assert.True(cs2.SecretKeyRefs.ContainsKey("SRCDS_TOKEN"));
        Assert.True(cs2.SecretKeyRefs.ContainsKey("CS2_RCONPW"));

        // Secret-sourced keys must never also appear in the plaintext ConfigMap defaults.
        Assert.False(cs2.DefaultConfig.ContainsKey("SRCDS_TOKEN"));
        Assert.False(cs2.DefaultConfig.ContainsKey("CS2_RCONPW"));
    }

    [Fact]
    public void Minecraft_Template_Sets_Eula_True_And_Wires_Rcon_Password_As_Secret()
    {
        var mc = CuratedGameTemplates.Minecraft;

        Assert.Equal("TRUE", mc.DefaultConfig["EULA"]);
        Assert.True(mc.SecretKeyRefs.ContainsKey("RCON_PASSWORD"));
        Assert.False(mc.DefaultConfig.ContainsKey("RCON_PASSWORD"));
    }

    [Fact]
    public void All_Catalog_Is_Keyed_By_ImageTag_With_Fifteen_Entries()
    {
        Assert.Equal(15, CuratedGameTemplates.All.Count);
        Assert.Same(CuratedGameTemplates.Cs2, CuratedGameTemplates.All[CuratedGameTemplates.Cs2.ImageTag]);
        Assert.Same(CuratedGameTemplates.Insurgency, CuratedGameTemplates.All[CuratedGameTemplates.Insurgency.ImageTag]);
        Assert.Same(CuratedGameTemplates.Minecraft, CuratedGameTemplates.All[CuratedGameTemplates.Minecraft.ImageTag]);
        Assert.Same(CuratedGameTemplates.Rust, CuratedGameTemplates.All[CuratedGameTemplates.Rust.ImageTag]);
        Assert.Same(CuratedGameTemplates.Satisfactory, CuratedGameTemplates.All[CuratedGameTemplates.Satisfactory.ImageTag]);
    }

    // --- tiny Kubernetes quantity parsers, test-local only ---

    private static double ParseCpu(string value) =>
        value.EndsWith('m') ? double.Parse(value[..^1]) : double.Parse(value) * 1000;

    private static long ParseMemory(string value)
    {
        if (value.EndsWith("Gi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024 * 1024);
        if (value.EndsWith("Mi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024);
        if (value.EndsWith("Ki")) return (long)(double.Parse(value[..^2]) * 1024);
        return long.Parse(value);
    }
}
