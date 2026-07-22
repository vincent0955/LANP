using System.Text;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Exceptions;
using GameDashboard.Api.Models;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// See <see cref="IBackupService"/>. Uses a throwaway helper container
/// (a tiny <c>busybox</c>) that mounts the server's data volume and runs
/// <c>tar</c>, streaming the archive to/from a file on the host over the Docker
/// API. This keeps the whole thing game-agnostic (one volume per server) and
/// works across the WSL2 boundary, where the volume's bytes are otherwise
/// unreachable from Windows.
/// </summary>
public sealed partial class BackupService : IBackupService
{
    private const string BackupExtension = ".tar.gz";

    private readonly IDockerClientFactory _clientFactory;
    private readonly BackupOptions _options;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IDockerClientFactory clientFactory,
        IOptions<DashboardOptions> options,
        ILogger<BackupService> logger)
    {
        _clientFactory = clientFactory;
        _options = options.Value.Backup;
        _logger = logger;
    }

    public Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string serverName, CancellationToken ct)
    {
        ServerNameValidator.EnsureValid(serverName);

        var dir = ServerBackupDir(serverName);
        if (!Directory.Exists(dir))
        {
            return Task.FromResult<IReadOnlyList<BackupInfo>>(Array.Empty<BackupInfo>());
        }

        var backups = Directory.EnumerateFiles(dir, "*" + BackupExtension)
            .Select(path => ToBackupInfo(serverName, path))
            .OrderByDescending(b => b.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<BackupInfo>>(backups);
    }

    public async Task<BackupInfo> CreateBackupAsync(string serverName, CancellationToken ct)
    {
        ServerNameValidator.EnsureValid(serverName);
        var client = await RequireClientAsync(ct);
        await EnsureManagedAsync(client, serverName, ct);

        var dir = ServerBackupDir(serverName);
        Directory.CreateDirectory(dir);
        var id = NewBackupId();
        var path = Path.Combine(dir, id + BackupExtension);

        await EnsureHelperImageAsync(client, ct);

        // tar the volume contents to stdout; we stream stdout straight to the host file.
        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _options.HelperImage,
            Cmd = new[] { "tar", "czf", "-", "-C", "/data", "." },
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            HostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new() { Type = "volume", Source = ContainerSpecBuilder.VolumeNameFor(serverName), Target = "/data", ReadOnly = true },
                },
            },
        }, ct);

        try
        {
            using var stream = await client.Containers.AttachContainerAsync(
                created.ID, tty: false,
                new ContainerAttachParameters { Stream = true, Stdout = true, Stderr = true }, ct);

            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

            string stderr;
            await using (var file = File.Create(path))
            {
                stderr = await CopyStdoutToAsync(stream, file, ct);
            }

            var wait = await client.Containers.WaitContainerAsync(created.ID, ct);
            if (wait.StatusCode != 0)
            {
                TryDeleteFile(path);
                throw new InvalidOperationException(
                    $"Backup of '{serverName}' failed (tar exit {wait.StatusCode}): {Trim(stderr)}");
            }
        }
        catch
        {
            TryDeleteFile(path);
            throw;
        }
        finally
        {
            await TryRemoveContainerAsync(client, created.ID, ct);
        }

        var info = ToBackupInfo(serverName, path);
        _logger.LogInformation(
            "Created backup {Id} for server {Server} ({Bytes} bytes).", id, serverName, info.SizeBytes);
        return info;
    }

    public async Task RestoreBackupAsync(string serverName, string backupId, CancellationToken ct)
    {
        ServerNameValidator.EnsureValid(serverName);
        var path = GetBackupFilePath(serverName, backupId);

        var client = await RequireClientAsync(ct);
        var container = await InspectManagedAsync(client, serverName, ct);

        // Restoring under a live server would race the game's own writes and
        // corrupt the save; require it stopped first.
        if (container.State?.Running == true || container.State?.Status == "restarting")
        {
            throw new InvalidOperationException(
                $"Stop '{serverName}' before restoring a backup — restoring into a running server would corrupt its save.");
        }

        await EnsureHelperImageAsync(client, ct);

        // Wipe the volume then extract, so the restore is an exact replacement
        // rather than a merge over whatever is currently there.
        var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = _options.HelperImage,
            Cmd = new[] { "sh", "-c", "rm -rf /data/* /data/..?* /data/.[!.]* 2>/dev/null; exec tar xzf - -C /data" },
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            OpenStdin = true,
            StdinOnce = true,
            Tty = false,
            HostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new() { Type = "volume", Source = ContainerSpecBuilder.VolumeNameFor(serverName), Target = "/data" },
                },
            },
        }, ct);

        try
        {
            using var stream = await client.Containers.AttachContainerAsync(
                created.ID, tty: false,
                new ContainerAttachParameters { Stream = true, Stdin = true, Stdout = true, Stderr = true }, ct);

            await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);

            await using (var file = File.OpenRead(path))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await file.ReadAsync(buffer, ct)) > 0)
                {
                    await stream.WriteAsync(buffer, 0, read, ct);
                }
            }
            stream.CloseWrite();

            var stderr = await DrainAsync(stream, ct);
            var wait = await client.Containers.WaitContainerAsync(created.ID, ct);
            if (wait.StatusCode != 0)
            {
                throw new InvalidOperationException(
                    $"Restore of '{serverName}' failed (tar exit {wait.StatusCode}): {Trim(stderr)}");
            }
        }
        finally
        {
            await TryRemoveContainerAsync(client, created.ID, ct);
        }

        _logger.LogInformation("Restored backup {Id} into server {Server}.", backupId, serverName);
    }

    public Task DeleteBackupAsync(string serverName, string backupId, CancellationToken ct)
    {
        var path = GetBackupFilePath(serverName, backupId);
        File.Delete(path);
        _logger.LogInformation("Deleted backup {Id} for server {Server}.", backupId, serverName);
        return Task.CompletedTask;
    }

    public string GetBackupFilePath(string serverName, string backupId)
    {
        ServerNameValidator.EnsureValid(serverName);
        if (!BackupIdRegex().IsMatch(backupId))
        {
            throw new ArgumentException($"'{backupId}' is not a valid backup id.");
        }

        var path = Path.Combine(ServerBackupDir(serverName), backupId + BackupExtension);
        if (!File.Exists(path))
        {
            throw new KeyNotFoundException($"Backup '{backupId}' was not found for server '{serverName}'.");
        }
        return path;
    }

    // --- internals ---

    private string ServerBackupDir(string serverName) =>
        Path.Combine(_options.ResolvedDirectory, serverName);

    private static string NewBackupId() => DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");

    private static BackupInfo ToBackupInfo(string serverName, string path)
    {
        var info = new FileInfo(path);
        var id = Path.GetFileName(path);
        if (id.EndsWith(BackupExtension, StringComparison.Ordinal))
        {
            id = id[..^BackupExtension.Length];
        }
        return new BackupInfo(id, serverName, info.LastWriteTimeUtc, info.Length);
    }

    private async Task<IDockerClient> RequireClientAsync(CancellationToken ct)
    {
        var (client, error) = await _clientFactory.TryGetClientAsync(ct);
        return client ?? throw new ClusterUnreachableException($"Docker engine unreachable: {error}");
    }

    private static async Task EnsureManagedAsync(IDockerClient client, string serverName, CancellationToken ct) =>
        await InspectManagedAsync(client, serverName, ct);

    private static async Task<ContainerInspectResponse> InspectManagedAsync(
        IDockerClient client, string serverName, CancellationToken ct)
    {
        ContainerInspectResponse container;
        try
        {
            container = await client.Containers.InspectContainerAsync(serverName, ct);
        }
        catch (DockerContainerNotFoundException)
        {
            throw new ServerNotFoundException(serverName);
        }

        var labels = container.Config?.Labels;
        if (labels is null ||
            !labels.TryGetValue(ContainerLabels.Managed, out var managed) ||
            managed != ContainerLabels.ManagedValue)
        {
            throw new ServerNotFoundException(serverName);
        }
        return container;
    }

    private async Task EnsureHelperImageAsync(IDockerClient client, CancellationToken ct)
    {
        var (fromImage, tag) = DockerService.SplitImageRef(_options.HelperImage);
        var existing = await client.Images.ListImagesAsync(
            new ImagesListParameters { Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [_options.HelperImage] = true },
            } }, ct);
        if (existing.Count > 0)
        {
            return;
        }

        _logger.LogInformation("Pulling backup helper image {Image}.", _options.HelperImage);
        await client.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
            authConfig: null, progress: new Progress<JSONMessage>(), ct);
    }

    /// <summary>Copies stdout frames of a demuxed stream to <paramref name="destination"/>, returning any stderr text.</summary>
    private static async Task<string> CopyStdoutToAsync(
        MultiplexedStream stream, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];
        var stderr = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF)
            {
                break;
            }

            if (read.Target == MultiplexedStream.TargetStream.StandardError)
            {
                stderr.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            }
            else
            {
                await destination.WriteAsync(buffer.AsMemory(0, read.Count), ct);
            }
        }

        return stderr.ToString();
    }

    /// <summary>Reads a demuxed stream to EOF, returning any stderr text (stdout discarded).</summary>
    private static async Task<string> DrainAsync(MultiplexedStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var stderr = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (read.EOF)
            {
                break;
            }
            if (read.Target == MultiplexedStream.TargetStream.StandardError)
            {
                stderr.Append(Encoding.UTF8.GetString(buffer, 0, read.Count));
            }
        }

        return stderr.ToString();
    }

    private async Task TryRemoveContainerAsync(IDockerClient client, string id, CancellationToken ct)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                id, new ContainerRemoveParameters { Force = true }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove backup helper container {Id} (will be cleaned up eventually).", id);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort — a stray partial archive is harmless.
        }
    }

    private static string Trim(string text) =>
        text.Length > 500 ? text[..500] + "…" : text.Trim();

    [GeneratedRegex(@"^\d{8}-\d{6}-\d{3}$")]
    private static partial Regex BackupIdRegex();
}
