using Docker.DotNet;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Covers GetSetupStatusAsync's detection logic (Req 13.2) and the secrets
/// contract (Req 13.3: write-only listing, per-key reveal, template-key
/// protection) against the local secrets store. Secrets no longer live on the
/// cluster, so — unlike the k8s era — every secrets operation works with the
/// engine down.
/// </summary>
public class SetupStatusTests
{
    private static (DockerService Service, Mock<ISecretsStore> Secrets) Create(bool engineReachable)
    {
        var factory = new Mock<IDockerClientFactory>();
        if (engineReachable)
        {
            var client = new Mock<IDockerClient>();
            client.SetupGet(c => c.System).Returns(Mock.Of<ISystemOperations>());
            factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((client.Object, null));
        }
        else
        {
            factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));
        }

        var secrets = new Mock<ISecretsStore>();
        secrets.Setup(s => s.GetKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        var metrics = new Mock<IMetricsService>();
        metrics.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetricsSnapshot(false, null, Array.Empty<PodMetricsInfo>(), "unavailable"));

        var service = new DockerService(
            factory.Object,
            new ContainerSpecBuilder(),
            new DeployTracker(),
            secrets.Object,
            Mock.Of<ILastActiveStore>(),
            Mock.Of<ITcpReadinessProber>(),
            Mock.Of<IRconService>(),
            metrics.Object,
            Mock.Of<IMinecraftMetadataService>(),
            Options.Create(new DashboardOptions()),
            NullLogger<DockerService>.Instance);

        return (service, secrets);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_Engine_Unreachable_With_A_Warning()
    {
        var (service, _) = Create(engineReachable: false);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.False(status.DockerEngineReachable);
        // docker stats is built into the engine: metrics are available exactly
        // when the engine is (the k8s metrics-server middle state is gone).
        Assert.False(status.MetricsAvailable);
        Assert.False(status.SecretsConfigured);
        Assert.NotEmpty(status.Warnings);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Never_Throws_When_Engine_Unreachable()
    {
        var (service, _) = Create(engineReachable: false);

        var exception = await Record.ExceptionAsync(() => service.GetSetupStatusAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Reports_All_Green_When_Engine_Up_And_Secrets_Configured()
    {
        var (service, secrets) = Create(engineReachable: true);
        secrets.Setup(s => s.GetKeysAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "MY_CUSTOM_KEY", "SRCDS_TOKEN" });

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.True(status.DockerEngineReachable);
        Assert.True(status.MetricsAvailable);
        Assert.True(status.SecretsConfigured);
        // Key names (and only key names) are surfaced, sorted by the store, so
        // the UI can list configured secrets — including custom ones (Req 13.3).
        Assert.Equal(new[] { "MY_CUSTOM_KEY", "SRCDS_TOKEN" }, status.ConfiguredSecretKeys);
        Assert.Empty(status.Warnings);
    }

    [Fact]
    public async Task GetSetupStatusAsync_Warns_But_Does_Not_Fail_When_No_Secrets_Configured()
    {
        var (service, _) = Create(engineReachable: true);

        var status = await service.GetSetupStatusAsync(CancellationToken.None);

        Assert.True(status.DockerEngineReachable);
        Assert.False(status.SecretsConfigured);
        Assert.Empty(status.ConfiguredSecretKeys);
        Assert.Contains(status.Warnings, w => w.Contains("secrets", StringComparison.OrdinalIgnoreCase));
    }

    // --- secrets contract (local store; engine state is irrelevant) ---

    [Fact]
    public async Task GetSecretValueAsync_Returns_The_Stored_Value()
    {
        var (service, secrets) = Create(engineReachable: false);
        secrets.Setup(s => s.GetValueAsync("MY_KEY", It.IsAny<CancellationToken>()))
            .ReturnsAsync("hunter2");

        Assert.Equal("hunter2", await service.GetSecretValueAsync("MY_KEY", CancellationToken.None));
    }

    [Fact]
    public async Task GetSecretValueAsync_Throws_KeyNotFound_When_Key_Missing()
    {
        var (service, secrets) = Create(engineReachable: false);
        secrets.Setup(s => s.GetValueAsync("NOPE", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetSecretValueAsync("NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task SetSecretsAsync_Works_With_The_Engine_Down()
    {
        // Secrets are a local file now — setup must be possible before the
        // runtime is even installed (the whole point of the Setup screen).
        var (service, secrets) = Create(engineReachable: false);
        var values = new Dictionary<string, string> { ["FOO"] = "bar" };

        await service.SetSecretsAsync(values, CancellationToken.None);

        secrets.Verify(s => s.SetAsync(values, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteSecretKeyAsync_Removes_A_Custom_Key()
    {
        var (service, secrets) = Create(engineReachable: false);
        secrets.Setup(s => s.DeleteAsync("CUSTOM_KEY", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await service.DeleteSecretKeyAsync("CUSTOM_KEY", CancellationToken.None);

        secrets.Verify(s => s.DeleteAsync("CUSTOM_KEY", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteSecretKeyAsync_Throws_KeyNotFound_When_Key_Missing()
    {
        var (service, secrets) = Create(engineReachable: false);
        secrets.Setup(s => s.DeleteAsync("NOPE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.DeleteSecretKeyAsync("NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteSecretKeyAsync_Rejects_Template_Referenced_Keys()
    {
        var (service, secrets) = Create(engineReachable: false);

        // SRCDS_TOKEN is wired into the CS2 template's secretKeyRefs; deleting
        // it must be refused (409) even though it exists in the store.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteSecretKeyAsync("SRCDS_TOKEN", CancellationToken.None));

        secrets.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
