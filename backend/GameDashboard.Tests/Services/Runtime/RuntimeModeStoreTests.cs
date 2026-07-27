using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Services.Runtime;

/// <summary>
/// The engine choice has to survive an app restart — that is the whole
/// requirement ("remember the setting so every startup honours it"), and the
/// only thing standing between the user and a WSL VM booting behind their back.
/// </summary>
public sealed class RuntimeModeStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"gd-mode-tests-{Guid.NewGuid():N}");

    public RuntimeModeStoreTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private RuntimeModeStore CreateStore(string? dockerEndpoint = null) =>
        new(
            Options.Create(new DashboardOptions
            {
                DataDirectory = _tempDir,
                DockerEndpoint = dockerEndpoint,
            }),
            NullLogger<RuntimeModeStore>.Instance);

    [Fact]
    public async Task Defaults_To_The_Bundled_Runtime()
    {
        Assert.Equal(RuntimeMode.Bundled, await CreateStore().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_Saved_Choice_Survives_A_Restart()
    {
        await CreateStore().SetAsync(RuntimeMode.DockerDesktop, CancellationToken.None);

        // A brand-new instance stands in for the next app launch — no shared
        // in-memory cache, so this only passes if the value reached disk.
        Assert.Equal(RuntimeMode.DockerDesktop, await CreateStore().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Switching_Back_To_Bundled_Persists_Too()
    {
        await CreateStore().SetAsync(RuntimeMode.DockerDesktop, CancellationToken.None);
        await CreateStore().SetAsync(RuntimeMode.Bundled, CancellationToken.None);

        Assert.Equal(RuntimeMode.Bundled, await CreateStore().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_Explicit_Endpoint_Override_Defaults_To_Not_Installing_Our_Runtime()
    {
        // The operator already pointed us at an engine; installing a bundled
        // distro that would then go unused is pure waste.
        var store = CreateStore(dockerEndpoint: "tcp://10.0.0.5:2375");

        Assert.Equal(RuntimeMode.DockerDesktop, await store.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_Corrupt_File_Falls_Back_To_The_Default_Instead_Of_Throwing()
    {
        // A half-written file must not stop the app from booting.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "runtime-mode.json"), "{ not json");

        Assert.Equal(RuntimeMode.Bundled, await CreateStore().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_Mode_Is_Stored_By_Name_So_Reordering_The_Enum_Cannot_Flip_It()
    {
        await CreateStore().SetAsync(RuntimeMode.DockerDesktop, CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "runtime-mode.json"));
        Assert.Contains("DockerDesktop", json);
    }
}
