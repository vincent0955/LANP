using GameDashboard.Api.Models;

namespace GameDashboard.Api.Services;

/// <summary>
/// Game-agnostic world backup/restore (WS1). Every server keeps all persistent
/// state in a single named Docker volume ({name}-data), so a backup is a gzip
/// tar of that volume's contents and a restore untars an archive back into it —
/// no per-game knowledge of where "the world" lives. The tar runs inside a
/// throwaway helper container so it works even though the volume lives inside
/// the WSL2 runtime VM; bytes are streamed over the Docker API to/from a file on
/// the real host filesystem.
/// </summary>
public interface IBackupService
{
    /// <summary>Lists a server's saved backups, newest first.</summary>
    Task<IReadOnlyList<BackupInfo>> ListBackupsAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// Snapshots a server's data volume to a new archive on the host. Allowed
    /// while the server is running (a hot backup); callers wanting a quiescent
    /// copy stop the server first.
    /// </summary>
    Task<BackupInfo> CreateBackupAsync(string serverName, CancellationToken ct);

    /// <summary>
    /// Restores a saved archive into the server's data volume. The server must
    /// not be running (throws <see cref="InvalidOperationException"/> otherwise)
    /// so an open save is never corrupted mid-write.
    /// </summary>
    Task RestoreBackupAsync(string serverName, string backupId, CancellationToken ct);

    /// <summary>Deletes a saved archive. Throws <see cref="KeyNotFoundException"/> if it does not exist.</summary>
    Task DeleteBackupAsync(string serverName, string backupId, CancellationToken ct);

    /// <summary>
    /// Absolute path of a saved archive for streaming a download. Throws
    /// <see cref="KeyNotFoundException"/> if it does not exist.
    /// </summary>
    string GetBackupFilePath(string serverName, string backupId);
}
