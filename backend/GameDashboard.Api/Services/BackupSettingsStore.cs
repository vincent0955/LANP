using System.Text.Json;
using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// Runtime-adjustable scheduled-backup settings (WS1). The scheduler's cadence
/// is user-editable from the Settings screen, but <c>IOptions</c> is bound once
/// at startup, so the mutable slice lives in a small JSON file next to the other
/// local state and is re-read by <see cref="BackupSchedulerService"/> each cycle.
/// Non-mutable knobs (directory, helper image) stay in <see cref="BackupOptions"/>.
/// </summary>
public interface IBackupSettingsStore
{
    Task<BackupSettings> GetAsync(CancellationToken ct);
    Task SetAsync(BackupSettings settings, CancellationToken ct);
}

/// <summary>User-editable scheduled-backup knobs.</summary>
public sealed record BackupSettings(bool Enabled, int IntervalMinutes, int RetentionCount);

public sealed class BackupSettingsStore : IBackupSettingsStore
{
    private readonly string _filePath;
    private readonly BackupSettings _defaults;
    private readonly ILogger<BackupSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupSettingsStore(IOptions<DashboardOptions> options, ILogger<BackupSettingsStore> logger)
    {
        var backup = options.Value.Backup;
        _filePath = Path.Combine(options.Value.ResolvedDataDirectory, "backup-settings.json");
        _defaults = new BackupSettings(backup.Enabled, backup.IntervalMinutes, backup.RetentionCount);
        _logger = logger;
    }

    public async Task<BackupSettings> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                return _defaults;
            }

            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<BackupSettings>(stream, cancellationToken: ct) ?? _defaults;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backup settings at {Path} could not be read; using defaults.", _filePath);
            return _defaults;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(BackupSettings settings, CancellationToken ct)
    {
        // Clamp to sane bounds so a bad value can't wedge the scheduler.
        var normalized = settings with
        {
            IntervalMinutes = Math.Max(1, settings.IntervalMinutes),
            RetentionCount = Math.Max(0, settings.RetentionCount),
        };

        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await File.WriteAllBytesAsync(_filePath, JsonSerializer.SerializeToUtf8Bytes(normalized), ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
