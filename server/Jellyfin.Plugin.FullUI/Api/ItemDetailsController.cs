using System;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>The in-app title modal: seasons, episodes, cast, trailers and "More like this" for one title.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class ItemDetailsController : ControllerBase
{
    private readonly ItemDetailsService _details;
    private readonly ILogger<ItemDetailsController> _log;

    public ItemDetailsController(ItemDetailsService details, ILogger<ItemDetailsController> log)
    {
        _details = details;
        _log = log;
    }

    /// <summary>404 when the title does not exist or the caller may not see it; a problem body (500) when the library cannot be read.</summary>
    [HttpGet("Item/{id}/Details")]
    public IActionResult Details(Guid id)
    {
        if (!UserClaim.TryGet(User, out var userId))
        {
            return Unauthorized();
        }

        try
        {
            return _details.Get(userId, id) is { } result ? SafeApi.Json(result) : NotFound();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: could not load the details of {Item}", id);
            Ops.ErrorLog.Add("Something went wrong while loading a title page.");
            return SafeApi.Problem(
                StatusCodes.Status500InternalServerError,
                "Something went wrong",
                "Sorry, we couldn't load this title right now. Please try again in a moment.");
        }
    }
}
