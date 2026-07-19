using GameDashboard.Api.Services.Runtime;

namespace GameDashboard.Tests.Services.Runtime;

/// <summary>
/// Covers the .wslconfig merge-edit (docs/docker-migration.md → Inbound
/// networking): mirrored networking must be enabled without ever clobbering a
/// user's unrelated WSL settings.
/// </summary>
public class WslConfigEditorTests
{
    [Fact]
    public void Empty_Config_Gets_A_Fresh_Wsl2_Section()
    {
        var result = WslConfigEditor.EnsureMirroredNetworking(null);

        Assert.True(WslConfigEditor.HasMirroredNetworking(result));
        Assert.Contains("[wsl2]", result);
        Assert.Contains("networkingMode=mirrored", result);
    }

    [Fact]
    public void Existing_Wsl2_Section_Without_The_Key_Gets_It_Added()
    {
        var existing = "[wsl2]\nmemory=8GB\nprocessors=4\n";

        var result = WslConfigEditor.EnsureMirroredNetworking(existing);

        Assert.True(WslConfigEditor.HasMirroredNetworking(result));
        // Unrelated keys preserved.
        Assert.Contains("memory=8GB", result);
        Assert.Contains("processors=4", result);
    }

    [Fact]
    public void A_Different_NetworkingMode_Is_Updated_In_Place()
    {
        var existing = "[wsl2]\nnetworkingMode=nat\nmemory=8GB\n";

        var result = WslConfigEditor.EnsureMirroredNetworking(existing);

        Assert.True(WslConfigEditor.HasMirroredNetworking(result));
        Assert.DoesNotContain("networkingMode=nat", result);
        Assert.Contains("memory=8GB", result);
    }

    [Fact]
    public void Other_Sections_Are_Preserved_Verbatim()
    {
        var existing = "[experimental]\nsparseVhd=true\n\n[wsl2]\nmemory=8GB\n";

        var result = WslConfigEditor.EnsureMirroredNetworking(existing);

        Assert.Contains("[experimental]", result);
        Assert.Contains("sparseVhd=true", result);
        Assert.True(WslConfigEditor.HasMirroredNetworking(result));
    }

    [Fact]
    public void Config_Without_A_Wsl2_Section_Gets_One_Appended()
    {
        var existing = "[experimental]\nsparseVhd=true\n";

        var result = WslConfigEditor.EnsureMirroredNetworking(existing);

        Assert.Contains("sparseVhd=true", result);
        Assert.True(WslConfigEditor.HasMirroredNetworking(result));
    }

    [Fact]
    public void EnsureMirroredNetworking_Is_Idempotent()
    {
        var once = WslConfigEditor.EnsureMirroredNetworking("[wsl2]\nmemory=8GB\n");
        var twice = WslConfigEditor.EnsureMirroredNetworking(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void A_NetworkingMode_Key_In_Another_Section_Does_Not_Count()
    {
        var existing = "[experimental]\nnetworkingMode=mirrored\n";

        Assert.False(WslConfigEditor.HasMirroredNetworking(existing));
    }

    [Theory]
    [InlineData("[wsl2]\nnetworkingMode=mirrored\n")]
    [InlineData("[WSL2]\nNetworkingMode = Mirrored\n")] // ini keys are case-insensitive
    [InlineData("[wsl2]\r\nnetworkingMode=mirrored\r\n")] // Windows line endings
    public void HasMirroredNetworking_Detects_Existing_Configuration(string content)
    {
        Assert.True(WslConfigEditor.HasMirroredNetworking(content));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[wsl2]\nmemory=8GB\n")]
    [InlineData("[wsl2]\nnetworkingMode=nat\n")]
    [InlineData("[wsl2]\n# networkingMode=mirrored\n")] // commented out
    public void HasMirroredNetworking_Rejects_Absent_Or_Disabled_Configuration(string? content)
    {
        Assert.False(WslConfigEditor.HasMirroredNetworking(content));
    }

}
