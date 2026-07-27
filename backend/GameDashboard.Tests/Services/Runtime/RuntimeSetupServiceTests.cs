using Docker.DotNet;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services.Docker;
using GameDashboard.Api.Services.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace GameDashboard.Tests.Services.Runtime;

/// <summary>
/// Covers RuntimeSetupService's status derivation and .wslconfig application
/// against a mocked wsl.exe. The install pipeline itself (UAC prompts, distro
/// download, VM boot) requires a real Windows box being modified and is
/// exercised manually — these tests pin the decision logic around it.
/// Windows-only paths are guarded so the suite still passes on Linux.
/// </summary>
public sealed class RuntimeSetupServiceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"gd-runtime-tests-{Guid.NewGuid():N}");

    private readonly Mock<IWslRunner> _wsl = new();
    private readonly Mock<IDockerClientFactory> _factory = new();
    private readonly Mock<IRuntimeModeStore> _modeStore = new();

    public RuntimeSetupServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _factory.Setup(f => f.TryGetClientAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(((IDockerClient?)null, "engine unreachable"));
        // Default across the suite: the bundled runtime is the engine, so the
        // WSL machinery below is live. Docker Desktop mode is opted into per-test.
        _modeStore.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RuntimeMode.Bundled);
    }

    private void UseDockerDesktopMode() =>
        _modeStore.Setup(m => m.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(RuntimeMode.DockerDesktop);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private RuntimeSetupService CreateService() =>
        new(
            _wsl.Object,
            _factory.Object,
            Mock.Of<IHttpClientFactory>(),
            _modeStore.Object,
            Options.Create(new DashboardOptions()),
            NullLogger<RuntimeSetupService>.Instance,
            wslConfigPath: Path.Combine(_tempDir, ".wslconfig"));

    private void SetupWsl(bool installed, params string[] distros)
    {
        _wsl.Setup(w => w.RunAsync("--status", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult(installed ? 0 : 1, installed ? "Default Version: 2" : ""));
        _wsl.Setup(w => w.RunAsync("--list --quiet", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult(0, string.Join("\r\n", distros)));
    }

    [Fact]
    public async Task Status_Reports_Wsl_Missing_When_The_Probe_Fails()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWsl(installed: false);

        var status = await CreateService().GetStatusAsync(CancellationToken.None);

        Assert.Equal("windows", status.Platform);
        Assert.False(status.WslInstalled);
        Assert.False(status.DistroImported);
        Assert.False(status.EngineReachable);
        Assert.Equal(RuntimePhase.Idle, status.Phase);
    }

    [Fact]
    public async Task Status_Detects_The_Imported_Distro_By_Name()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWsl(installed: true, "Ubuntu", "gamedashboard", "docker-desktop");

        var status = await CreateService().GetStatusAsync(CancellationToken.None);

        Assert.True(status.WslInstalled);
        Assert.True(status.DistroImported);
    }

    [Fact]
    public async Task Status_Does_Not_Mistake_Other_Distros_For_Ours()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWsl(installed: true, "Ubuntu", "docker-desktop");

        var status = await CreateService().GetStatusAsync(CancellationToken.None);

        Assert.True(status.WslInstalled);
        Assert.False(status.DistroImported);
    }

    [Fact]
    public async Task Status_Includes_Copyable_Firewall_Commands_Covering_The_Port_Window()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWsl(installed: true);

        var status = await CreateService().GetStatusAsync(CancellationToken.None);

        Assert.Contains(status.FirewallCommands, c =>
            c.Contains($"{ContainerSpecBuilder.HostPortRangeStart}-{ContainerSpecBuilder.HostPortRangeEnd}"));
        Assert.Contains(status.FirewallCommands, c => c.Contains("Set-NetFirewallHyperVVMSetting"));
    }

    [Fact]
    public async Task Status_Never_Throws_When_Wsl_Exe_Is_Broken()
    {
        if (!OperatingSystem.IsWindows()) return;
        _wsl.Setup(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("wsl.exe not found"));

        var status = await CreateService().GetStatusAsync(CancellationToken.None);

        Assert.False(status.WslInstalled);
        Assert.False(status.DistroImported);
    }

    [Fact]
    public async Task ApplyMirroredNetworking_Creates_The_Config_And_Requires_A_Restart()
    {
        var service = CreateService();

        var (applied, shutdownRequired) = await service.ApplyMirroredNetworkingAsync(CancellationToken.None);

        Assert.True(applied);
        Assert.True(shutdownRequired);
        var written = await File.ReadAllTextAsync(Path.Combine(_tempDir, ".wslconfig"));
        Assert.True(WslConfigEditor.HasMirroredNetworking(written));
    }

    [Fact]
    public async Task ApplyMirroredNetworking_Is_A_NoOp_When_Already_Configured()
    {
        var configPath = Path.Combine(_tempDir, ".wslconfig");
        await File.WriteAllTextAsync(configPath, "[wsl2]\nnetworkingMode=mirrored\n");
        var service = CreateService();

        var (applied, shutdownRequired) = await service.ApplyMirroredNetworkingAsync(CancellationToken.None);

        Assert.False(applied);
        Assert.False(shutdownRequired);
    }

    [Fact]
    public async Task ApplyMirroredNetworking_Preserves_Unrelated_Settings()
    {
        var configPath = Path.Combine(_tempDir, ".wslconfig");
        await File.WriteAllTextAsync(configPath, "[wsl2]\nmemory=8GB\n\n[experimental]\nsparseVhd=true\n");
        var service = CreateService();

        await service.ApplyMirroredNetworkingAsync(CancellationToken.None);

        var written = await File.ReadAllTextAsync(configPath);
        Assert.Contains("memory=8GB", written);
        Assert.Contains("sparseVhd=true", written);
        Assert.True(WslConfigEditor.HasMirroredNetworking(written));
    }

    // --- keep-alive / auto-start ---
    // WSL kills a distro's VM (daemons included) shortly after the last
    // attached wsl.exe session exits — verified empirically, and NOT
    // preventable via vmIdleTimeout (-1 and int.MaxValue both failed). The
    // detached keep-alive session is therefore load-bearing: without it every
    // game server dies ~60s after the install flow finishes.

    private RuntimeSetupService CreateService(bool bundledEngineUp) =>
        new(
            _wsl.Object,
            _factory.Object,
            Mock.Of<IHttpClientFactory>(),
            _modeStore.Object,
            Options.Create(new DashboardOptions()),
            NullLogger<RuntimeSetupService>.Instance,
            wslConfigPath: Path.Combine(_tempDir, ".wslconfig"),
            bundledEngineProbe: _ => Task.FromResult(bundledEngineUp),
            engineStartTimeout: TimeSpan.Zero); // a probe that will never pass shouldn't stall the test

    [Fact]
    public async Task AutoStart_Pins_An_Already_Running_Bundled_Engine_With_A_KeepAlive()
    {
        if (!OperatingSystem.IsWindows()) return;
        var service = CreateService(bundledEngineUp: true);

        await service.AutoStartAsync(CancellationToken.None);

        _wsl.Verify(w => w.StartDetached(It.Is<string>(args =>
            args.Contains("gamedashboard") && args.Contains("flock"))), Times.Once);
        // Engine already up: no boot attempt.
        _wsl.Verify(w => w.RunAsync(It.Is<string>(a => a.Contains("start-dockerd")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AutoStart_Boots_The_Imported_Distro_When_The_Engine_Is_Down()
    {
        if (!OperatingSystem.IsWindows()) return;
        SetupWsl(installed: true, "gamedashboard");
        // Engine down on both probes: the initial quick probe and the
        // post-boot readiness wait (the wait's failure path is exercised —
        // what matters here is that the boot was attempted).
        var service = CreateService(bundledEngineUp: false);

        await service.AutoStartAsync(CancellationToken.None);

        _wsl.Verify(w => w.RunAsync(It.Is<string>(a => a.Contains("start-dockerd")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoStart_Does_Nothing_On_A_Machine_Without_The_Bundled_Runtime()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Docker Desktop users who never installed our runtime: touching WSL
        // here would boot a distro that doesn't exist / isn't ours.
        SetupWsl(installed: true, "Ubuntu", "docker-desktop");
        var service = CreateService(bundledEngineUp: false);

        await service.AutoStartAsync(CancellationToken.None);

        _wsl.Verify(w => w.StartDetached(It.IsAny<string>()), Times.Never);
        _wsl.Verify(w => w.RunAsync(It.Is<string>(a => a.Contains("start-dockerd")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Docker Desktop mode ---
    // The point of the mode is that launching the app costs no second VM, so
    // the bundled machinery must stay completely dormant.

    [Fact]
    public async Task AutoStart_Skips_The_Bundled_Runtime_Entirely_In_DockerDesktop_Mode()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Everything the Bundled path keys off is true here — the distro IS
        // imported and the engine is down — so only the mode can hold it back.
        SetupWsl(installed: true, "gamedashboard");
        UseDockerDesktopMode();
        var service = CreateService(bundledEngineUp: false);

        await service.AutoStartAsync(CancellationToken.None);

        _wsl.Verify(w => w.StartDetached(It.IsAny<string>()), Times.Never);
        _wsl.Verify(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StopRuntime_Leaves_Docker_Desktop_Running_On_Exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Quitting the dashboard must not take down an engine the user runs
        // for everything else on the machine.
        _wsl.Setup(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult(0, ""));
        UseDockerDesktopMode();
        var service = CreateService(bundledEngineUp: false);

        await service.StopRuntimeAsync(CancellationToken.None);

        _wsl.Verify(w => w.RunAsync(It.Is<string>(a => a.Contains("--terminate")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Status_In_DockerDesktop_Mode_Reports_The_Engine_Without_Probing_Wsl()
    {
        if (!OperatingSystem.IsWindows()) return;
        UseDockerDesktopMode();
        var service = CreateService();

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(RuntimeMode.DockerDesktop, status.Mode);
        Assert.False(status.EngineReachable);
        // The Setup card must not nag about installing WSL in this mode; the
        // actionable message is "start Docker Desktop".
        Assert.Contains("Docker Desktop", status.Error);
        _wsl.Verify(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StopRuntime_Terminates_The_Distro()
    {
        if (!OperatingSystem.IsWindows()) return;
        _wsl.Setup(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WslResult(0, ""));
        var service = CreateService(bundledEngineUp: false);

        await service.StopRuntimeAsync(CancellationToken.None);

        _wsl.Verify(w => w.RunAsync(It.Is<string>(a => a.Contains("--terminate") && a.Contains("gamedashboard")),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopRuntime_Never_Throws_Even_When_Wsl_Is_Broken()
    {
        if (!OperatingSystem.IsWindows()) return;
        // Exit must always complete — a distro that was never imported or a
        // broken wsl.exe cannot be allowed to wedge the app's quit path.
        _wsl.Setup(w => w.RunAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("wsl.exe not found"));
        var service = CreateService(bundledEngineUp: false);

        var exception = await Record.ExceptionAsync(
            () => service.StopRuntimeAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public void TryStartInstall_Rejects_A_Second_Concurrent_Install()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The install task immediately probes wsl --status; block it so the
        // first install is guaranteed to still be "running" for the second call.
        var block = new TaskCompletionSource<WslResult>();
        _wsl.Setup(w => w.RunAsync("--status", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(block.Task);
        var service = CreateService();

        Assert.True(service.TryStartInstall());
        Assert.False(service.TryStartInstall());

        block.SetResult(new WslResult(1, "")); // let the background task finish
    }
}
