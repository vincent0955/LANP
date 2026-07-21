namespace GameDashboard.Api.Models;

/// <summary>
/// One saved backup archive of a server's data volume (WS1). <see cref="Id"/> is
/// the archive's file name (a UTC timestamp), unique per server and safe to use
/// in a URL path segment.
/// </summary>
public sealed record BackupInfo(
    string Id,
    string ServerName,
    DateTimeOffset CreatedAt,
    long SizeBytes);
