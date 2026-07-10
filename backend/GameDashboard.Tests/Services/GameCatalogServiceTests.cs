using GameDashboard.Api.GameTemplates;
using GameDashboard.Api.Services;

namespace GameDashboard.Tests.Services;

public class GameCatalogServiceTests
{
    private readonly GameCatalogService _service = new();

    [Fact]
    public void GetGames_Returns_All_Fifteen_Games_When_No_Search()
    {
        var games = _service.GetGames(null);

        Assert.Equal(15, games.Count);
    }

    [Fact]
    public void GetGames_Returns_All_Games_When_Search_Is_Empty_String()
    {
        var games = _service.GetGames(string.Empty);

        Assert.Equal(15, games.Count);
    }

    [Fact]
    public void GetGames_Returns_All_Games_When_Search_Is_Whitespace()
    {
        var games = _service.GetGames("   ");

        Assert.Equal(15, games.Count);
    }

    [Fact]
    public void GetGames_Results_Are_Sorted_Alphabetically_By_DisplayName()
    {
        var games = _service.GetGames(null);

        var sorted = games.Select(g => g.DisplayName)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(sorted, games.Select(g => g.DisplayName).ToList());
    }

    [Theory]
    [InlineData("minecraft")]
    [InlineData("MINECRAFT")]
    [InlineData("Minecraft")]
    public void GetGames_Search_Is_Case_Insensitive(string search)
    {
        var games = _service.GetGames(search);

        Assert.Single(games);
        Assert.Equal("Minecraft (Java Edition)", games[0].DisplayName);
    }

    [Fact]
    public void GetGames_Search_Matches_Substring_Not_Just_Prefix()
    {
        // "Strike" is a mid-string match for "Counter-Strike 2".
        var games = _service.GetGames("Strike");

        Assert.Contains(games, g => g.DisplayName == "Counter-Strike 2");
    }

    [Fact]
    public void GetGames_Search_With_No_Matches_Returns_Empty_List()
    {
        var games = _service.GetGames("NoSuchGameExists12345");

        Assert.Empty(games);
    }

    [Fact]
    public void GetGameByTag_Returns_Match_For_Known_Tag()
    {
        var game = _service.GetGameByTag(CuratedGameTemplates.Rust.ImageTag);

        Assert.NotNull(game);
        Assert.Equal("Rust", game!.DisplayName);
    }

    [Fact]
    public void GetGameByTag_Returns_Null_For_Unknown_Tag()
    {
        var game = _service.GetGameByTag("some/unknown-image:latest");

        Assert.Null(game);
    }

    [Fact]
    public void GetGames_Includes_All_Three_Original_Curated_Games()
    {
        var games = _service.GetGames(null);
        var names = games.Select(g => g.DisplayName).ToList();

        Assert.Contains("Counter-Strike 2", names);
        Assert.Contains("Insurgency (2014)", names);
        Assert.Contains("Minecraft (Java Edition)", names);
    }
}
