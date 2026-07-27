namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// Where the bundled-runtime first-run flow currently is
/// (docs/docker-migration.md → Windows first-run flow). Serialized as strings
/// over the wire (JsonStringEnumConverter).
/// </summary>
public enum RuntimePhase
{
    /// <summary>No install running (initial state, or after completion/failure).</summary>
    Idle,

    /// <summary>`wsl --install --no-distribution` running (UAC prompt on screen).</summary>
    InstallingWsl,

    /// <summary>WSL was installed but needs a Windows reboot before it works.</summary>
    AwaitingReboot,

    DownloadingDistro,
    ImportingDistro,
    StartingEngine,

    /// <summary>The engine answered on tcp://127.0.0.1:2375 — install complete.</summary>
    Ready,

    Failed,
}

/// <summary>
/// Which container engine the dashboard talks to. Persisted across restarts by
/// <see cref="IRuntimeModeStore"/> and chosen by the user on the Setup screen;
/// self-hosters who already run Docker Desktop on the box can point the
/// dashboard at it instead of paying for a second VM.
/// </summary>
public enum RuntimeMode
{
    /// <summary>The app-managed WSL distro (default): installed, started and stopped by us.</summary>
    Bundled,

    /// <summary>
    /// A Docker engine the user already runs (Docker Desktop on Windows, the
    /// native daemon on Linux). We only connect to it — never install, start or
    /// stop it; its lifecycle belongs to the user.
    /// </summary>
    DockerDesktop,
}

/// <summary>
/// Snapshot of the bundled runtime's state for the Setup screen. On Linux
/// only <see cref="Platform"/> and <see cref="EngineReachable"/> are
/// meaningful (native engine, no WSL machinery). In
/// <see cref="RuntimeMode.DockerDesktop"/> mode the WSL fields are likewise
/// meaningless — nothing is ours to install.
/// </summary>
public record RuntimeStatus(
    string Platform,
    /// <summary>The persisted engine choice; drives which half of the Setup card applies.</summary>
    RuntimeMode Mode,
    bool WslInstalled,
    bool DistroImported,
    bool EngineReachable,
    bool MirroredNetworkingConfigured,
    RuntimePhase Phase,
    /// <summary>0–100 while <see cref="RuntimePhase.DownloadingDistro"/>; null otherwise.</summary>
    double? DownloadPercent,
    string? Error,
    /// <summary>
    /// Admin commands the app deliberately does not run itself (firewall
    /// changes); the UI renders them as copyable snippets.
    /// </summary>
    IReadOnlyList<string> FirewallCommands);
