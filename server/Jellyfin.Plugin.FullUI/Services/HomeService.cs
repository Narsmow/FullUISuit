using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Services;

public sealed record MyServerResponse(
    IReadOnlyList<ItemCard> ContinueWatching,
    IReadOnlyList<ItemCard> MyList,
    IReadOnlyList<ComingSoonCard> Wanted);

/// <summary>
/// Per-user cached Home composition: runs the engine and maps its output to DTOs.
/// The <c>*Strict</c> methods throw when the library cannot be read (the API turns that into a 5xx so clients can
/// tell "broken" from "empty"); the plain methods swallow the failure and return an empty result for background callers.
/// </summary>
public sealed class HomeService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ILogger<HomeService> _log;
    private readonly IConfigSource? _config;
    private readonly INextUpSource? _nextUp;
    private readonly ConcurrentDictionary<Guid, (DateTime At, HomeResponse Home)> _cache = new();

    public HomeService(PluginStore store, ICatalog catalog, ILogger<HomeService> log, IConfigSource? config = null, INextUpSource? nextUp = null)
    {
        _store = store;
        _catalog = catalog;
        _log = log;
        _config = config;
        _nextUp = nextUp;
        _catalog.Changed += (_, _) => Invalidate();
    }

    private PluginConfiguration? Cfg => _config?.Current ?? Plugin.Instance?.Configuration;

    /// <summary>Drops one user's cached home (or everyone's when null).</summary>
    public void Invalidate(Guid? userId = null)
    {
        if (userId is Guid id)
        {
            _cache.TryRemove(id, out _);
        }
        else
        {
            _cache.Clear();
        }
    }

    /// <summary>The user's home, or an empty one (never cached) when it cannot be built. For background callers.</summary>
    public HomeResponse GetHome(Guid userId)
    {
        try
        {
            return GetHomeStrict(userId);
        }
        catch (Exception ex)
        {
            // Never surface raw errors from here: an empty home is rendered as "nothing to show yet" and is not cached.
            _log.LogError(ex, "FullUI: building home for {User} failed", userId);
            var cfg = Cfg;
            return new HomeResponse(
                string.IsNullOrWhiteSpace(cfg?.ServerName) ? "FullUI" : cfg!.ServerName,
                Branding.NormalizeAccent(cfg?.AccentColor),
                Array.Empty<HomeRow>());
        }
    }

    /// <summary>The user's home. Throws when it cannot be built, so the API can answer 5xx instead of an empty 200.</summary>
    public HomeResponse GetHomeStrict(Guid userId)
    {
        if (_cache.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < Ttl)
        {
            return hit.Home;
        }

        var home = Compose(userId);
        _cache[userId] = (DateTime.UtcNow, home);
        return home;
    }

    /// <summary>The card for a single title, or null when it does not exist or the user may not see it.</summary>
    public ItemCard? GetItem(Guid userId, Guid itemId)
    {
        try
        {
            return GetItemStrict(userId, itemId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: loading item {Item} for {User} failed", itemId, userId);
            return null;
        }
    }

    /// <summary>Like <see cref="GetItem"/> but throws on a library failure (null still means "not found / not visible").</summary>
    public ItemCard? GetItemStrict(Guid userId, Guid itemId)
    {
        var item = _catalog.All.FirstOrDefault(c => c.Id == itemId);
        if (item is null || !_catalog.VisibleTo(userId).Contains(itemId))
        {
            return null;
        }

        var home = GetHome(userId);
        var inRow = home.Rows.SelectMany(r => r.Items).FirstOrDefault(c => c.Id == itemId.ToString("N"));
        var now = DateTime.UtcNow;
        return _store.Read(d =>
        {
            var key = StoreData.UserItemKey(userId, itemId);
            var progress = inRow?.Progress;
            if (inRow is null)
            {
                // Not in any Home row: still report resume progress so More Info can show it.
                var latest = d.Signals.Where(s => s.UserId == userId && s.ItemId == itemId).OrderByDescending(s => s.At).FirstOrDefault();
                progress = latest is null ? null : RecEngine.ResumeProgress(latest, now);
            }

            return CardMapper.ToCard(
                item,
                inRow?.Badges ?? RecEngine.Badges(item, now),
                inRow?.Rank,
                progress,
                d.Ratings.GetValueOrDefault(key),
                d.MyList.Contains(key),
                d);
        });
    }

    public MyServerResponse GetMyServer(Guid userId)
    {
        try
        {
            return GetMyServerStrict(userId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: building My Server for {User} failed", userId);
            return new MyServerResponse(Array.Empty<ItemCard>(), Array.Empty<ItemCard>(), Array.Empty<ComingSoonCard>());
        }
    }

    public MyServerResponse GetMyServerStrict(Guid userId)
    {
        var home = GetHomeStrict(userId);
        IReadOnlyList<ItemCard> Items(string id) => home.Rows.FirstOrDefault(r => r.Id == id)?.Items ?? Array.Empty<ItemCard>();

        var wanted = _store.Read(d => d.Votes
            .Where(v => v.UserId == userId && v.Vote == 1)
            .OrderByDescending(v => v.At)
            .Select(v => CardMapper.ToCard(v, d))
            .ToList());
        return new MyServerResponse(Items("continue"), Items("mylist"), wanted);
    }

    private HomeResponse Compose(Guid userId)
    {
        var cfg = Cfg;
        var serverName = string.IsNullOrWhiteSpace(cfg?.ServerName) ? "FullUI" : cfg!.ServerName;
        var excluded = (cfg?.ExcludedUserIds ?? Array.Empty<string>())
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();

        var catalog = _catalog.All;
        var visible = _catalog.VisibleTo(userId);
        var nextUp = _nextUp?.NextUpSeries(userId) ?? Array.Empty<Guid>();
        var input = _store.Read(d =>
        {
            var inp = new RecInput
            {
                UserId = userId,
                Catalog = catalog,
                Visible = visible,
                Signals = d.Signals.ToList(),
                Ratings = new Dictionary<string, int>(d.Ratings),
                MyList = new HashSet<string>(d.MyList),
                Now = DateTime.UtcNow,
                ServerName = serverName,
                TopTenWindowDays = cfg?.TopTenWindowDays > 0 ? Math.Min(cfg.TopTenWindowDays, 90) : 7,
                ExcludedUsers = excluded,
                RowTitles = new Dictionary<string, string>(d.RowTitles),
                NextUpSeries = nextUp,
            };
            return inp;
        });

        var rows = RecEngine.Build(input);
        var result = new List<HomeRow>();
        HomeRow? comingSoon = null;
        try
        {
            comingSoon = ComingSoonRow(userId, cfg?.TmdbApiKey, catalog);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: Coming Soon row skipped for {User}", userId);
        }

        var inserted = comingSoon is null;

        _store.Read(d =>
        {
            foreach (var row in rows)
            {
                if (!inserted && row.Order > RecEngine.OrderComingSoon)
                {
                    result.Add(comingSoon!);
                    inserted = true;
                }

                try
                {
                    var cards = row.Items.Select(r => CardMapper.ToCard(
                        r.Item,
                        r.Badges,
                        r.Rank,
                        r.Progress,
                        d.Ratings.GetValueOrDefault(StoreData.UserItemKey(userId, r.Item.Id)),
                        d.MyList.Contains(StoreData.UserItemKey(userId, r.Item.Id)),
                        d)).DistinctBy(c => c.Id).ToList();
                    result.Add(new HomeRow(row.Id, row.Title, row.Type, cards));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "FullUI: row {Row} skipped for {User}", row.Id, userId);
                }
            }

            return 0;
        });

        if (!inserted)
        {
            result.Add(comingSoon!);
        }

        // Clients key their lists on the row id; a duplicate crashes some of them, so enforce uniqueness here.
        var unique = result.DistinctBy(r => r.Id).ToList();
        return new HomeResponse(serverName, Branding.NormalizeAccent(cfg?.AccentColor), unique);
    }

    /// <summary>Reads what the discovery task stored; only shown with >=3 entries and a TMDB key configured.</summary>
    private HomeRow? ComingSoonRow(Guid userId, string? tmdbKey, IReadOnlyList<CatalogItem> catalog)
    {
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            return null;
        }

        var libraryKeys = ComingSoonView.LibraryKeys(catalog);
        var cards = _store.Read(d => ComingSoonView.Cards(d, userId, libraryKeys));
        return cards.Count >= 3
            ? new HomeRow("comingsoon", "Coming Soon", "comingsoon", Array.Empty<ItemCard>(), cards)
            : null;
    }
}
