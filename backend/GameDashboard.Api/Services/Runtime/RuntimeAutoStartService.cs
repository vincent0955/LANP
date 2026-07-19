namespace GameDashboard.Api.Services.Runtime;

/// <summary>
/// One-shot startup hook: brings the bundled runtime back after a machine
/// reboot (and re-pins a surviving VM with a keep-alive session) so launching
/// the app is all a user ever has to do — no Setup-screen trip on every boot.
/// All the decision logic lives in <see cref="IRuntimeSetupService.AutoStartAsync"/>;
/// this class only schedules it off the startup path.
/// </summary>
public sealed class RuntimeAutoStartService : BackgroundService
{
    private readonly IRuntimeSetupService _runtime;
    private readonly ILogger<RuntimeAutoStartService> _logger;

    public RuntimeAutoStartService(IRuntimeSetupService runtime, ILogger<RuntimeAutoStartService> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _runtime.AutoStartAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App shutting down during startup — nothing to clean up.
        }
        catch (Exception ex)
        {
            // Best-effort: a failed auto-start just means the Setup screen's
            // manual "Start runtime" flow is needed; never crash the backend.
            _logger.LogWarning(ex, "Bundled runtime auto-start failed; the Setup screen can start it manually.");
        }
    }
}
