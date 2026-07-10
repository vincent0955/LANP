using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.RealTime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace GameDashboard.Api.Services;

/// <summary>
/// Periodically pushes a <see cref="Models.MetricsSnapshot"/> to SignalR clients
/// subscribed to the "metrics" group. See design.md → MetricsUpdate;
/// requirements.md → Req 9.4.
///
/// Uses <see cref="IMetricsService"/>, which never throws even when metrics-server
/// is absent, so this loop does not need special-case error handling beyond the
/// usual "don't let a background service die" guard.
/// </summary>
public sealed class MetricsPushService : BackgroundService
{
    private readonly IMetricsService _metricsService;
    private readonly IHubContext<DashboardHub> _hubContext;
    private readonly DashboardOptions _options;
    private readonly ILogger<MetricsPushService> _logger;

    public MetricsPushService(
        IMetricsService metricsService,
        IHubContext<DashboardHub> hubContext,
        IOptions<DashboardOptions> options,
        ILogger<MetricsPushService> logger)
    {
        _metricsService = metricsService;
        _hubContext = hubContext;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Metrics.PushIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _metricsService.GetSnapshotAsync(stoppingToken);

                await _hubContext.Clients.Group("metrics").SendAsync(
                    HubEvents.MetricsUpdate, snapshot, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to push metrics update.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
