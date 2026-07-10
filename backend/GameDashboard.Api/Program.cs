using GameDashboard.Api.Configuration;
using GameDashboard.Api.Hubs;
using GameDashboard.Api.Middleware;
using GameDashboard.Api.RealTime;
using GameDashboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration (options pattern) ---
builder.Services
    .AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Resolve options early so we can configure Kestrel's bind address/port.
var dashboardOptions = builder.Configuration
    .GetSection(DashboardOptions.SectionName)
    .Get<DashboardOptions>() ?? new DashboardOptions();

// --- Kestrel: bind to loopback by default (localhost-first, Req 14) ---
builder.WebHost.ConfigureKestrel(kestrel =>
{
    if (System.Net.IPAddress.TryParse(dashboardOptions.BindAddress, out var ip))
    {
        kestrel.Listen(ip, dashboardOptions.Port);
    }
    else
    {
        // Fallback to loopback if the configured address is invalid.
        kestrel.ListenLocalhost(dashboardOptions.Port);
    }
});

// Fail fast rather than silently running unauthenticated: if the operator has
// configured non-loopback exposure with auth required, a token MUST be set
// (Req 14.1, Req 14.6).
if (!dashboardOptions.IsLoopbackBind && dashboardOptions.RequireAuthWhenExposed &&
    string.IsNullOrWhiteSpace(dashboardOptions.ApiToken))
{
    throw new InvalidOperationException(
        $"Dashboard is configured to bind to a non-loopback address ({dashboardOptions.BindAddress}) " +
        "with RequireAuthWhenExposed=true, but no Dashboard:ApiToken is configured. " +
        "Set Dashboard:ApiToken in configuration, or set RequireAuthWhenExposed=false to explicitly " +
        "run unauthenticated (not recommended).");
}

builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));

// --- Kubernetes connectivity (Phase 2) ---
builder.Services.AddSingleton<IKubernetesClientFactory, KubernetesClientFactory>();
builder.Services.AddScoped<IKubernetesService, KubernetesService>();
builder.Services.AddSingleton<IDeploymentBuilder, DeploymentBuilderService>();
builder.Services.AddSingleton<IGameCatalogService, GameCatalogService>();

// --- Real-time (Phase 5) ---
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
        options.PayloadSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<ILogStreamManager, LogStreamManager>();
builder.Services.AddHostedService<PodWatchService>();

// --- Metrics (Phase 6) ---
builder.Services.AddSingleton<IMetricsService, MetricsService>();
builder.Services.AddHostedService<MetricsPushService>();

// --- RCON (Phase 6) ---
builder.Services.AddSingleton<IRconService, RconService>();

// --- Auto-scaling (Phase 8) ---
builder.Services.AddHostedService<AutoScaleService>();

var app = builder.Build();

// --- Best-effort namespace bootstrap (Req 1.3, Req 13.1) ---
// Never allowed to crash startup — the cluster may legitimately be unreachable
// (Docker Desktop not running yet), in which case /api/health will report it.
using (var scope = app.Services.CreateScope())
{
    var k8s = scope.ServiceProvider.GetRequiredService<IKubernetesService>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await k8s.EnsureNamespaceAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex,
            "Could not ensure the game-servers namespace on startup. " +
            "The backend will continue running; check /api/health.");
    }
}

// --- Global exception handling → ProblemDetails (Req 14) ---
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<TokenAuthMiddleware>();

// Root + health endpoints (full cluster health arrives in Phase 2).
app.MapGet("/", () => Results.Ok(new { service = "GameDashboard.Api", status = "ok" }));

app.MapControllers();
app.MapHub<DashboardHub>("/hubs/dashboard");

app.Run();

// Exposed for integration testing via WebApplicationFactory.
public partial class Program { }
