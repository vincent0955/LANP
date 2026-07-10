using GameDashboard.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameDashboard.Api.Controllers;

/// <summary>
/// See requirements.md → Req 11 (game library catalog).
/// </summary>
[ApiController]
[Route("api/games")]
public sealed class GamesController : ControllerBase
{
    private readonly IGameCatalogService _catalogService;

    public GamesController(IGameCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    [HttpGet]
    public IActionResult GetGames([FromQuery] string? search)
    {
        var games = _catalogService.GetGames(search);
        return Ok(games);
    }
}
