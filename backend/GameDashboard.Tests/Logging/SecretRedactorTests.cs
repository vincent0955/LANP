using GameDashboard.Api.Logging;

namespace GameDashboard.Tests.Logging;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("SRCDS_TOKEN", true)]
    [InlineData("CS2_RCONPW", true)]
    [InlineData("RCON_PASSWORD", true)]
    [InlineData("MY_SECRET", true)]
    [InlineData("API_KEY", true)]
    [InlineData("CS2_MAXPLAYERS", false)]
    [InlineData("GAME_MAP", false)]
    [InlineData("EULA", false)]
    public void IsSensitiveKey_Detects_Sensitive_Names(string key, bool expected)
    {
        Assert.Equal(expected, SecretRedactor.IsSensitiveKey(key));
    }

    [Fact]
    public void Redact_Masks_Sensitive_Values_Only()
    {
        var input = new Dictionary<string, string>
        {
            ["SRCDS_TOKEN"] = "abc123",
            ["CS2_RCONPW"] = "hunter2",
            ["CS2_MAXPLAYERS"] = "16",
            ["GAME_MAP"] = "de_dust2"
        };

        var result = SecretRedactor.Redact(input);

        Assert.Equal("***REDACTED***", result["SRCDS_TOKEN"]);
        Assert.Equal("***REDACTED***", result["CS2_RCONPW"]);
        Assert.Equal("16", result["CS2_MAXPLAYERS"]);
        Assert.Equal("de_dust2", result["GAME_MAP"]);
    }

    [Fact]
    public void RedactString_Masks_Inline_Secret_Assignments()
    {
        var input = "starting server RCON_PASSWORD=hunter2 with MAX_PLAYERS=16";

        var result = SecretRedactor.RedactString(input);

        Assert.Contains("RCON_PASSWORD=***REDACTED***", result);
        Assert.Contains("MAX_PLAYERS=16", result);
        Assert.DoesNotContain("hunter2", result);
    }

    [Fact]
    public void RedactString_Handles_Empty_Input()
    {
        Assert.Equal(string.Empty, SecretRedactor.RedactString(string.Empty));
    }
}
