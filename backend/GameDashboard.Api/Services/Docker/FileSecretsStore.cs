using System.Text.Json;
using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Docker;

/// <summary>
/// Replaces the Kubernetes game-secrets Secret: a single local file holding the
/// secret key/value map (docs/docker-migration.md → concept mapping).
///
/// At rest the JSON payload is DPAPI-encrypted per user on Windows
/// (System.Security.Cryptography.ProtectedData); on Linux it is written with
/// 0600 permissions — the same protection model as ~/.docker/config.json or an
/// SSH private key. Values never appear in logs, and never leave the process
/// except via the explicit per-key reveal endpoint.
/// </summary>
public interface ISecretsStore
{
    /// <summary>Sorted key names currently configured (never the values).</summary>
    Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken ct);

    /// <summary>Merge-writes the given pairs (existing keys not mentioned are kept).</summary>
    Task SetAsync(IDictionary<string, string> values, CancellationToken ct);

    /// <summary>Single-value read; null when the key is not configured.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken ct);

    /// <summary>Removes one key. False when it was not configured.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken ct);
}

public sealed class FileSecretsStore : ISecretsStore
{
    private readonly string _filePath;
    private readonly ILogger<FileSecretsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileSecretsStore(IOptions<DashboardOptions> options, ILogger<FileSecretsStore> logger)
    {
        _filePath = Path.Combine(options.Value.ResolvedDataDirectory, "secrets.bin");
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> GetKeysAsync(CancellationToken ct)
    {
        var data = await ReadAsync(ct);
        return data.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    public async Task SetAsync(IDictionary<string, string> values, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadUnlockedAsync(ct);
            foreach (var (key, value) in values)
            {
                data[key] = value;
            }
            await WriteUnlockedAsync(data, ct);

            // Log only the key names, never the values.
            _logger.LogInformation("Updated secrets store with keys: {Keys}", string.Join(", ", values.Keys));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetValueAsync(string key, CancellationToken ct)
    {
        var data = await ReadAsync(ct);
        return data.TryGetValue(key, out var value) ? value : null;
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var data = await ReadUnlockedAsync(ct);
            if (!data.Remove(key))
            {
                return false;
            }
            await WriteUnlockedAsync(data, ct);
            _logger.LogInformation("Removed key {Key} from secrets store.", key);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnlockedAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var raw = await File.ReadAllBytesAsync(_filePath, ct);
            var json = Unprotect(raw);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A corrupt/undecryptable store (e.g. copied from another Windows
            // user profile) must not take the whole dashboard down; treat as
            // empty and let the user re-enter secrets on the Setup screen.
            _logger.LogWarning(ex, "Secrets store at {Path} could not be read; treating as empty.", _filePath);
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task WriteUnlockedAsync(Dictionary<string, string> data, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);

        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllBytesAsync(_filePath, Protect(json), ct);
        }
        else
        {
            // 0600 before content lands: create/truncate with owner-only mode.
            using var stream = new FileStream(_filePath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            await stream.WriteAsync(json, ct);
        }
    }

    private static byte[] Protect(byte[] json) =>
        OperatingSystem.IsWindows()
            ? System.Security.Cryptography.ProtectedData.Protect(
                json, optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser)
            : json;

    private static string Unprotect(byte[] raw)
    {
        var bytes = OperatingSystem.IsWindows()
            ? System.Security.Cryptography.ProtectedData.Unprotect(
                raw, optionalEntropy: null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser)
            : raw;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
