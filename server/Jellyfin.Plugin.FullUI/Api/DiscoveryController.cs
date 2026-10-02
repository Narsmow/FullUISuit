using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.AspNetCore.Authorization;
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
    public ActionResult<ComingSoonResponse> ComingSoon()
        => SafeApi.Run(this, _log, "loading Coming Soon", ComingSoonCore);

    private ActionResult<ComingSoonResponse> ComingSoonCore()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!_tmdb.Configured)
        {
            return new ComingSoonResponse(Array.Empty<ComingSoonCard>());
        }

        var mine = _votes.MyVotes(userId);
        var inLibrary = _catalog.All.Where(i => i.TmdbId is > 0)
            .Select(i => ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value)).ToHashSet();
        var entries = _store.Read(d => d.ComingSoon.TryGetValue(userId.ToString("N"), out var l) ? l.ToList() : new List<ComingSoonEntry>());
        var cards = entries
            .Where(e => !inLibrary.Contains(ComingSoonRanker.Key(e.MediaType, e.TmdbId)))
            .Select(e => new ComingSoonCard(
                e.TmdbId,
                e.MediaType,
                e.Title,
                e.Overview,
                e.PosterPath,
                e.BackdropPath,
                e.ReleaseDate,
                e.TrailerKey,
                mine.GetValueOrDefault(VoteService.StatusKey(e.MediaType, e.TmdbId))))
            .ToList();
        return new ComingSoonResponse(cards);
    }

    [HttpPost("Vote")]
    public ActionResult Vote([FromBody] VoteRequest? request)
        => SafeApi.Run(this, _log, "saving your vote", () => VoteCore(request));

    private ActionResult VoteCore(VoteRequest? request)
    {
        if (request is null)
        {
            return BadRequest(new ApiError("That vote could not be read."));
        }

        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        return _votes.Cast(userId, request) == VoteResult.Ok ? NoContent() : BadRequest(new ApiError("That vote was not valid."));
    }

    [HttpGet("Notifications")]
    public ActionResult<NotificationsResponse> Notifications()
        => SafeApi.Run(this, _log, "loading notifications", NotificationsCore);

    private ActionResult<NotificationsResponse> NotificationsCore()
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var items = _store.Read(d => d.Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.At).Take(50)
            .Select(n => new NotificationDto(n.Id, n.Text, n.At, n.Read, n.ItemId?.ToString("N"))).ToList());
        return new NotificationsResponse(items);
    }

    [HttpPost("Notifications/Read")]
    public ActionResult MarkRead([FromBody] MarkReadRequest? request)
        => SafeApi.Run(this, _log, "updating notifications", () => MarkReadCore(request));

    private ActionResult MarkReadCore(MarkReadRequest? request)
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
    public Task<ActionResult<SearchResponse>> Search([FromQuery] string? q, CancellationToken ct)
        => SafeApi.RunAsync(this, _log, "searching", () => SearchCore(q, ct));

    private async Task<ActionResult<SearchResponse>> SearchCore(string? q, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        q = q?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return new SearchResponse("keyword", Array.Empty<ItemCard>());
        }

        if (q.Length > 200)
        {
            q = q[..200];
        }

        var (mode, hits) = await _search.SearchAsync(userId, q, ct).ConfigureAwait(false);
        var (ratings, list) = _store.Read(d => (
            d.Ratings.Where(kv => kv.Key.StartsWith(userId.ToString("N") + "|", StringComparison.Ordinal)).ToDictionary(kv => kv.Key, kv => kv.Value),
            d.MyList.Where(k => k.StartsWith(userId.ToString("N") + "|", StringComparison.Ordinal)).ToHashSet()));
        var cards = hits.Select(h => ToCard(h.Item, userId, ratings, list)).ToList();
        return new SearchResponse(mode, cards);
    }

    private static ItemCard ToCard(CatalogItem i, Guid userId, Dictionary<string, int> ratings, HashSet<string> myList)
    {
        var key = StoreData.UserItemKey(userId, i.Id);
        return new ItemCard(
            i.Id.ToString("N"),
            i.Name,
            i.Kind == CatalogKind.Series ? "Series" : "Movie",
            i.Year,
            i.Rating,
            i.OfficialRating,
            i.Overview,
            i.Genres.ToArray(),
            i.RuntimeMinutes,
            Array.Empty<string>(),
            i.TrailerKey,
            i.TmdbId,
            null,
            i.HasBackdrop,
            i.HasLogo,
            ratings.GetValueOrDefault(key),
            myList.Contains(key),
            null);
    }

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var claim = User.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out userId) && userId != Guid.Empty;
    }
}
