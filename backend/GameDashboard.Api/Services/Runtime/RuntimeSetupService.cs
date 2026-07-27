using Docker.DotNet;
using GameDashboard.Api.Configuration;
using GameDashboard.Api.Services.Docker;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// First-run orchestration for the bundled Windows runtime
/// (docs/docker-migration.md → Windows first-run flow): WSL feature install →
/// distro download + import → engine start + health check. Driven by the
/// Setup screen; state is exposed via <see cref="GetStatusAsync"/> and the
/// install itself runs in the background so the HTTP request returns
/// immediately.
///
/// On Linux there is nothing to orchestrate (native engine); status reports
/// engine reachability and install requests are rejected.
/// </summary>
public interface IRuntimeSetupService
{
    Task<RuntimeStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// Kicks off the background install. False when one is already running or
    /// the platform has no bundled runtime (Linux).
    /// </summary>
    bool TryStartInstall();

    /// <summary>
    /// Merge-edits .wslconfig to enable mirrored networking. Returns whether a
    /// change was written and whether a `wsl --shutdown` restart is required
    /// for it to take effect (true whenever the file changed).
    /// </summary>
    Task<(bool Applied, bool ShutdownRequired)> ApplyMirroredNetworkingAsync(CancellationToken ct);

    /// <summary>
    /// Backend-startup hook (see RuntimeAutoStartService): reattaches the
    /// keep-alive if the bundled engine is already up, or boots it when the
    /// distro is imported but stopped (machine reboot). No-op on Linux and on
    /// Windows machines that never installed the bundled runtime.
    /// </summary>
    Task AutoStartAsync(CancellationToken ct);

    /// <summary>
    /// Stops the bundled runtime VM (tray-menu Exit). Callers stop the game
    /// servers gracefully first — this is the final hard teardown. The
    /// keep-alive session dies with the distro. No-op on Linux; never throws.
    /// </summary>
    Task StopRuntimeAsync(CancellationToken ct);
}

public sealed class RuntimeSetupService : IRuntimeSetupService
{
    private static readonly TimeSpan WslQueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WslInstallTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ImportTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultEngineStartTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The Windows-side session that pins the runtime VM. WSL terminates a
    /// distro's VM a short idle period after the last attached wsl.exe
    /// session exits — even with daemons still running inside, and
    /// independent of vmIdleTimeout (both verified empirically 2026-07-17).
    /// Docker Desktop stays up the same way: its Windows processes hold WSL
    /// sessions open. The flock makes duplicate spawns exit immediately, so
    /// EnsureKeepAlive is idempotent without any process inspection.
    /// </summary>
    private const string KeepAliveArguments =
        "sh -c \"exec flock -n /run/gamedashboard-keepalive.lock sleep 2147483647\"";

    /// <summary>
    /// Under mirrored networking, Windows-loopback connections to published
    /// ports enter the VM on loopback0 and would be DNAT'd to the container
    /// with a 127.0.0.1 source the kernel then drops — the readiness probe,
    /// RCON, and local players all hang. This idempotent rule routes loopback0
    /// traffic to docker-proxy instead (details in build-wsl-distro.sh, whose
    /// boot script installs the same rule; runtime versions ≤ 3 predate it,
    /// so it is re-ensured here on every engine start).
    /// </summary>
    private const string LoopbackDnatBypassArguments =
        "sh -c \"iptables -t nat -C PREROUTING -i loopback0 -j ACCEPT 2>/dev/null || " +
        "iptables -t nat -I PREROUTING 1 -i loopback0 -j ACCEPT\"";

    private readonly IWslRunner _wsl;
    private readonly IDockerClientFactory _clientFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IRuntimeModeStore _modeStore;
    private readonly DashboardOptions _options;
    private readonly ILogger<RuntimeSetupService> _logger;
    private readonly string _wslConfigPath;
    private readonly Func<CancellationToken, Task<bool>> _bundledEngineProbe;
    private readonly TimeSpan _engineStartTimeout;

    private readonly object _stateLock = new();
    private RuntimePhase _phase = RuntimePhase.Idle;
    private double? _downloadPercent;
    private string? _error;
    private Task? _installTask;

    public RuntimeSetupService(
        IWslRunner wsl,
        IDockerClientFactory clientFactory,
        IHttpClientFactory httpClientFactory,
        IRuntimeModeStore modeStore,
        IOptions<DashboardOptions> options,
        ILogger<RuntimeSetupService> logger)
        : this(wsl, clientFactory, httpClientFactory, modeStore, options, logger,
            wslConfigPath: null, bundledEngineProbe: null, engineStartTimeout: null)
    {
    }

    /// <summary>Test seam: lets unit tests point .wslconfig at a temp file and fake the engine probe.</summary>
    internal RuntimeSetupService(
        IWslRunner wsl,
        IDockerClientFactory clientFactory,
        IHttpClientFactory httpClientFactory,
        IRuntimeModeStore modeStore,
        IOptions<DashboardOptions> options,
        ILogger<RuntimeSetupService> logger,
        string? wslConfigPath,
        Func<CancellationToken, Task<bool>>? bundledEngineProbe = null,
        TimeSpan? engineStartTimeout = null)
    {
        _wsl = wsl;
        _clientFactory = clientFactory;
        _httpClientFactory = httpClientFactory;
        _modeStore = modeStore;
        _options = options.Value;
        _logger = logger;
        _wslConfigPath = wslConfigPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
        _bundledEngineProbe = bundledEngineProbe ?? ProbeBundledEngineOnceAsync;
        _engineStartTimeout = engineStartTimeout ?? DefaultEngineStartTimeout;
    }

    private string DistroName => _options.Runtime.DistroName;

    private string InstallDirectory =>
        string.IsNullOrWhiteSpace(_options.Runtime.InstallDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameDashboard", "wsl")
            : _options.Runtime.InstallDirectory;

    public async Task<RuntimeStatus> GetStatusAsync(CancellationToken ct)
    {
        var (client, _) = await _clientFactory.TryGetClientAsync(ct);
        var engineReachable = client is not null;
        var mode = await _modeStore.GetAsync(ct);

        if (!OperatingSystem.IsWindows())
        {
            return new RuntimeStatus(
                Platform: "linux",
                Mode: mode,
                WslInstalled: false,
                DistroImported: false,
                EngineReachable: engineReachable,
                MirroredNetworkingConfigured: false,
                Phase: RuntimePhase.Idle,
                DownloadPercent: null,
                Error: engineReachable
                    ? null
                    : "No Docker engine found. Run scripts/runtime/install-linux.sh to install it.",
                FirewallCommands: Array.Empty<string>());
        }

        if (mode == RuntimeMode.DockerDesktop)
        {
            // Nothing here is ours: no WSL query (the user may not even have it),
            // no install phase, and mirrored networking is Docker Desktop's own
            // setting to manage. Firewall rules still apply — those are about the
            // host's ports, whoever publishes them.
            return new RuntimeStatus(
                Platform: "windows",
                Mode: mode,
                WslInstalled: false,
                DistroImported: false,
                EngineReachable: engineReachable,
                MirroredNetworkingConfigured: false,
                Phase: RuntimePhase.Idle,
                DownloadPercent: null,
                Error: engineReachable
                    ? null
                    : "Docker Desktop isn't running. Start it, or switch back to the bundled runtime below.",
                FirewallCommands: FirewallCommands());
        }

        var wslInstalled = await IsWslInstalledAsync(ct);
        var distroImported = wslInstalled && await IsDistroImportedAsync(ct);
        var mirrored = HasMirroredNetworking();

        RuntimePhase phase;
        double? percent;
        string? error;
        lock (_stateLock)
        {
            phase = _phase;
            percent = _downloadPercent;
            error = _error;
        }

        return new RuntimeStatus(
            Platform: "windows",
            Mode: mode,
            WslInstalled: wslInstalled,
            DistroImported: distroImported,
            EngineReachable: engineReachable,
            MirroredNetworkingConfigured: mirrored,
            Phase: phase,
            DownloadPercent: percent,
            Error: error,
            FirewallCommands: FirewallCommands());
    }

    public bool TryStartInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        lock (_stateLock)
        {
            if (_installTask is { IsCompleted: false })
            {
                return false;
            }

            _phase = RuntimePhase.Idle;
            _downloadPercent = null;
            _error = null;
            _installTask = Task.Run(() => RunInstallAsync(CancellationToken.None));
            return true;
        }
    }

    public async Task<(bool Applied, bool ShutdownRequired)> ApplyMirroredNetworkingAsync(CancellationToken ct)
    {
        var existing = File.Exists(_wslConfigPath)
            ? await File.ReadAllTextAsync(_wslConfigPath, ct)
            : null;

        if (WslConfigEditor.HasMirroredNetworking(existing))
        {
            return (Applied: false, ShutdownRequired: false);
        }

        var updated = WslConfigEditor.EnsureMirroredNetworking(existing);
        await File.WriteAllTextAsync(_wslConfigPath, updated, ct);
        _logger.LogInformation("Enabled WSL mirrored networking in {Path}.", _wslConfigPath);

        // The setting only applies after the WSL VM restarts; the UI asks the
        // user before we shut down a VM their servers may be running in.
        return (Applied: true, ShutdownRequired: true);
    }

    public async Task AutoStartAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The whole point of the Docker Desktop mode: boot the app without
        // paying for a second VM. Starting our distro here would spin up the
        // WSL machine the user explicitly opted out of, every launch.
        if (await _modeStore.GetAsync(ct) == RuntimeMode.DockerDesktop)
        {
            _logger.LogInformation("Container engine is Docker Desktop; skipping bundled runtime auto-start.");
            return;
        }

        // The bundled engine is already up (VM survived from a previous
        // backend run): just make sure a keep-alive session pins it. When the
        // engine that's up is Docker Desktop's instead, this probe fails and
        // we deliberately don't touch our distro — their VM, their lifecycle.
        if (await _bundledEngineProbe(ct))
        {
            EnsureKeepAlive();
            await EnsureLoopbackDnatBypassAsync(ct);
            return;
        }

        // Machine (or WSL) restarted since the runtime was installed: boot it
        // back up so the user's servers come back without a Setup-screen trip.
        if (await IsWslInstalledAsync(ct) && await IsDistroImportedAsync(ct))
        {
            _logger.LogInformation("Bundled runtime is imported but stopped; starting it.");
            await StartEngineAndKeepAliveAsync(ct);
        }
    }

    public async Task StopRuntimeAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Their engine, their lifecycle: exiting the dashboard must not take
        // down a Docker Desktop the user runs for everything else on the box.
        // (The servers themselves were already stopped by the caller.)
        if (await _modeStore.GetAsync(ct) == RuntimeMode.DockerDesktop)
        {
            return;
        }

        try
        {
            var result = await _wsl.RunAsync($"--terminate {DistroName}", WslQueryTimeout, ct);
            _logger.LogInformation(
                "Stopped bundled runtime distro {Distro} (exit {ExitCode}).", DistroName, result.ExitCode);
        }
        catch (Exception ex)
        {
            // A distro that was never imported / already stopped is fine —
            // Exit must always complete.
            _logger.LogDebug(ex, "Stopping the bundled runtime failed (probably already stopped).");
        }
    }

    /// <summary>
    /// Spawns the detached keep-alive session (see <see cref="KeepAliveArguments"/>).
    /// Idempotent via the in-distro flock; never throws — a failed spawn just
    /// means the VM may idle out and the next AutoStart/install re-pins it.
    /// </summary>
    private void EnsureKeepAlive()
    {
        try
        {
            _wsl.StartDetached($"-d {DistroName} {KeepAliveArguments}");
            _logger.LogInformation("Spawned WSL keep-alive session for {Distro}.", DistroName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to spawn the WSL keep-alive session.");
        }
    }

    private async Task<bool> StartEngineAndKeepAliveAsync(CancellationToken ct)
    {
        // Entering the distro boots the VM; wsl.conf's [boot] command starts
        // dockerd. The explicit script call covers a VM that was already up.
        await _wsl.RunAsync($"-d {DistroName} /usr/local/bin/start-dockerd.sh", WslQueryTimeout, ct);

        if (!await WaitForEngineAsync(ct))
        {
            return false;
        }

        // Without this the VM idles out ~60s after the last wsl.exe session,
        // killing every game server in it — see KeepAliveArguments.
        EnsureKeepAlive();
        await EnsureLoopbackDnatBypassAsync(ct);
        return true;
    }

    /// <summary>
    /// Ensures the loopback0 DNAT-bypass rule (see
    /// <see cref="LoopbackDnatBypassArguments"/>) after the engine is up.
    /// Never throws — without it the dashboard still works except that
    /// statuses stick at Pending and RCON can't connect, which the logs flag.
    /// </summary>
    private async Task EnsureLoopbackDnatBypassAsync(CancellationToken ct)
    {
        try
        {
            var result = await _wsl.RunAsync(
                $"-d {DistroName} {LoopbackDnatBypassArguments}", WslQueryTimeout, ct);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "Loopback DNAT bypass rule could not be ensured (exit {ExitCode}): {Output}. " +
                    "Server statuses may stick at Pending and RCON may be unreachable.",
                    result.ExitCode, Summarize(result.Output));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Loopback DNAT bypass rule could not be ensured. " +
                "Server statuses may stick at Pending and RCON may be unreachable.");
        }
    }

    // --- install pipeline ---

    private async Task RunInstallAsync(CancellationToken ct)
    {
        try
        {
            if (!await IsWslInstalledAsync(ct))
            {
                SetPhase(RuntimePhase.InstallingWsl);
                var exitCode = await _wsl.RunElevatedAsync("--install --no-distribution", WslInstallTimeout, ct);
                if (exitCode is null)
                {
                    Fail("The administrator prompt was cancelled. WSL is required for the bundled runtime.");
                    return;
                }

                if (!await IsWslInstalledAsync(ct))
                {
                    // The feature installed but the kernel isn't live yet — the
                    // standard reason is a pending reboot.
                    SetPhase(RuntimePhase.AwaitingReboot);
                    return;
                }
            }

            if (!await IsDistroImportedAsync(ct))
            {
                var tarball = await ResolveDistroTarballAsync(ct);

                SetPhase(RuntimePhase.ImportingDistro);
                Directory.CreateDirectory(InstallDirectory);
                var import = await _wsl.RunAsync(
                    $"--import {DistroName} \"{InstallDirectory}\" \"{tarball}\" --version 2",
                    ImportTimeout, ct);
                if (import.ExitCode != 0)
                {
                    Fail($"Importing the runtime distro failed: {Summarize(import.Output)}");
                    return;
                }
            }

            SetPhase(RuntimePhase.StartingEngine);
            if (!await StartEngineAndKeepAliveAsync(ct))
            {
                Fail(
                    "The runtime started but its engine never answered on tcp://127.0.0.1:2375. " +
                    "Check virtualization is enabled in BIOS/UEFI, then retry.");
                return;
            }

            SetPhase(RuntimePhase.Ready);
            _logger.LogInformation("Bundled runtime install completed; engine is up.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bundled runtime install failed.");
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// The rootfs tarball name as shipped in the installer (a Tauri resource
    /// that lands next to the sidecar) and produced by
    /// scripts/runtime/build-wsl-distro.sh.
    /// </summary>
    private const string BundledTarballName = "gamedashboard-wsl-rootfs.tar.gz";

    private async Task<string> ResolveDistroTarballAsync(CancellationToken ct)
    {
        // Prefer the tarball the installer shipped — no download, works
        // offline. The sidecar runs with the resource dir as cwd, but check
        // the exe's own dir too (dev layouts differ).
        foreach (var dir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var bundled = Path.Combine(dir, BundledTarballName);
            if (File.Exists(bundled))
            {
                _logger.LogInformation("Using installer-bundled runtime tarball at {Path}.", bundled);
                return bundled;
            }
        }

        return await DownloadDistroAsync(ct);
    }

    private async Task<string> DownloadDistroAsync(CancellationToken ct)
    {
        SetPhase(RuntimePhase.DownloadingDistro);
        Directory.CreateDirectory(InstallDirectory);
        var tarballPath = Path.Combine(InstallDirectory, "rootfs.tar.gz");

        var http = _httpClientFactory.CreateClient();
        using var response = await http.GetAsync(
            _options.Runtime.DistroDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(tarballPath);

        var buffer = new byte[1024 * 1024];
        long written = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            if (totalBytes is > 0)
            {
                lock (_stateLock)
                {
                    _downloadPercent = Math.Round(written * 100.0 / totalBytes.Value, 1);
                }
            }
        }

        lock (_stateLock)
        {
            _downloadPercent = null;
        }
        return tarballPath;
    }

    /// <summary>Single quick ping of the bundled endpoint — no retry loop.</summary>
    private static async Task<bool> ProbeBundledEngineOnceAsync(CancellationToken ct)
    {
        try
        {
            using var client = new DockerClientConfiguration(
                new Uri("tcp://127.0.0.1:2375")).CreateClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.System.PingAsync(cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForEngineAsync(CancellationToken ct)
    {
        // Health-check the bundled endpoint directly — the shared factory may
        // legitimately be attached to a different engine (Docker Desktop).
        var deadline = DateTime.UtcNow + _engineStartTimeout;
        while (true)
        {
            if (await _bundledEngineProbe(ct))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    // --- probes ---

    private async Task<bool> IsWslInstalledAsync(CancellationToken ct)
    {
        try
        {
            // Exit code 0 with output means WSL is functional; a missing
            // kernel or disabled feature exits non-zero.
            var result = await _wsl.RunAsync("--status", WslQueryTimeout, ct);
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "wsl.exe probe failed; treating WSL as not installed.");
            return false;
        }
    }

    private async Task<bool> IsDistroImportedAsync(CancellationToken ct)
    {
        try
        {
            var result = await _wsl.RunAsync("--list --quiet", WslQueryTimeout, ct);
            if (result.ExitCode != 0)
            {
                return false;
            }

            return result.Output
                .Split('\n')
                .Select(line => line.Trim().TrimEnd('\r'))
                .Any(line => line.Equals(DistroName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "wsl.exe list failed; treating distro as not imported.");
            return false;
        }
    }

    private bool HasMirroredNetworking()
    {
        try
        {
            return File.Exists(_wslConfigPath) &&
                WslConfigEditor.HasMirroredNetworking(File.ReadAllText(_wslConfigPath));
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> FirewallCommands() => new[]
    {
        // Host firewall: allow the dashboard's whole port window once (TCP+UDP).
        "New-NetFirewallRule -DisplayName 'GameDashboard Servers (TCP)' -Direction Inbound " +
            $"-Protocol TCP -LocalPort {ContainerSpecBuilder.HostPortRangeStart}-{ContainerSpecBuilder.HostPortRangeEnd} -Action Allow",
        "New-NetFirewallRule -DisplayName 'GameDashboard Servers (UDP)' -Direction Inbound " +
            $"-Protocol UDP -LocalPort {ContainerSpecBuilder.HostPortRangeStart}-{ContainerSpecBuilder.HostPortRangeEnd} -Action Allow",
        // Hyper-V firewall must also allow inbound for mirrored-mode WSL; the
        // GUID is WSL's fixed VM creator id.
        "Set-NetFirewallHyperVVMSetting -Name '{40E0AC32-46A5-438A-A0B2-2B479E8F2E90}' -DefaultInboundAction Allow",
    };

    private void SetPhase(RuntimePhase phase)
    {
        lock (_stateLock)
        {
            _phase = phase;
        }
    }

    private void Fail(string message)
    {
        lock (_stateLock)
        {
            _phase = RuntimePhase.Failed;
            _error = message;
        }
    }

    private static string Summarize(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }
}
