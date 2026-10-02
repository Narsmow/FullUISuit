using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>Personalised home, ratings, My List and the "My Server" tab.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class HomeController : ControllerBase
{
    // Jellyfin's own formatter is PascalCase; the contract is camelCase, so serialize explicitly.
    private static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    private readonly HomeService _home;
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<HomeController> _log;

    public HomeController(
        HomeService home,
        PluginStore store,
        ICatalog catalog,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        ILogger<HomeController> log)
    {
        _home = home;
        _store = store;
        _catalog = catalog;
        _library = library;
        _users = users;
        _userData = userData;
        _log = log;
    }

    [HttpGet("Home")]
    public IActionResult GetHome() =>
        Safe("load your home page", () => CurrentUserId() is Guid uid ? Camel(_home.GetHome(uid)) : Unauthorized());

    [HttpPost("Rate")]
    public IActionResult Rate([FromBody] RateRequest request)
        => Safe("save your rating", () =>
        {
            if (CurrentUserId() is not Guid uid)
            {
                return Unauthorized();
            }

            if (request.Rating is < -1 or > 2)
            {
                return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid rating", Detail = "Rating must be -1, 0, 1 or 2." });
            }

            if (!IsVisible(uid, request.ItemId))
            {
                return NotFound();
            }

            var key = StoreData.UserItemKey(uid, request.ItemId);
            var previous = _store.Read(d => d.Ratings.GetValueOrDefault(key));
            _store.Write(d =>
            {
                if (request.Rating == 0)
                {
                    d.Ratings.Remove(key);
                }
                else
                {
                    d.Ratings[key] = request.Rating;
                }
            });

            // Love also sets the Jellyfin favorite; moving away from love clears it again.
            if (request.Rating == 2 || previous == 2)
            {
                SetFavorite(uid, request.ItemId, request.Rating == 2);
            }

            _home.Invalidate(uid);
            return NoContent();
        });

    [HttpPost("MyList")]
    public IActionResult MyList([FromBody] MyListRequest request)
        => Safe("update your list", () =>
        {
            if (CurrentUserId() is not Guid uid)
            {
                return Unauthorized();
            }

            if (!IsVisible(uid, request.ItemId))
            {
                return NotFound();
            }

            var key = StoreData.UserItemKey(uid, request.ItemId);
            _store.Write(d =>
            {
                if (request.Add)
                {
                    d.MyList.Add(key);
                }
                else
                {
                    d.MyList.Remove(key);
                }
            });
            _home.Invalidate(uid);
            return NoContent();
        });

    [HttpGet("Item/{id}")]
    public IActionResult GetItem(Guid id)
        => Safe("load this title", () =>
        {
            if (CurrentUserId() is not Guid uid)
            {
                return Unauthorized();
            }

            var card = _home.GetItem(uid, id);
            return card is null ? NotFound() : Camel(card);
        });

    [HttpGet("MyServer")]
    public IActionResult MyServer()
        => Safe("load your page", () =>
        {
            if (CurrentUserId() is not Guid uid)
            {
                return Unauthorized();
            }

            var r = _home.GetMyServer(uid);
            return Camel(new { continueWatching = r.ContinueWatching, myList = r.MyList, wanted = r.Wanted });
        });
    /// <summary>Runs an action, turning any failure into a short plain-English message (details only go to the log).</summary>
    private IActionResult Safe(string what, Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: could not {Action}", what);
            return new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Something went wrong",
                Detail = $"Sorry, we couldn't {what} right now. Please try again in a moment.",
            })
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }
    }

    private IActionResult Camel(object value) => new JsonResult(value, CamelCase);

    private Guid? CurrentUserId()
    {
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out var id) && id != Guid.Empty ? id : null;
    }

    private bool IsVisible(Guid userId, Guid itemId) => _catalog.VisibleTo(userId).Contains(itemId);

    private void SetFavorite(Guid userId, Guid itemId, bool favorite)
    {
        try
        {
            var user = _users.GetUserById(userId);
            var item = _library.GetItemById(itemId);
            if (user is null || item is null)
            {
                return;
            }

            var data = _userData.GetUserData(user, item);
            if (data is null || data.IsFavorite == favorite)
            {
                return;
            }

            data.IsFavorite = favorite;
            _userData.SaveUserData(user, item, data, MediaBrowser.Model.Entities.UserDataSaveReason.UpdateUserRating, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The rating itself is saved; a failed favorite sync must not turn that into an error.
            _log.LogWarning(ex, "FullUI: could not sync favorite for {Item}", itemId);
        }
    }
}
