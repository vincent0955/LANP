using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;

namespace GameDashboard.Tests.Services;

public class DeploymentBuilderServiceTests
{
    private const string Namespace = "game-servers";
    private const string SecretName = "game-secrets";

    private static readonly IReadOnlySet<int> NoUsedPorts = new HashSet<int>();

    private static DeployServerRequest Request(
        string name = "my-cs2",
        ResourceSpec? resources = null,
        IDictionary<string, string>? overrides = null) =>
        new(name, CuratedGameTemplates.Cs2.ImageTag, resources, overrides);

    // --- manifest correctness ---

    [Fact]
    public void Build_Sets_Namespace_On_All_Four_Resources()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        Assert.Equal(Namespace, result.Deployment.Metadata.NamespaceProperty);
        Assert.Equal(Namespace, result.Service.Metadata.NamespaceProperty);
        Assert.Equal(Namespace, result.Pvc.Metadata.NamespaceProperty);
        Assert.Equal(Namespace, result.ConfigMap.Metadata.NamespaceProperty);
    }

    [Fact]
    public void Build_Always_Sets_Replicas_To_One()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        Assert.Equal(1, result.Deployment.Spec.Replicas);
    }

    [Fact]
    public void Build_Sets_ReadWriteOnce_Access_Mode_On_Pvc()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        Assert.Single(result.Pvc.Spec.AccessModes);
        Assert.Equal("ReadWriteOnce", result.Pvc.Spec.AccessModes[0]);
    }

    [Fact]
    public void Build_Sets_Correct_Data_Mount_Path_Per_Template()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        var mount = result.Deployment.Spec.Template.Spec.Containers[0].VolumeMounts[0];
        Assert.Equal(CuratedGameTemplates.Cs2.DataMountPath, mount.MountPath);
    }

    [Fact]
    public void Build_Wires_ConfigMap_Via_EnvFrom()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: "cs2-a"), NoUsedPorts, Namespace, SecretName);

        var container = result.Deployment.Spec.Template.Spec.Containers[0];
        Assert.Single(container.EnvFrom);
        Assert.Equal("cs2-a-config", container.EnvFrom[0].ConfigMapRef.Name);
        Assert.Equal("cs2-a-config", result.ConfigMap.Metadata.Name);
    }

    [Fact]
    public void Build_Adds_Tcp_Readiness_Probe_On_First_Tcp_Port()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        // CS2's first (and only) TCP port is rcon 27015 — srcds serves RCON and
        // gameplay on the same number, so a TCP probe on it means srcds is up.
        // Status must stay Pending until then (first-deploy downloads, Req 2.4).
        var probe = result.Deployment.Spec.Template.Spec.Containers[0].ReadinessProbe;
        Assert.NotNull(probe);
        Assert.NotNull(probe!.TcpSocket);
        Assert.Equal("27015", probe.TcpSocket.Port.Value);
    }

    [Fact]
    public void Build_Omits_Readiness_Probe_For_Udp_Only_Templates()
    {
        var builder = new DeploymentBuilderService();
        var request = new DeployServerRequest("my-palworld", CuratedGameTemplates.Palworld.ImageTag, null, null);
        var result = builder.Build(CuratedGameTemplates.Palworld, request, NoUsedPorts, Namespace, SecretName);

        // A TCP probe against a UDP-only server never succeeds and would pin the
        // status at Pending forever — these templates get no probe.
        Assert.Null(result.Deployment.Spec.Template.Spec.Containers[0].ReadinessProbe);
    }

    [Fact]
    public void Build_Wires_Secret_Keys_Via_SecretKeyRef_Not_ConfigMap()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: "cs2-a"), NoUsedPorts, Namespace, SecretName);

        var container = result.Deployment.Spec.Template.Spec.Containers[0];
        var envNames = container.Env.Select(e => e.Name).ToList();

        Assert.Contains("SRCDS_TOKEN", envNames);
        Assert.Contains("CS2_RCONPW", envNames);

        foreach (var env in container.Env)
        {
            Assert.Equal(SecretName, env.ValueFrom.SecretKeyRef.Name);
        }

        Assert.False(result.ConfigMap.Data.ContainsKey("SRCDS_TOKEN"));
        Assert.False(result.ConfigMap.Data.ContainsKey("CS2_RCONPW"));
    }

    [Fact]
    public void Build_Sets_Service_Type_NodePort_With_Matching_Selector()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: "cs2-a"), NoUsedPorts, Namespace, SecretName);

        Assert.Equal("NodePort", result.Service.Spec.Type);
        Assert.Equal("cs2-a", result.Service.Spec.Selector["app"]);
        Assert.Equal("cs2-a", result.Deployment.Spec.Selector.MatchLabels["app"]);
        Assert.Equal("cs2-a", result.Deployment.Spec.Template.Metadata.Labels["app"]);
    }

    [Fact]
    public void Build_Container_Ports_Match_Template_Ports()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        var containerPorts = result.Deployment.Spec.Template.Spec.Containers[0].Ports;
        Assert.Equal(CuratedGameTemplates.Cs2.DefaultPorts.Count, containerPorts.Count);

        foreach (var templatePort in CuratedGameTemplates.Cs2.DefaultPorts)
        {
            Assert.Contains(containerPorts, p =>
                p.Name == templatePort.Name &&
                p.ContainerPort == templatePort.ContainerPort &&
                p.Protocol == templatePort.Protocol);
        }
    }

    // --- NodePort assignment / conflict avoidance ---

    [Fact]
    public void Build_Assigns_NodePorts_Starting_At_30000_When_None_Used()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Minecraft, Request(name: "mc-a"), NoUsedPorts, Namespace, SecretName);

        var ports = result.Service.Spec.Ports.OrderBy(p => p.NodePort).ToList();
        Assert.Equal(30000, ports[0].NodePort);
        Assert.Equal(30001, ports[1].NodePort);
    }

    [Fact]
    public void Build_Skips_Already_Used_NodePorts()
    {
        var used = new HashSet<int> { 30000, 30001, 30002 };
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Minecraft, Request(name: "mc-a"), used, Namespace, SecretName);

        var assignedPorts = result.Service.Spec.Ports.Select(p => p.NodePort!.Value).ToList();

        Assert.DoesNotContain(30000, assignedPorts);
        Assert.DoesNotContain(30001, assignedPorts);
        Assert.DoesNotContain(30002, assignedPorts);
        Assert.All(assignedPorts, p => Assert.InRange(p, 30000, 32767));
    }

    [Fact]
    public void Build_Assigns_Unique_NodePorts_Within_Same_Service()
    {
        // CS2 has three ports; none of the three assigned NodePorts may collide.
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: "cs2-a"), NoUsedPorts, Namespace, SecretName);

        var nodePorts = result.Service.Spec.Ports.Select(p => p.NodePort).ToList();
        Assert.Equal(nodePorts.Count, nodePorts.Distinct().Count());
    }

    [Fact]
    public void Build_Throws_When_NodePort_Range_Fully_Exhausted()
    {
        var allPorts = Enumerable.Range(30000, 32767 - 30000 + 1).ToHashSet();
        var builder = new DeploymentBuilderService();

        Assert.Throws<InvalidOperationException>(() =>
            builder.Build(CuratedGameTemplates.Minecraft, Request(name: "mc-a"), allPorts, Namespace, SecretName));
    }

    // --- resource defaults / overrides ---

    [Fact]
    public void Build_Uses_Template_Defaults_When_No_Override_Given()
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(), NoUsedPorts, Namespace, SecretName);

        // ResourceQuantity normalizes canonical values (e.g. "1000m" -> "1") when
        // round-tripped through the k8s client, so assert on parsed millicore/byte
        // values rather than exact string equality.
        var resources = result.Deployment.Spec.Template.Spec.Containers[0].Resources;
        Assert.Equal(1000, ParseCpuMillicores(resources.Requests["cpu"].ToString()));
        Assert.Equal(4000, ParseCpuMillicores(resources.Limits["cpu"].ToString()));
        Assert.Equal(ParseMemoryBytes("2Gi"), ParseMemoryBytes(resources.Requests["memory"].ToString()));
        Assert.Equal(ParseMemoryBytes("4Gi"), ParseMemoryBytes(resources.Limits["memory"].ToString()));
    }

    [Fact]
    public void Build_Uses_Override_Resources_When_Provided()
    {
        var overrideSpec = new ResourceSpec("2000m", "3000m", "1Gi", "2Gi");
        var builder = new DeploymentBuilderService();
        var result = builder.Build(
            CuratedGameTemplates.Cs2, Request(resources: overrideSpec), NoUsedPorts, Namespace, SecretName);

        var resources = result.Deployment.Spec.Template.Spec.Containers[0].Resources;
        Assert.Equal(2000, ParseCpuMillicores(resources.Requests["cpu"].ToString()));
        Assert.Equal(3000, ParseCpuMillicores(resources.Limits["cpu"].ToString()));
        Assert.Equal(ParseMemoryBytes("1Gi"), ParseMemoryBytes(resources.Requests["memory"].ToString()));
        Assert.Equal(ParseMemoryBytes("2Gi"), ParseMemoryBytes(resources.Limits["memory"].ToString()));
    }

    [Fact]
    public void Build_Rejects_Resource_Override_Where_Request_Exceeds_Limit()
    {
        var invalidSpec = new ResourceSpec("4000m", "2000m", "2Gi", "4Gi");
        var builder = new DeploymentBuilderService();

        Assert.Throws<ArgumentException>(() =>
            builder.Build(CuratedGameTemplates.Cs2, Request(resources: invalidSpec), NoUsedPorts, Namespace, SecretName));
    }

    [Fact]
    public void Build_Rejects_Memory_Override_Where_Request_Exceeds_Limit()
    {
        var invalidSpec = new ResourceSpec("500m", "2000m", "4Gi", "2Gi");
        var builder = new DeploymentBuilderService();

        Assert.Throws<ArgumentException>(() =>
            builder.Build(CuratedGameTemplates.Cs2, Request(resources: invalidSpec), NoUsedPorts, Namespace, SecretName));
    }

    [Fact]
    public void Build_Merges_Config_Overrides_With_Template_Defaults()
    {
        var overrides = new Dictionary<string, string> { ["CS2_MAP"] = "de_mirage", ["CS2_MAXPLAYERS"] = "10" };
        var builder = new DeploymentBuilderService();
        var result = builder.Build(
            CuratedGameTemplates.Cs2, Request(overrides: overrides), NoUsedPorts, Namespace, SecretName);

        Assert.Equal("de_mirage", result.ConfigMap.Data["CS2_MAP"]);
        Assert.Equal("10", result.ConfigMap.Data["CS2_MAXPLAYERS"]);
        // Untouched default keys remain.
        Assert.Equal("mg_active", result.ConfigMap.Data["CS2_MAPGROUP"]);
    }

    [Fact]
    public void Build_Ignores_Config_Override_For_Secret_Sourced_Key()
    {
        var overrides = new Dictionary<string, string> { ["SRCDS_TOKEN"] = "leaked-value" };
        var builder = new DeploymentBuilderService();
        var result = builder.Build(
            CuratedGameTemplates.Cs2, Request(overrides: overrides), NoUsedPorts, Namespace, SecretName);

        Assert.False(result.ConfigMap.Data.ContainsKey("SRCDS_TOKEN"));
    }

    // --- name validation ---

    [Theory]
    [InlineData("cs2-server")]
    [InlineData("a")]
    [InlineData("my-server-1")]
    [InlineData("server123")]
    public void Build_Accepts_Valid_DNS1123_Names(string name)
    {
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: name), NoUsedPorts, Namespace, SecretName);

        Assert.Equal(name, result.Deployment.Metadata.Name);
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
        var builder = new DeploymentBuilderService();

        Assert.Throws<ArgumentException>(() =>
            builder.Build(CuratedGameTemplates.Cs2, Request(name: name), NoUsedPorts, Namespace, SecretName));
    }

    [Fact]
    public void Build_Rejects_Name_Longer_Than_63_Characters()
    {
        var tooLong = new string('a', 64);
        var builder = new DeploymentBuilderService();

        Assert.Throws<ArgumentException>(() =>
            builder.Build(CuratedGameTemplates.Cs2, Request(name: tooLong), NoUsedPorts, Namespace, SecretName));
    }

    [Fact]
    public void Build_Accepts_Name_Exactly_63_Characters()
    {
        var exactly63 = "a" + new string('b', 61) + "a";
        var builder = new DeploymentBuilderService();
        var result = builder.Build(CuratedGameTemplates.Cs2, Request(name: exactly63), NoUsedPorts, Namespace, SecretName);

        Assert.Equal(exactly63, result.Deployment.Metadata.Name);
    }

    // --- test-local Kubernetes quantity parsers ---

    private static double ParseCpuMillicores(string value) =>
        value.EndsWith('m') ? double.Parse(value[..^1]) : double.Parse(value) * 1000;

    private static long ParseMemoryBytes(string value)
    {
        if (value.EndsWith("Gi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024 * 1024);
        if (value.EndsWith("Mi")) return (long)(double.Parse(value[..^2]) * 1024 * 1024);
        if (value.EndsWith("Ki")) return (long)(double.Parse(value[..^2]) * 1024);
        return long.Parse(value);
    }
}
