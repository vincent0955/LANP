using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Covers DockerService against a mocked engine client: health/unreachable
/// translation, managed-container filtering, deploy validation and its
/// fail-fast secret check, scale/delete guards, and the tracker merge.
/// Replaces the KubernetesService unit tests; full happy-path lifecycle runs
/// live in the Phase 10 integration suite.
/// </summary>
public class DockerServiceTests
{
    private const string Namespace = "game-servers";

    private sealed class Harness
    {
        public Mock<IContainerOperations> Containers { get; } = new();
        public Mock<IImageOperations> Images { get; } = new();
        public Mock<IVolumeOperations> Volumes { get; } = new();
        public Mock<ISystemOperations> System { get; } = new();
        public Mock<ISecretsStore> Secrets { get; } = new();
        public Mock<ILastActiveStore> LastActive { get; } = new();
        public Mock<ITcpReadinessProber> Prober { get; } = new();
        public Mock<IMinecraftMetadataService> MinecraftMetadata { get; } = new();
        public DeployTracker Tracker { get; } = new();
        public DockerService Service { get; }

        public Harness(bool engineReachable)
        {
            var factory = new Mock<IDockerClientFactory>();
            if (engineReachable)
            {
                var client = new Mock<IDockerClient>();
                client.SetupGet(c => c.Containers).Returns(Containers.Object);
                client.SetupGet(c => c.Images).Returns(Images.Object);
                client.SetupGet(c => c.Volumes).Returns(Volumes.Object);
                client.SetupGet(c => c.System).Returns(System.Object);

                // An accepted deploy hands off to a background pull; keep that
                // pull blocked forever so its outcome never races a test's
                // assertions about the tracker's Pending state.
                Images.Setup(i => i.CreateImageAsync(
                        It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig>(),
                        It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
                    .Returns(new TaskCompletionSource().Task);
                factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync((client.Object, null));
            }
            else
            {
                factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));
            }

            // Default: probe says ready, so "running" maps straight to Running.
            Prober.Setup(p => p.IsReadyAsync(
                    It.IsAny<string>(), It.IsAny<string?>(),
                    It.IsAny<IReadOnlyList<PortMapping>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            // Default: metrics unavailable, so the warn-only capacity check is skipped.
            var metrics = new Mock<IMetricsService>();
            metrics.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

            Service = new DockerService(
                factory.Object,
                new ContainerSpecBuilder(),
                Tracker,
                Secrets.Object,
                LastActive.Object,
                Prober.Object,
                Mock.Of<IRconService>(),
                metrics.Object,
                MinecraftMetadata.Object,
                Options.Create(new DashboardOptions { Namespace = Namespace }),
                NullLogger<DockerService>.Instance);
        }

        public void SetupNoContainers()
        {
            Containers.Setup(c => c.ListContainersAsync(
                    It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ContainerListResponse>());
        }

        public void SetupInspectNotFound(string name)
        {
            Containers.Setup(c => c.InspectContainerAsync(name, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container"));
        }

        /// <summary>Configures every template-referenced secret as present in the store.</summary>
        public void SetupAllSecretsConfigured()
        {
            Secrets.Setup(s => s.GetValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("configured-value");
        }
    }

    private static ContainerInspectResponse ManagedContainer(
        string name, string state = "running", IDictionary<string, string>? extraLabels = null)
    {
        var labels = new Dictionary<string, string>
        {
            [ContainerLabels.Managed] = ContainerLabels.ManagedValue,
            [ContainerLabels.Game] = name,
        };
        foreach (var (key, value) in extraLabels ?? new Dictionary<string, string>())
        {
            labels[key] = value;
        }

        return new ContainerInspectResponse
        {
            ID = $"{name}-id",
            Name = $"/{name}",
            Created = DateTime.UtcNow,
            Config = new Config { Labels = labels, Image = "some/image:latest" },
            State = new ContainerState { Status = state, Running = state == "running" },
            HostConfig = new HostConfig(),
        };
    }

    // --- health ---

    [Fact]
    public async Task GetHealthAsync_Reports_Unreachable_When_No_Engine_Answers()
    {
        var harness = new Harness(engineReachable: false);

        var health = await harness.Service.GetHealthAsync(CancellationToken.None);

        Assert.False(health.ClusterReachable);
        Assert.False(health.NamespaceReady);
        Assert.Equal(Namespace, health.Namespace);
        Assert.NotNull(health.Error);
    }

    [Fact]
    public async Task GetHealthAsync_Reports_Reachable_When_The_Engine_Answers_A_Ping()
    {
        var harness = new Harness(engineReachable: true);

        var health = await harness.Service.GetHealthAsync(CancellationToken.None);

        Assert.True(health.ClusterReachable);
        Assert.True(health.NamespaceReady);
        Assert.Null(health.Error);
    }

    [Fact]
    public async Task GetHealthAsync_Never_Throws_When_The_Ping_Fails()
    {
        var harness = new Harness(engineReachable: true);
        harness.System.Setup(s => s.PingAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var health = await harness.Service.GetHealthAsync(CancellationToken.None);

        Assert.False(health.ClusterReachable);
        Assert.NotNull(health.Error);
    }

    // --- reads ---

    [Fact]
    public async Task ListServersAsync_Returns_Empty_List_When_No_Containers()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupNoContainers();

        Assert.Empty(await harness.Service.ListServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListServersAsync_Throws_ClusterUnreachable_When_Engine_Down()
    {
        var harness = new Harness(engineReachable: false);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.ListServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListServersAsync_Translates_Connection_Failures_To_ClusterUnreachable()
    {
        // The factory can hold a cached client whose engine died since the last
        // call; low-level socket errors must still surface as 503, not 500.
        var harness = new Harness(engineReachable: true);
        harness.Containers.Setup(c => c.ListContainersAsync(
                It.IsAny<ContainersListParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.ListServersAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListServersAsync_Merges_In_Flight_Deploys_From_The_Tracker()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupNoContainers();
        harness.Tracker.TryRegister(new DeployTracker.PendingDeploy(
            "pulling-server", "some/image:latest", "some/image:latest",
            Array.Empty<PortMapping>(), new ResourceSpec("1", "1", "1Gi", "1Gi"),
            new Dictionary<string, string>(), DateTime.UtcNow));

        var servers = await harness.Service.ListServersAsync(CancellationToken.None);

        var pending = Assert.Single(servers);
        Assert.Equal("pulling-server", pending.Name);
        Assert.Equal(ServerStatus.Pending, pending.Status);
    }

    [Fact]
    public async Task GetServerAsync_Returns_Null_When_Container_Missing()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("missing-server");

        Assert.Null(await harness.Service.GetServerAsync("missing-server", CancellationToken.None));
    }

    [Fact]
    public async Task GetServerAsync_Returns_Null_For_A_Container_The_Dashboard_Does_Not_Manage()
    {
        // A user's unrelated container must never surface through the API.
        var harness = new Harness(engineReachable: true);
        var unmanaged = new ContainerInspectResponse
        {
            ID = "someones-db-id",
            Name = "/someones-db",
            Config = new Config { Labels = new Dictionary<string, string>() },
            State = new ContainerState { Status = "running", Running = true },
        };
        harness.Containers.Setup(c => c.InspectContainerAsync("someones-db", It.IsAny<CancellationToken>()))
            .ReturnsAsync(unmanaged);

        Assert.Null(await harness.Service.GetServerAsync("someones-db", CancellationToken.None));
    }

    [Fact]
    public async Task GetServerAsync_Falls_Back_To_The_Tracker_While_A_Deploy_Is_In_Flight()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("pulling-server");
        harness.Tracker.TryRegister(new DeployTracker.PendingDeploy(
            "pulling-server", "some/image:latest", "some/image:latest",
            Array.Empty<PortMapping>(), new ResourceSpec("1", "1", "1Gi", "1Gi"),
            new Dictionary<string, string>(), DateTime.UtcNow));

        var detail = await harness.Service.GetServerAsync("pulling-server", CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(ServerStatus.Pending, detail!.Status);
    }

    [Fact]
    public async Task GetServerAsync_Maps_A_Running_Managed_Container()
    {
        var harness = new Harness(engineReachable: true);
        harness.Containers.Setup(c => c.InspectContainerAsync("cs2-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagedContainer("cs2-a"));

        var detail = await harness.Service.GetServerAsync("cs2-a", CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("cs2-a", detail!.Name);
        Assert.Equal(ServerStatus.Running, detail.Status);
        Assert.Equal(1, detail.Replicas);
    }

    [Fact]
    public async Task GetConfigAsync_Throws_ClusterUnreachable_When_Engine_Down()
    {
        var harness = new Harness(engineReachable: false);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.GetConfigAsync("my-server", CancellationToken.None));
    }

    // --- deploy validation ---

    [Fact]
    public async Task DeployServerAsync_Rejects_Unknown_Game()
    {
        var harness = new Harness(engineReachable: true);
        var request = new DeployServerRequest("my-server", "some/unknown-image:latest", null, null);

        await Assert.ThrowsAsync<UnknownGameException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Validates_Name_Even_When_Engine_Down()
    {
        // Name validation must run before the engine is consulted (fail fast).
        var harness = new Harness(engineReachable: false);
        var request = new DeployServerRequest("BAD NAME", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Throws_ClusterUnreachable_When_Engine_Down()
    {
        var harness = new Harness(engineReachable: false);
        var request = new DeployServerRequest("my-server", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Rejects_A_Name_Any_Existing_Container_Claims()
    {
        var harness = new Harness(engineReachable: true);
        harness.Containers.Setup(c => c.InspectContainerAsync("my-server", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ManagedContainer("my-server"));
        var request = new DeployServerRequest("my-server", CuratedGameTemplates.Cs2.ImageTag, null, null);

        await Assert.ThrowsAsync<ServerAlreadyExistsException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Fails_Fast_With_A_Clear_Message_When_Secrets_Missing()
    {
        // The k8s deploy silently created a pod that crashed at start time with
        // CreateContainerConfigError; the Docker deploy checks the store up
        // front and tells the user what to configure.
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("my-cs2");
        harness.Secrets.Setup(s => s.GetValueAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var request = new DeployServerRequest("my-cs2", CuratedGameTemplates.Cs2.ImageTag, null, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));

        Assert.Contains("Missing secret value(s)", ex.Message);
        Assert.Contains("Setup screen", ex.Message);
    }

    [Fact]
    public async Task DeployServerAsync_Rejects_A_Second_Deploy_While_The_First_Is_Still_Pulling()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("my-cs2");
        harness.SetupAllSecretsConfigured();
        harness.SetupNoContainers();
        var request = new DeployServerRequest("my-cs2", CuratedGameTemplates.Cs2.ImageTag, null, null);

        var first = await harness.Service.DeployServerAsync(request, CancellationToken.None);
        Assert.Equal(ServerStatus.Pending, first.Status);

        await Assert.ThrowsAsync<ServerAlreadyExistsException>(
            () => harness.Service.DeployServerAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task DeployServerAsync_Picks_The_Java_Image_For_The_Modpacks_Resolved_Minecraft_Version()
    {
        // Live failure 2026-07-18: a 26.x-era pack on the java21 fallback image
        // crash-looped with UnsupportedClassVersionError. The deploy must ask
        // Modrinth which Minecraft version the pack pins and match the JVM.
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("mc-pack");
        harness.SetupAllSecretsConfigured();
        harness.SetupNoContainers();
        harness.MinecraftMetadata
            .Setup(m => m.TryGetModpackMinecraftVersionAsync("fabulously-optimized", It.IsAny<CancellationToken>()))
            .ReturnsAsync("26.2");
        var request = new DeployServerRequest(
            "mc-pack", CuratedGameTemplates.Minecraft.ImageTag, null,
            new Dictionary<string, string> { ["MODRINTH_MODPACK"] = "fabulously-optimized" });

        var detail = await harness.Service.DeployServerAsync(request, CancellationToken.None);

        Assert.Equal(MinecraftJavaImage.Java25, detail.Image);
    }

    [Fact]
    public async Task DeployServerAsync_Falls_Back_To_Java21_When_The_Modpack_Version_Is_Unknown()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("mc-pack");
        harness.SetupAllSecretsConfigured();
        harness.SetupNoContainers();
        // Loose mock: TryGetModpackMinecraftVersionAsync returns null (Modrinth down).
        var request = new DeployServerRequest(
            "mc-pack", CuratedGameTemplates.Minecraft.ImageTag, null,
            new Dictionary<string, string> { ["MODRINTH_MODPACK"] = "fabulously-optimized" });

        var detail = await harness.Service.DeployServerAsync(request, CancellationToken.None);

        Assert.Equal(MinecraftJavaImage.Java21, detail.Image);
    }

    // --- scale ---

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ScaleServerAsync_Rejects_Replica_Counts_Outside_0_And_1(int replicas)
    {
        var harness = new Harness(engineReachable: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ScaleServerAsync("my-server", replicas, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ScaleServerAsync_Accepts_0_And_1_As_Valid(int replicas)
    {
        // The guard runs before any engine call; with an unreachable engine a
        // valid value surfaces as ClusterUnreachable, proving it was accepted.
        var harness = new Harness(engineReachable: false);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.ScaleServerAsync("my-server", replicas, CancellationToken.None));
    }

    [Fact]
    public async Task ScaleServerAsync_Throws_NotFound_For_An_Unknown_Server()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("missing-server");

        await Assert.ThrowsAsync<ServerNotFoundException>(
            () => harness.Service.ScaleServerAsync("missing-server", 1, CancellationToken.None));
    }

    [Fact]
    public async Task ScaleServerAsync_Explains_When_The_Server_Is_Still_Deploying()
    {
        var harness = new Harness(engineReachable: true);
        harness.SetupInspectNotFound("pulling-server");
        harness.Tracker.TryRegister(new DeployTracker.PendingDeploy(
            "pulling-server", "some/image:latest", "some/image:latest",
            Array.Empty<PortMapping>(), new ResourceSpec("1", "1", "1Gi", "1Gi"),
            new Dictionary<string, string>(), DateTime.UtcNow));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ScaleServerAsync("pulling-server", 1, CancellationToken.None));

        Assert.Contains("still deploying", ex.Message);
    }

    [Fact]
    public async Task ScaleServerAsync_Refuses_To_Touch_An_Unmanaged_Container()
    {
        var harness = new Harness(engineReachable: true);
        var unmanaged = new ContainerInspectResponse
        {
            ID = "someones-db-id",
            Name = "/someones-db",
            Config = new Config { Labels = new Dictionary<string, string>() },
            State = new ContainerState { Status = "running", Running = true },
        };
        harness.Containers.Setup(c => c.InspectContainerAsync("someones-db", It.IsAny<CancellationToken>()))
            .ReturnsAsync(unmanaged);

        await Assert.ThrowsAsync<ServerNotFoundException>(
            () => harness.Service.ScaleServerAsync("someones-db", 0, CancellationToken.None));
    }

    // --- delete ---

    [Fact]
    public async Task DeleteServerAsync_Throws_ClusterUnreachable_When_Engine_Down()
    {
        var harness = new Harness(engineReachable: false);

        await Assert.ThrowsAsync<ClusterUnreachableException>(
            () => harness.Service.DeleteServerAsync("my-server", deleteData: true, CancellationToken.None));
    }

    [Fact]
    public async Task DeleteServerAsync_Is_Idempotent_When_Container_And_Volume_Are_Already_Gone()
    {
        var harness = new Harness(engineReachable: true);
        harness.Containers.Setup(c => c.RemoveContainerAsync(
                "my-server", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no such container"));
        harness.Volumes.Setup(v => v.RemoveAsync(
                "my-server-data", It.IsAny<bool?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerApiException(HttpStatusCode.NotFound, "no such volume"));

        var exception = await Record.ExceptionAsync(
            () => harness.Service.DeleteServerAsync("my-server", deleteData: true, CancellationToken.None));

        Assert.Null(exception);
        harness.LastActive.Verify(
            s => s.RemoveAsync("my-server", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteServerAsync_Keeps_The_Volume_When_DeleteData_Is_False()
    {
        var harness = new Harness(engineReachable: true);

        await harness.Service.DeleteServerAsync("my-server", deleteData: false, CancellationToken.None);

        harness.Volumes.Verify(v => v.RemoveAsync(
            It.IsAny<string>(), It.IsAny<bool?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteServerAsync_Clears_A_Stuck_Pending_Deploy()
    {
        var harness = new Harness(engineReachable: true);
        harness.Tracker.TryRegister(new DeployTracker.PendingDeploy(
            "stuck-server", "some/image:latest", "some/image:latest",
            Array.Empty<PortMapping>(), new ResourceSpec("1", "1", "1Gi", "1Gi"),
            new Dictionary<string, string>(), DateTime.UtcNow));

        await harness.Service.DeleteServerAsync("stuck-server", deleteData: true, CancellationToken.None);

        Assert.Null(harness.Tracker.Get("stuck-server"));
    }

    // --- image ref parsing ---

    [Theory]
    [InlineData("itzg/minecraft-server:java21", "itzg/minecraft-server", "java21")]
    [InlineData("nginx:latest", "nginx", "latest")]
    [InlineData("nginx", "nginx", "latest")]
    [InlineData("registry.example.com:5000/game/server:v2", "registry.example.com:5000/game/server", "v2")]
    [InlineData("registry.example.com:5000/game/server", "registry.example.com:5000/game/server", "latest")]
    public void SplitImageRef_Separates_Image_From_Tag(string imageRef, string expectedImage, string expectedTag)
    {
        // A registry port must not be mistaken for a tag separator.
        Assert.Equal((expectedImage, expectedTag), DockerService.SplitImageRef(imageRef));
    }
}
