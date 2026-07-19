using System.Text.Json;
using Docker.DotNet.Models;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Ports the DeploymentBuilderService tests to ContainerSpecBuilder: same port
/// allocator, same resource validation, same config merge — but the output is
/// CreateContainerParameters instead of k8s manifests. The old
/// "Recreate strategy" concern (never run old and new pods against the same
/// data volume) is now structural: DockerService.UpdateConfigAsync removes the
/// container before creating its replacement.
/// </summary>
public class ContainerSpecBuilderTests
{
    private static readonly IReadOnlySet<int> NoUsedPorts = new HashSet<int>();
    private static readonly IReadOnlyDictionary<string, string> NoSecrets =
        new Dictionary<string, string>();

    private static DeployServerRequest Request(
        string name = "my-cs2",
        ResourceSpec? resources = null,
        IDictionary<string, string>? overrides = null) =>
        new(name, CuratedGameTemplates.Cs2.ImageTag, resources, overrides);

    private static ContainerServerSpec Build(
        GameTemplate? template = null,
        DeployServerRequest? request = null,
        IReadOnlySet<int>? usedPorts = null,
        IReadOnlyDictionary<string, string>? secretEnv = null) =>
        new ContainerSpecBuilder().Build(
            template ?? CuratedGameTemplates.Cs2,
            request ?? Request(),
            usedPorts ?? NoUsedPorts,
            secretEnv ?? NoSecrets);

    // --- container spec correctness ---

    [Fact]
    public void Build_Names_Container_And_Volume_After_The_Server()
    {
        var spec = Build(request: Request(name: "cs2-a"));

        Assert.Equal("cs2-a", spec.Name);
        Assert.Equal("cs2-a", spec.CreateParameters.Name);
        Assert.Equal("cs2-a-data", spec.VolumeName);
        Assert.Equal("cs2-a-data", spec.CreateParameters.HostConfig.Mounts[0].Source);
    }

    [Fact]
    public void Build_Mounts_The_Data_Volume_At_The_Template_Mount_Path()
    {
        var spec = Build();

        var mount = spec.CreateParameters.HostConfig.Mounts[0];
        Assert.Equal("volume", mount.Type);
        Assert.Equal(CuratedGameTemplates.Cs2.DataMountPath, mount.Target);
    }

    [Fact]
    public void Build_Sets_Restart_Policy_UnlessStopped()
    {
        var spec = Build();

        // unless-stopped ⇒ "exited" can only mean the user stopped it (crashes
        // restart, showing "restarting") — ContainerStatusMapper relies on this.
        Assert.Equal(RestartPolicyKind.UnlessStopped,
            spec.CreateParameters.HostConfig.RestartPolicy.Name);
    }

    [Fact]
    public void Build_Puts_Merged_Config_Into_Env_As_KeyValue_Pairs()
    {
        var spec = Build(request: Request(overrides: new Dictionary<string, string>
        {
            ["CS2_MAP"] = "de_mirage",
        }));

        Assert.Contains("CS2_MAP=de_mirage", spec.CreateParameters.Env);
        // Untouched template defaults remain.
        Assert.Contains("CS2_MAPGROUP=mg_active", spec.CreateParameters.Env);
    }

    [Fact]
    public void Build_Appends_Resolved_Secret_Env_But_Never_Labels_It()
    {
        var spec = Build(secretEnv: new Dictionary<string, string>
        {
            ["SRCDS_TOKEN"] = "hunter2",
        });

        Assert.Contains("SRCDS_TOKEN=hunter2", spec.CreateParameters.Env);

        // The config label is plaintext on the container; secret values must
        // only ever exist in env (sourced from the store at deploy time).
        var configLabel = spec.CreateParameters.Labels[ContainerLabels.Config];
        Assert.DoesNotContain("hunter2", configLabel);
        var config = JsonSerializer.Deserialize<Dictionary<string, string>>(configLabel)!;
        Assert.False(config.ContainsKey("SRCDS_TOKEN"));
    }

    [Fact]
    public void Build_Marks_The_Container_As_Managed()
    {
        var spec = Build();

        Assert.Equal(ContainerLabels.ManagedValue,
            spec.CreateParameters.Labels[ContainerLabels.Managed]);
        Assert.Equal(CuratedGameTemplates.Cs2.ImageTag,
            spec.CreateParameters.Labels[ContainerLabels.ImageTag]);
    }

    [Fact]
    public void Build_Labels_Round_Trip_Ports_And_Resources_As_Json()
    {
        var spec = Build();
        var labels = spec.CreateParameters.Labels;

        var ports = JsonSerializer.Deserialize<List<PortMapping>>(labels[ContainerLabels.Ports])!;
        Assert.Equal(spec.Ports.Count, ports.Count);
        Assert.Equal(spec.Ports[0], ports[0]);

        var resources = JsonSerializer.Deserialize<ResourceSpec>(labels[ContainerLabels.Resources])!;
        Assert.Equal(spec.Resources, resources);

        // CreatedAt must be a round-trippable timestamp — it preserves the
        // original deploy time across config-change recreations.
        Assert.True(DateTime.TryParse(
            labels[ContainerLabels.CreatedAt], null,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));
    }

    [Fact]
    public void Build_Container_Ports_Match_Template_Ports()
    {
        var spec = Build();

        Assert.Equal(CuratedGameTemplates.Cs2.DefaultPorts.Count, spec.Ports.Count);
        foreach (var templatePort in CuratedGameTemplates.Cs2.DefaultPorts)
        {
            Assert.Contains(spec.Ports, p =>
                p.Name == templatePort.Name &&
                p.ContainerPort == templatePort.ContainerPort &&
                p.Protocol == templatePort.Protocol);
        }
    }

    [Fact]
    public void Build_Exposes_And_Binds_Every_Assigned_Port()
    {
        var spec = Build();

        foreach (var port in spec.Ports)
        {
            var key = $"{port.ContainerPort}/{port.Protocol.ToLowerInvariant()}";
            Assert.True(spec.CreateParameters.ExposedPorts.ContainsKey(key));
            var binding = Assert.Single(spec.CreateParameters.HostConfig.PortBindings[key]);
            Assert.Equal(port.NodePort.ToString(), binding.HostPort);
        }
    }

    // --- host port assignment / conflict avoidance ---

    [Fact]
    public void Build_Assigns_Host_Ports_Starting_At_30000_When_None_Used()
    {
        var request = new DeployServerRequest("mc-a", CuratedGameTemplates.Minecraft.ImageTag, null, null);
        var spec = Build(CuratedGameTemplates.Minecraft, request);

        var ports = spec.Ports.OrderBy(p => p.NodePort).ToList();
        Assert.Equal(30000, ports[0].NodePort);
        Assert.Equal(30001, ports[1].NodePort);
    }

    [Fact]
    public void Build_Skips_Already_Used_Host_Ports()
    {
        var used = new HashSet<int> { 30000, 30001, 30002 };
        var request = new DeployServerRequest("mc-a", CuratedGameTemplates.Minecraft.ImageTag, null, null);
        var spec = Build(CuratedGameTemplates.Minecraft, request, used);

        var assigned = spec.Ports.Select(p => p.NodePort).ToList();

        Assert.DoesNotContain(30000, assigned);
        Assert.DoesNotContain(30001, assigned);
        Assert.DoesNotContain(30002, assigned);
        Assert.All(assigned, p => Assert.InRange(
            p, ContainerSpecBuilder.HostPortRangeStart, ContainerSpecBuilder.HostPortRangeEnd));
    }

    [Fact]
    public void Build_Assigns_Unique_Host_Ports_Within_Same_Server()
    {
        // CS2 has three ports; none of the three assigned host ports may collide.
        var spec = Build(request: Request(name: "cs2-a"));

        var ports = spec.Ports.Select(p => p.NodePort).ToList();
        Assert.Equal(ports.Count, ports.Distinct().Count());
    }

    [Fact]
    public void Build_Throws_When_Host_Port_Range_Fully_Exhausted()
    {
        var allPorts = Enumerable.Range(
            ContainerSpecBuilder.HostPortRangeStart,
            ContainerSpecBuilder.HostPortRangeEnd - ContainerSpecBuilder.HostPortRangeStart + 1).ToHashSet();
        var request = new DeployServerRequest("mc-a", CuratedGameTemplates.Minecraft.ImageTag, null, null);

        Assert.Throws<InvalidOperationException>(() =>
            Build(CuratedGameTemplates.Minecraft, request, allPorts));
    }

    // --- resource defaults / overrides ---

    [Fact]
    public void Build_Uses_Template_Defaults_When_No_Override_Given()
    {
        var spec = Build();

        Assert.Equal(CuratedGameTemplates.Cs2.DefaultResources, spec.Resources);
    }

    [Fact]
    public void Build_Translates_Resources_Into_Docker_Host_Limits()
    {
        var overrideSpec = new ResourceSpec("2000m", "3000m", "1Gi", "2Gi");
        var spec = Build(request: Request(resources: overrideSpec));

        var hostConfig = spec.CreateParameters.HostConfig;
        // MemoryReservation = request (soft), Memory = limit (hard),
        // NanoCPUs = CPU limit; the CPU request has no Docker equivalent.
        Assert.Equal(1L * 1024 * 1024 * 1024, hostConfig.MemoryReservation);
        Assert.Equal(2L * 1024 * 1024 * 1024, hostConfig.Memory);
        Assert.Equal(3000L * 1_000_000, hostConfig.NanoCPUs);
    }

    [Fact]
    public void Build_Rejects_Cpu_Override_Where_Request_Exceeds_Limit()
    {
        var invalidSpec = new ResourceSpec("4000m", "2000m", "2Gi", "4Gi");

        Assert.Throws<ArgumentException>(() => Build(request: Request(resources: invalidSpec)));
    }

    [Fact]
    public void Build_Rejects_Memory_Override_Where_Request_Exceeds_Limit()
    {
        var invalidSpec = new ResourceSpec("500m", "2000m", "4Gi", "2Gi");

        Assert.Throws<ArgumentException>(() => Build(request: Request(resources: invalidSpec)));
    }

    [Theory]
    [InlineData("2Gi", 2L * 1024 * 1024 * 1024)]
    [InlineData("512Mi", 512L * 1024 * 1024)]
    [InlineData("100Ki", 100L * 1024)]
    [InlineData("12345", 12345L)]
    public void ParseMemoryBytes_Handles_K8s_Style_Quantities(string quantity, long expected)
    {
        Assert.Equal(expected, ContainerSpecBuilder.ParseMemoryBytes(quantity));
    }

    // --- config merge ---

    [Fact]
    public void Build_Merges_Config_Overrides_With_Template_Defaults()
    {
        var overrides = new Dictionary<string, string> { ["CS2_MAP"] = "de_mirage", ["CS2_MAXPLAYERS"] = "10" };
        var spec = Build(request: Request(overrides: overrides));

        Assert.Equal("de_mirage", spec.Config["CS2_MAP"]);
        Assert.Equal("10", spec.Config["CS2_MAXPLAYERS"]);
        // Untouched default keys remain.
        Assert.Equal("mg_active", spec.Config["CS2_MAPGROUP"]);
    }

    [Fact]
    public void Build_Ignores_Config_Override_For_Secret_Sourced_Key()
    {
        var overrides = new Dictionary<string, string> { ["SRCDS_TOKEN"] = "leaked-value" };
        var spec = Build(request: Request(overrides: overrides));

        Assert.False(spec.Config.ContainsKey("SRCDS_TOKEN"));
        Assert.DoesNotContain(spec.CreateParameters.Env, e => e.Contains("leaked-value"));
    }

    // --- name validation ---

    [Theory]
    [InlineData("cs2-server")]
    [InlineData("a")]
    [InlineData("my-server-1")]
    [InlineData("server123")]
    public void Build_Accepts_Valid_DNS1123_Names(string name)
    {
        var spec = Build(request: Request(name: name));

        Assert.Equal(name, spec.CreateParameters.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Cs2-Server")]        // uppercase not allowed
    [InlineData("cs2_server")]        // underscore not allowed
    [InlineData("-cs2-server")]       // cannot start with hyphen
    [InlineData("cs2-server-")]       // cannot end with hyphen
    [InlineData("cs2 server")]        // space not allowed
    public void Build_Rejects_Invalid_DNS1123_Names(string name)
    {
        Assert.Throws<ArgumentException>(() => Build(request: Request(name: name)));
    }

    [Fact]
    public void Build_Rejects_Name_Longer_Than_63_Characters()
    {
        var tooLong = new string('a', 64);

        Assert.Throws<ArgumentException>(() => Build(request: Request(name: tooLong)));
    }

    [Fact]
    public void Build_Accepts_Name_Exactly_63_Characters()
    {
        var exactly63 = "a" + new string('b', 61) + "a";
        var spec = Build(request: Request(name: exactly63));

        Assert.Equal(exactly63, spec.CreateParameters.Name);
    }

    [Fact]
    public void VolumeNameFor_Is_The_Name_Plus_Data_Suffix()
    {
        // DeleteServerAsync derives the volume to remove from the server name
        // alone (the container may already be gone), so this must stay stable.
        Assert.Equal("my-server-data", ContainerSpecBuilder.VolumeNameFor("my-server"));
    }
}
