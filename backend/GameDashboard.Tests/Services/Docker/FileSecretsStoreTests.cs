using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Services.Docker;

/// <summary>
/// Round-trips the local secrets store against a temp directory. On Windows
/// the payload is DPAPI-encrypted (CurrentUser scope — works in a test process
/// too); on Linux it is a 0600 plain file. The store's contract (merge writes,
/// sorted key listing, corrupt file treated as empty) is platform-independent.
/// </summary>
public sealed class FileSecretsStoreTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"gd-secrets-tests-{Guid.NewGuid():N}");

    private FileSecretsStore CreateStore() =>
        new(
            Options.Create(new DashboardOptions { DataDirectory = _tempDir }),
            NullLogger<FileSecretsStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetKeysAsync_Is_Empty_Before_Anything_Is_Stored()
    {
        var store = CreateStore();

        Assert.Empty(await store.GetKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_Then_GetValueAsync_Round_Trips()
    {
        var store = CreateStore();

        await store.SetAsync(new Dictionary<string, string> { ["MY_KEY"] = "hunter2" }, CancellationToken.None);

        Assert.Equal("hunter2", await store.GetValueAsync("MY_KEY", CancellationToken.None));
    }

    [Fact]
    public async Task Values_Survive_A_New_Store_Instance()
    {
        await CreateStore().SetAsync(
            new Dictionary<string, string> { ["MY_KEY"] = "hunter2" }, CancellationToken.None);

        // A fresh instance (≈ backend restart) reads the same file.
        Assert.Equal("hunter2", await CreateStore().GetValueAsync("MY_KEY", CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_Merges_Keeping_Keys_Not_Mentioned()
    {
        var store = CreateStore();
        await store.SetAsync(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, CancellationToken.None);

        await store.SetAsync(new Dictionary<string, string> { ["B"] = "changed" }, CancellationToken.None);

        Assert.Equal("1", await store.GetValueAsync("A", CancellationToken.None));
        Assert.Equal("changed", await store.GetValueAsync("B", CancellationToken.None));
    }

    [Fact]
    public async Task GetKeysAsync_Returns_Sorted_Key_Names()
    {
        var store = CreateStore();
        await store.SetAsync(new Dictionary<string, string>
        {
            ["SRCDS_TOKEN"] = "x",
            ["MY_CUSTOM_KEY"] = "y",
        }, CancellationToken.None);

        Assert.Equal(new[] { "MY_CUSTOM_KEY", "SRCDS_TOKEN" },
            await store.GetKeysAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetValueAsync_Returns_Null_For_Unknown_Key()
    {
        var store = CreateStore();

        Assert.Null(await store.GetValueAsync("NOPE", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_Removes_The_Key_And_Reports_Whether_It_Existed()
    {
        var store = CreateStore();
        await store.SetAsync(new Dictionary<string, string> { ["A"] = "1" }, CancellationToken.None);

        Assert.True(await store.DeleteAsync("A", CancellationToken.None));
        Assert.Null(await store.GetValueAsync("A", CancellationToken.None));
        Assert.False(await store.DeleteAsync("A", CancellationToken.None));
    }

    [Fact]
    public async Task Secret_Values_Are_Not_Stored_In_Plaintext_On_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Linux relies on 0600 file permissions instead of encryption.
        }

        var store = CreateStore();
        await store.SetAsync(new Dictionary<string, string> { ["MY_KEY"] = "hunter2" }, CancellationToken.None);

        var raw = await File.ReadAllTextAsync(Path.Combine(_tempDir, "secrets.bin"));
        Assert.DoesNotContain("hunter2", raw);
    }

    [Fact]
    public async Task A_Corrupt_Store_File_Is_Treated_As_Empty_Not_Fatal()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllBytesAsync(
            Path.Combine(_tempDir, "secrets.bin"), new byte[] { 1, 2, 3, 4, 5 });
        var store = CreateStore();

        // Must not throw — the user just re-enters secrets on the Setup screen.
        Assert.Empty(await store.GetKeysAsync(CancellationToken.None));

        // And the store must be writable again afterwards.
        await store.SetAsync(new Dictionary<string, string> { ["A"] = "1" }, CancellationToken.None);
        Assert.Equal("1", await store.GetValueAsync("A", CancellationToken.None));
    }
}
