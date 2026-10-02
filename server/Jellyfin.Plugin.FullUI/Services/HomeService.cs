using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Services;

public sealed record MyServerResponse(
    IReadOnlyList<ItemCard> ContinueWatching,
    IReadOnlyList<ItemCard> MyList,
    IReadOnlyList<ComingSoonCard> Wanted);

/// <summary>Per-user cached Home composition: runs the engine and maps its output to DTOs.</summary>
public sealed class HomeService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ILogger<HomeService> _log;
    private readonly ConcurrentDictionary<Guid, (DateTime At, HomeResponse Home)> _cache = new();

    public HomeService(PluginStore store, ICatalog catalog, ILogger<HomeService> log)
    {
        _store = store;
        _catalog = catalog;
        _log = log;
        _catalog.Changed += (_, _) => Invalidate();
    }

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

    public HomeResponse GetHome(Guid userId)
    {
        if (_cache.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < Ttl)
        {
            return hit.Home;
        }

        try
        {
            var home = Compose(userId);
            _cache[userId] = (DateTime.UtcNow, home);
            return home;
        }
        catch (Exception ex)
        {
            // Never surface raw errors: an empty home is rendered as "nothing to show yet" and is not cached.
            _log.LogError(ex, "FullUI: building home for {User} failed", userId);
            var cfg = Plugin.Instance?.Configuration;
            return new HomeResponse(
                string.IsNullOrWhiteSpace(cfg?.ServerName) ? "FullUI" : cfg!.ServerName,
                cfg?.AccentColor ?? "#e50914",
                Array.Empty<HomeRow>());
        }
    }

    /// <summary>The card for a single title, or null when it does not exist or the user may not see it.</summary>
    public ItemCard? GetItem(Guid userId, Guid itemId)
    {
        try
        {
            return GetItemCore(userId, itemId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: loading item {Item} for {User} failed", itemId, userId);
            return null;
        }
    }

    private ItemCard? GetItemCore(Guid userId, Guid itemId)
    {
        var item = _catalog.All.FirstOrDefault(c => c.Id == itemId);
        if (item is null || !_catalog.VisibleTo(userId).Contains(itemId))
        {
            return null;
        }

        var home = GetHome(userId);
        var inRow = home.Rows.SelectMany(r => r.Items).FirstOrDefault(c => c.Id == itemId.ToString("N"));
        return _store.Read(d => CardMapper.ToCard(
            item,
            inRow?.Badges ?? RecEngine.Badges(item, DateTime.UtcNow),
            inRow?.Rank,
            inRow?.Progress,
            d.Ratings.GetValueOrDefault(StoreData.UserItemKey(userId, itemId)),
            d.MyList.Contains(StoreData.UserItemKey(userId, itemId)),
            d));
    }

    public MyServerResponse GetMyServer(Guid userId)
    {
        try
        {
            return GetMyServerCore(userId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: building My Server for {User} failed", userId);
            return new MyServerResponse(Array.Empty<ItemCard>(), Array.Empty<ItemCard>(), Array.Empty<ComingSoonCard>());
        }
    }

    private MyServerResponse GetMyServerCore(Guid userId)
    {
        var home = GetHome(userId);
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
        var cfg = Plugin.Instance?.Configuration;
        var serverName = string.IsNullOrWhiteSpace(cfg?.ServerName) ? "FullUI" : cfg!.ServerName;
        var excluded = (cfg?.ExcludedUserIds ?? Array.Empty<string>())
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();

        var catalog = _catalog.All;
        var visible = _catalog.VisibleTo(userId);
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
                TopTenWindowDays = cfg?.TopTenWindowDays > 0 ? cfg.TopTenWindowDays : 7,
                ExcludedUsers = excluded,
                RowTitles = new Dictionary<string, string>(d.RowTitles),
            };
            return inp;
        });

        var rows = RecEngine.Build(input);
        var result = new List<HomeRow>();
        HomeRow? comingSoon = null;
        try
        {
            comingSoon = ComingSoonRow(userId, cfg?.TmdbApiKey);
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
                        d)).ToList();
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

        return new HomeResponse(serverName, cfg?.AccentColor ?? "#e50914", result);
    }

    /// <summary>Reads what the discovery task stored; only shown with >=3 entries and a TMDB key configured.</summary>
    private HomeRow? ComingSoonRow(Guid userId, string? tmdbKey)
    {
        if (string.IsNullOrWhiteSpace(tmdbKey))
        {
            return null;
        }

        var cards = _store.Read(d =>
        {
            if (!d.ComingSoon.TryGetValue(userId.ToString("N"), out var entries))
            {
                return new List<ComingSoonCard>();
            }

            var votes = d.Votes.Where(v => v.UserId == userId)
                .GroupBy(v => (v.MediaType, v.TmdbId))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.At).First().Vote);
            return entries
                .Select(e => (Entry: e, Vote: votes.GetValueOrDefault((e.MediaType, e.TmdbId))))
                .Where(x => x.Vote != -1) // "not for me" titles are not shown again
                .OrderByDescending(x => x.Entry.Score)
                .Select(x => CardMapper.ToCard(x.Entry, x.Vote))
                .ToList();
        });

        return cards.Count >= 3
            ? new HomeRow("comingsoon", "Coming Soon", "comingsoon", Array.Empty<ItemCard>(), cards)
            : null;
    }
}
