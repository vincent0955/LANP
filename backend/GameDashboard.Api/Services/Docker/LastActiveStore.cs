using System.Text.Json;
using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Replaces the last-active Deployment annotation: a plain JSON file mapping
/// server name → last-active timestamp, next to the secrets store. Not secret,
/// so unencrypted. Auto-scale reads/writes it every evaluation.
/// </summary>
public interface ILastActiveStore
{
    Task<DateTimeOffset?> GetAsync(string name, CancellationToken ct);

    Task SetAsync(string name, DateTimeOffset timestamp, CancellationToken ct);

    /// <summary>Drops the entry when a server is deleted (keeps the file tidy).</summary>
    Task RemoveAsync(string name, CancellationToken ct);
}

public sealed class LastActiveStore : ILastActiveStore
{
    private readonly string _filePath;
    private readonly ILogger<LastActiveStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LastActiveStore(IOptions<DashboardOptions> options, ILogger<LastActiveStore> logger)
    {
        _filePath = Path.Combine(options.Value.ResolvedDataDirectory, "last-active.json");
        _logger = logger;
    }

    public async Task<DateTimeOffset?> GetAsync(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadUnlockedAsync(ct);
            return data.TryGetValue(name, out var value) ? value : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string name, DateTimeOffset timestamp, CancellationToken ct)
    {
        await MutateAsync(data => data[name] = timestamp, ct);
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        await MutateAsync(data => data.Remove(name), ct);
    }

    private async Task MutateAsync(Action<Dictionary<string, DateTimeOffset>> mutate, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadUnlockedAsync(ct);
            mutate(data);
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllBytesAsync(_filePath, JsonSerializer.SerializeToUtf8Bytes(data), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, DateTimeOffset>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, DateTimeOffset>>(stream, cancellationToken: ct)
                ?? new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Last-active store at {Path} could not be read; starting fresh.", _filePath);
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }
}
