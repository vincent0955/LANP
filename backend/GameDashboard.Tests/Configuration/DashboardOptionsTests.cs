using GameDashboard.Api.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GameDashboard.Tests.Configuration;

public class DashboardOptionsTests
{
    [Fact]
    public void Binds_All_Values_From_Configuration()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dashboard:Namespace"] = "game-servers",
                ["Dashboard:SecretName"] = "game-secrets",
                ["Dashboard:BindAddress"] = "127.0.0.1",
                ["Dashboard:Port"] = "5000",
                ["Dashboard:RequireAuthWhenExposed"] = "true",
                ["Dashboard:ApiToken"] = "test-token",
                ["Dashboard:AutoScale:Enabled"] = "true",
                ["Dashboard:AutoScale:IntervalSeconds"] = "30",
                ["Dashboard:AutoScale:MemoryHighWaterPercent"] = "80",
                ["Dashboard:Metrics:PushIntervalSeconds"] = "5"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddOptions<DashboardOptions>()
            .Bind(config.GetSection(DashboardOptions.SectionName));

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;

        Assert.Equal("game-servers", options.Namespace);
        Assert.Equal("game-secrets", options.SecretName);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(5000, options.Port);
        Assert.True(options.RequireAuthWhenExposed);
        Assert.Equal("test-token", options.ApiToken);
        Assert.True(options.AutoScale.Enabled);
        Assert.Equal(30, options.AutoScale.IntervalSeconds);
        Assert.Equal(80, options.AutoScale.MemoryHighWaterPercent);
        Assert.Equal(5, options.Metrics.PushIntervalSeconds);
    }

    [Fact]
    public void Uses_Sensible_Defaults_When_Config_Absent()
    {
        var options = new DashboardOptions();

        Assert.Equal("game-servers", options.Namespace);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(5000, options.Port);
        Assert.True(options.AutoScale.Enabled);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.1.50", false)]
    [InlineData("not-an-ip", false)]
    public void IsLoopbackBind_Correctly_Identifies_Loopback_Addresses(string address, bool expected)
    {
        var options = new DashboardOptions { BindAddress = address };

        Assert.Equal(expected, options.IsLoopbackBind);
    }
}
