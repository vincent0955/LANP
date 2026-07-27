using System.Text.Json;
using System.Text.Json.Serialization;
using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// Persists the user's container-engine choice (<see cref="RuntimeMode"/>)
/// across app restarts. Same shape as <see cref="Services.IBackupSettingsStore"/>
/// and for the same reason: <c>IOptions</c> is bound once at startup, but this
/// knob is user-editable at runtime, so the mutable value lives in a small JSON
/// file next to the other local state.
///
/// Reads are hot-path (every Docker client probe consults the mode), so the
/// value is cached in memory after the first read and only re-read from disk
/// when this process is the one that wrote it.
/// </summary>
public interface IRuntimeModeStore
{
    Task<RuntimeMode> GetAsync(CancellationToken ct);

    Task SetAsync(RuntimeMode mode, CancellationToken ct);
}

public sealed class RuntimeModeStore : IRuntimeModeStore
{
    private readonly string _filePath;
    private readonly RuntimeMode _default;
    private readonly ILogger<RuntimeModeStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private RuntimeMode? _cached;

    public RuntimeModeStore(IOptions<DashboardOptions> options, ILogger<RuntimeModeStore> logger)
    {
        _filePath = Path.Combine(options.Value.ResolvedDataDirectory, "runtime-mode.json");

        // An explicit endpoint override means the operator has already picked an
        // engine by hand; defaulting to Bundled would have us install a WSL
        // distro that then goes unused.
        _default = string.IsNullOrWhiteSpace(options.Value.DockerEndpoint)
            ? RuntimeMode.Bundled
            : RuntimeMode.DockerDesktop;

        _logger = logger;
    }

    public async Task<RuntimeMode> GetAsync(CancellationToken ct)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } raced)
            {
                return raced;
            }

            _cached = await ReadAsync(ct);
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RuntimeMode> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return _default;
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var stored = await JsonSerializer.DeserializeAsync<StoredMode>(stream, JsonOptions, ct);
            return stored?.Mode ?? _default;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runtime mode at {Path} could not be read; using {Default}.", _filePath, _default);
            return _default;
        }
    }

    public async Task SetAsync(RuntimeMode mode, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllBytesAsync(
                _filePath, JsonSerializer.SerializeToUtf8Bytes(new StoredMode(mode), JsonOptions), ct);
            _cached = mode;
            _logger.LogInformation("Container engine set to {Mode}; takes effect after an app restart.", mode);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Written as a name, not an ordinal, so the file survives enum reordering.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record StoredMode(RuntimeMode Mode);
}
