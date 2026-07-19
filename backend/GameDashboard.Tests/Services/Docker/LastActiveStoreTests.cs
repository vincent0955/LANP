using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Round-trips the last-active file store (the replacement for the k8s
/// last-active Deployment annotation) against a temp directory.
/// </summary>
public sealed class LastActiveStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"gd-lastactive-tests-{Guid.NewGuid():N}");

    private LastActiveStore CreateStore() =>
        new(
            Options.Create(new DashboardOptions { DataDirectory = _tempDir }),
            NullLogger<LastActiveStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_Returns_Null_When_Nothing_Recorded()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync("my-server", CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_Then_GetAsync_Round_Trips_The_Timestamp()
    {
        var store = CreateStore();
        var timestamp = new DateTimeOffset(2026, 7, 16, 12, 30, 0, TimeSpan.Zero);

        await store.SetAsync("my-server", timestamp, CancellationToken.None);

        Assert.Equal(timestamp, await store.GetAsync("my-server", CancellationToken.None));
    }

    [Fact]
    public async Task Timestamps_Survive_A_New_Store_Instance()
    {
        var timestamp = DateTimeOffset.UtcNow;
        await CreateStore().SetAsync("my-server", timestamp, CancellationToken.None);

        Assert.Equal(timestamp, await CreateStore().GetAsync("my-server", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_Drops_The_Entry()
    {
        var store = CreateStore();
        await store.SetAsync("my-server", DateTimeOffset.UtcNow, CancellationToken.None);

        await store.RemoveAsync("my-server", CancellationToken.None);

        Assert.Null(await store.GetAsync("my-server", CancellationToken.None));
    }

    [Fact]
    public async Task RemoveAsync_For_An_Unknown_Server_Is_A_NoOp()
    {
        var store = CreateStore();

        await store.RemoveAsync("never-recorded", CancellationToken.None);

        Assert.Null(await store.GetAsync("never-recorded", CancellationToken.None));
    }

    [Fact]
    public async Task A_Corrupt_File_Starts_Fresh_Not_Fatal()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "last-active.json"), "{not json");
        var store = CreateStore();

        Assert.Null(await store.GetAsync("my-server", CancellationToken.None));

        await store.SetAsync("my-server", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.NotNull(await store.GetAsync("my-server", CancellationToken.None));
    }
}
