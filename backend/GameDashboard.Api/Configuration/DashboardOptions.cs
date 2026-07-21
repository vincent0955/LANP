namespace GameDashboard.Api.Configuration;

/// <summary>
/// Strongly-typed configuration bound to the "Dashboard" section of appsettings.json.
/// See design.md → Configuration.
/// </summary>
public sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    /// <summary>
    /// Logical grouping name for the dashboard's servers. A leftover from the
    /// Kubernetes namespace, kept because the /api/health wire shape still
    /// reports it; under Docker it is display-only.
    /// </summary>
    public string Namespace { get; set; } = "game-servers";

    /// <summary>
    /// Docker Engine endpoint override (e.g. "npipe://./pipe/docker_engine",
    /// "unix:///var/run/docker.sock", "tcp://127.0.0.1:2375"). When null, the
    /// platform default is probed first, then the bundled runtime's loopback
    /// TCP endpoint. See DockerClientFactory.
    /// </summary>
    public string? DockerEndpoint { get; set; }

    /// <summary>
    /// Directory for the dashboard's local state (secrets store, last-active
    /// file). Defaults to %APPDATA%/GameDashboard (Windows) or
    /// ~/.config/GameDashboard (Linux).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary><see cref="DataDirectory"/> with the platform default applied.</summary>
    public string ResolvedDataDirectory =>
        string.IsNullOrWhiteSpace(DataDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameDashboard")
            : DataDirectory;

    /// <summary>Address the backend binds to. Loopback by default (localhost-first).</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Port the backend listens on.</summary>
    public int Port { get; set; } = 5000;

    /// <summary>When the backend is exposed beyond loopback, require an auth token.</summary>
    public bool RequireAuthWhenExposed { get; set; } = true;

    /// <summary>
    /// Shared-secret token required on every request when the backend is bound to
    /// a non-loopback address and <see cref="RequireAuthWhenExposed"/> is true.
    /// Sent by clients via the "X-Api-Token" header. Not used at all when bound to
    /// loopback. See requirements.md → Req 14.1, Req 14.6.
    /// </summary>
    public string? ApiToken { get; set; }

    /// <summary>
    /// True if <see cref="BindAddress"/> resolves to a loopback address
    /// (127.0.0.1, ::1, etc.). Non-loopback binding is what triggers the
    /// token-auth requirement (Req 14.6).
    /// </summary>
    public bool IsLoopbackBind =>
        System.Net.IPAddress.TryParse(BindAddress, out var ip) && System.Net.IPAddress.IsLoopback(ip);

    /// <summary>
    /// Browser origins allowed to call the API cross-origin (CORS). Needed by the
    /// desktop frontend: the Tauri webview serves the UI from http://tauri.localhost
    /// and the Vite dev server from http://localhost:5173, both of which are
    /// cross-origin to this backend. CORS is origin gating for browsers only —
    /// token auth for non-loopback exposure is handled separately by
    /// TokenAuthMiddleware (Req 14).
    /// </summary>
    public IList<string> AllowedCorsOrigins { get; set; } = new List<string>
    {
        "http://tauri.localhost",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
    };

    public AutoScaleOptions AutoScale { get; set; } = new();
    public MetricsOptions Metrics { get; set; } = new();
    public RuntimeOptions Runtime { get; set; } = new();
    public BackupOptions Backup { get; set; } = new();
}

/// <summary>
/// World-backup settings (WS1). Backups are game-agnostic: every server stores
/// all persistent data in one named volume ({name}-data), so a backup is just a
/// gzip tar of that volume's contents, written to a real folder on the host.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>When true, <see cref="Services.BackupSchedulerService"/> snapshots every server on an interval.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How often the scheduler snapshots each server. Ignored when <see cref="Enabled"/> is false.</summary>
    public int IntervalMinutes { get; set; } = 360;

    /// <summary>
    /// Number of scheduled backups to keep per server; older ones are pruned
    /// after each scheduled run. Manual backups count toward this too. 0 disables pruning.
    /// </summary>
    public int RetentionCount { get; set; } = 5;

    /// <summary>
    /// Root directory backups are written under, one subfolder per server.
    /// Defaults to %USERPROFILE%/GameDashboard/backups so worlds land on the
    /// real host filesystem, safe from a runtime-VM reset.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Tiny helper image used to tar/untar volume contents. Overridable for air-gapped mirrors.</summary>
    public string HelperImage { get; set; } = "busybox:latest";

    /// <summary><see cref="Directory"/> with the platform default applied.</summary>
    public string ResolvedDirectory =>
        string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "GameDashboard", "backups")
            : Directory;
}

/// <summary>
/// Bundled container runtime settings (docs/docker-migration.md → Part 2).
/// Only meaningful on Windows, where the runtime is a WSL2 distro.
/// </summary>
public sealed class RuntimeOptions
{
    /// <summary>WSL distro name the bundled runtime is imported under.</summary>
    public string DistroName { get; set; } = "gamedashboard";

    /// <summary>
    /// Where the distro rootfs tarball is downloaded from on first run
    /// (built by scripts/runtime/build-wsl-distro.sh, published as a release
    /// asset — keeps the installer slim).
    /// </summary>
    public string DistroDownloadUrl { get; set; } =
        "https://github.com/vincent0955/source-server-cluster/releases/latest/download/gamedashboard-wsl-rootfs.tar.gz";

    /// <summary>
    /// Directory holding the imported distro's VHDX and the downloaded
    /// tarball. Defaults to %LOCALAPPDATA%\GameDashboard\wsl.
    /// </summary>
    public string? InstallDirectory { get; set; }
}

public sealed class AutoScaleOptions
{
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 30;
    public int MemoryHighWaterPercent { get; set; } = 80;
}

public sealed class MetricsOptions
{
    public int PushIntervalSeconds { get; set; } = 5;
}
