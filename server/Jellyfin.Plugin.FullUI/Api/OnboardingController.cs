using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>Body of <c>POST Onboarding</c>. Ids may be written with or without dashes.</summary>
public sealed record OnboardingSubmitRequest(string[]? Liked, string[]? Genres, bool Skipped);

public sealed record OnboardingResponse(bool Eligible, IReadOnlyList<ItemCard> Items, IReadOnlyList<string> Genres);

public sealed record ItemIdRequest(string? ItemId);

/// <summary>First-run onboarding and "remove from Continue Watching". Per signed-in user.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class OnboardingController : ControllerBase
{
    private readonly OnboardingService _onboarding;
    private readonly HiddenItemsService _hidden;
    private readonly ICatalog _catalog;
    private readonly PluginStore _store;
    private readonly IHomeInvalidator _home;
    private readonly ILogger<OnboardingController> _log;

    public OnboardingController(OnboardingService onboarding, HiddenItemsService hidden, ICatalog catalog, PluginStore store, IHomeInvalidator home, ILogger<OnboardingController> log)
    {
        _onboarding = onboarding;
        _hidden = hidden;
        _catalog = catalog;
        _store = store;
        _home = home;
        _log = log;
    }

    /// <summary>
    /// <c>eligible</c> is true for a user who has not finished onboarding and has fewer than 5 titles of history. For anyone else
    /// <c>items</c> is empty, unless the user re-opens the picker on purpose with <c>?force=true</c>.
    /// </summary>
    [HttpGet("Onboarding")]
    public IActionResult Get([FromQuery] bool force = false)
        => SafeApi.Run(_log, "loading the picker", () =>
        {
            if (!UserClaim.TryGet(User, out var userId))
            {
                return Unauthorized();
            }

            var eligible = _onboarding.IsEligible(userId);
            if (!eligible && !force)
            {
                return SafeApi.Json(new OnboardingResponse(false, Array.Empty<ItemCard>(), Array.Empty<string>()));
            }

            var now = Clock.UtcNow;
            var items = _onboarding.Suggestions(userId);
            var visible = _catalog.VisibleTo(userId);
            var genres = _catalog.All.Where(i => visible.Contains(i.Id)).SelectMany(i => i.Genres).Where(g => !string.IsNullOrWhiteSpace(g))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(24).Select(g => g.Key).ToList();
            var cards = _store.Read(d => items.Select(i =>
            {
                var key = StoreData.UserItemKey(userId, i.Id);
                return CardMapper.ToCard(i, RecEngine.Badges(i, now), null, null, d.Ratings.GetValueOrDefault(key), d.MyList.Contains(key), d);
            }).ToList());
            return SafeApi.Json(new OnboardingResponse(eligible, cards, genres));
        });

    [HttpPost("Onboarding")]
    public IActionResult Post([FromBody] OnboardingSubmitRequest? request)
        => SafeApi.Run(_log, "saving your picks", () =>
        {
            if (!UserClaim.TryGet(User, out var userId))
            {
                return Unauthorized();
            }

            if (request is null)
            {
                return SafeApi.Error(StatusCodes.Status400BadRequest, "Those picks could not be read.");
            }

            var outcome = _onboarding.Submit(userId, request.Liked, request.Genres, request.Skipped, DateTime.UtcNow);
            _home.Invalidate(userId);
            return SafeApi.Json(outcome);
        });

    [HttpPost("ContinueWatching/Hide")]
    public IActionResult Hide([FromBody] ItemIdRequest? request)
        => SafeApi.Run(_log, "removing the title from Continue Watching", () => SetHidden(request, hide: true));

    [HttpPost("ContinueWatching/Unhide")]
    public IActionResult Unhide([FromBody] ItemIdRequest? request)
        => SafeApi.Run(_log, "putting the title back in Continue Watching", () => SetHidden(request, hide: false));

    private IActionResult SetHidden(ItemIdRequest? request, bool hide)
    {
        if (!UserClaim.TryGet(User, out var userId))
        {
            return Unauthorized();
        }

        if (request?.ItemId is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return SafeApi.Error(StatusCodes.Status400BadRequest, "That title could not be read.");
        }

        if (hide)
        {
            if (!_catalog.VisibleTo(userId).Contains(itemId))
            {
                return SafeApi.Error(StatusCodes.Status404NotFound, "That title was not found.");
            }

            if (!_hidden.Hide(userId, itemId))
            {
                return SafeApi.Error(StatusCodes.Status400BadRequest, $"You have already hidden {HiddenItemsService.MaxHiddenPerUser} titles. Put some back first.");
            }
        }
        else
        {
            _hidden.Unhide(userId, itemId);
        }

        _home.Invalidate(userId);
        return NoContent();
    }
}

/// <summary>Typeahead.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class SearchSuggestController : ControllerBase
{
    private readonly SuggestService _suggest;
    private readonly ILogger<SearchSuggestController> _log;

    public SearchSuggestController(SuggestService suggest, ILogger<SearchSuggestController> log)
    {
        _suggest = suggest;
        _log = log;
    }

    [HttpGet("Search/Suggest")]
    public IActionResult Suggest([FromQuery] string? q)
        => SafeApi.Run(_log, "loading suggestions", () =>
        {
            if (!UserClaim.TryGet(User, out var userId))
            {
                return Unauthorized();
            }

            if (q is { Length: > 100 })
            {
                q = q[..100];
            }

            return SafeApi.Json(_suggest.Suggest(userId, q));
        });
}
