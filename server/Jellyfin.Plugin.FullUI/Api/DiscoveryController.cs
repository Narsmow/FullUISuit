using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Ai;
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

[ApiController]
[Route("FullUI")]
[Authorize]
public class DiscoveryController : ControllerBase
{
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly VoteService _votes;
    private readonly NlSearch _search;
    private readonly IConfigSource _config;
    private readonly ITmdbClient _tmdb;
    private readonly ILogger<DiscoveryController> _log;

    public DiscoveryController(PluginStore store, ICatalog catalog, VoteService votes, NlSearch search, IConfigSource config, ITmdbClient tmdb, ILogger<DiscoveryController> log)
    {
        _store = store;
        _catalog = catalog;
        _votes = votes;
        _search = search;
        _config = config;
        _tmdb = tmdb;
        _log = log;
    }

    public sealed record ComingSoonResponse(IReadOnlyList<ComingSoonCard> Cards);

    public sealed record NotificationsResponse(IReadOnlyList<NotificationDto> Items);

    [HttpGet("ComingSoon")]
    public IActionResult ComingSoon()
        => SafeApi.Run(_log, "loading Coming Soon", ComingSoonCore);

    private IActionResult ComingSoonCore()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!_tmdb.Configured)
        {
            return SafeApi.Json(new ComingSoonResponse(Array.Empty<ComingSoonCard>()));
        }

        var inLibrary = ComingSoonView.LibraryKeys(_catalog.All);
        var cards = _store.Read(d => ComingSoonView.Cards(d, userId, inLibrary));
        return SafeApi.Json(new ComingSoonResponse(cards));
    }

    [HttpPost("Vote")]
    public IActionResult Vote([FromBody] VoteRequest? request)
        => SafeApi.Run(_log, "saving your vote", () => VoteCore(request));

    private IActionResult VoteCore(VoteRequest? request)
    {
        if (request is null)
        {
            return SafeApi.Error(StatusCodes.Status400BadRequest, "That vote could not be read.");
        }

        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return _votes.Cast(userId, request) switch
        {
            VoteResult.Ok => NoContent(),
            VoteResult.LimitReached => SafeApi.Error(StatusCodes.Status400BadRequest, $"You have reached the limit of {VoteService.MaxVotesPerUser} votes. Remove an older vote first."),
            _ => SafeApi.Error(StatusCodes.Status400BadRequest, "That vote was not valid."),
        };
    }

    [HttpGet("Notifications")]
    public IActionResult Notifications()
        => SafeApi.Run(_log, "loading notifications", NotificationsCore);

    private IActionResult NotificationsCore()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var items = _store.Read(d => d.Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.At).Take(50)
            .Select(n => new NotificationDto(n.Id, n.Text, n.At, n.Read, n.ItemId?.ToString("N"))).ToList());
        return SafeApi.Json(new NotificationsResponse(items));
    }

    [HttpPost("Notifications/Read")]
    public IActionResult MarkRead([FromBody] MarkReadRequest? request)
        => SafeApi.Run(_log, "updating notifications", () => MarkReadCore(request));

    private IActionResult MarkReadCore(MarkReadRequest? request)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var ids = request?.Ids is { Length: > 0 } a ? a.ToHashSet() : null;
        _store.Write(d =>
        {
            foreach (var n in d.Notifications)
            {
                if (n.UserId == userId && (ids is null || ids.Contains(n.Id)))
                {
                    n.Read = true;
                }
            }
        });
        return NoContent();
    }

    [HttpGet("Search")]
    public Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
        => SafeApi.RunAsync(_log, "searching", () => SearchCore(q, ct));

    private async Task<IActionResult> SearchCore(string? q, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return SafeApi.Json(new SearchResponse("keyword", Array.Empty<ItemCard>()));
        }

        if (q.Length > 200)
        {
            q = q[..200];
        }

        var (mode, hits) = await _search.SearchAsync(userId, q, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var cards = _store.Read(d => hits.Select(h =>
        {
            var key = StoreData.UserItemKey(userId, h.Item.Id);
            return CardMapper.ToCard(
                h.Item,
                RecEngine.Badges(h.Item, now),
                null,
                null,
                d.Ratings.GetValueOrDefault(key),
                d.MyList.Contains(key),
                d);
        }).ToList());
        return SafeApi.Json(new SearchResponse(mode, cards));
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out userId) && userId != Guid.Empty;
    }
}
