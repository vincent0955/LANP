using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Services;

/// <summary>
/// Round-trips the runtime-adjustable scheduled-backup settings store (WS1)
/// against a temp directory, including default seeding and value clamping.
/// </summary>
public sealed class BackupSettingsStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"gd-backupsettings-tests-{Guid.NewGuid():N}");

    private BackupSettingsStore CreateStore(BackupOptions? backup = null) =>
        new(
            Options.Create(new DashboardOptions { DataDirectory = _tempDir, Backup = backup ?? new BackupOptions() }),
            NullLogger<BackupSettingsStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_Returns_Option_Defaults_When_Nothing_Saved()
    {
        var store = CreateStore(new BackupOptions { Enabled = true, IntervalMinutes = 120, RetentionCount = 3 });

        var settings = await store.GetAsync(CancellationToken.None);

        Assert.True(settings.Enabled);
        Assert.Equal(120, settings.IntervalMinutes);
        Assert.Equal(3, settings.RetentionCount);
    }

    [Fact]
    public async Task SetAsync_Then_GetAsync_Round_Trips()
    {
        var store = CreateStore();

        await store.SetAsync(new BackupSettings(true, 45, 10), CancellationToken.None);

        var settings = await store.GetAsync(CancellationToken.None);
        Assert.Equal(new BackupSettings(true, 45, 10), settings);
    }

    [Fact]
    public async Task Settings_Survive_A_New_Store_Instance()
    {
        await CreateStore().SetAsync(new BackupSettings(true, 30, 7), CancellationToken.None);

        Assert.Equal(new BackupSettings(true, 30, 7), await CreateStore().GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_Clamps_Interval_And_Retention_To_Sane_Bounds()
    {
        var store = CreateStore();

        await store.SetAsync(new BackupSettings(true, 0, -5), CancellationToken.None);

        var settings = await store.GetAsync(CancellationToken.None);
        Assert.Equal(1, settings.IntervalMinutes);
        Assert.Equal(0, settings.RetentionCount);
    }

    [Fact]
    public async Task A_Corrupt_File_Falls_Back_To_Defaults()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "backup-settings.json"), "{not json");
        var store = CreateStore(new BackupOptions { Enabled = false, IntervalMinutes = 360, RetentionCount = 5 });

        var settings = await store.GetAsync(CancellationToken.None);

        Assert.Equal(new BackupSettings(false, 360, 5), settings);
    }
}
