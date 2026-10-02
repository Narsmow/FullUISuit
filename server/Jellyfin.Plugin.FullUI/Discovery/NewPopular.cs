using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// What the "Everyone's Watching" and Top 10 lists on the New &amp; Popular page are made of. Every id returned is visible to
/// <c>userId</c>. The default implementation reads play signals directly; the recommendation engine can register its own
/// (registered first or last, whichever the container resolves) when it has a better one.
/// </summary>
public interface ITrendingProvider
{
    /// <summary>Titles many household members are watching right now, most popular first.</summary>
    IReadOnlyList<Guid> Trending(Guid userId, int take);

    /// <summary>The Top 10 (movies or shows) for the current window, rank 1 first.</summary>
    IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind);
}

/// <summary>Simple fallback: popularity from the stored play signals, restricted to what the caller may see.</summary>
public sealed class SignalTrendingProvider : ITrendingProvider
{
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IConfigSource _config;
    private readonly IUserDirectory _users;

    public SignalTrendingProvider(PluginStore store, ICatalog catalog, IConfigSource config, IUserDirectory users)
    {
        _store = store;
        _catalog = catalog;
        _config = config;
        _users = users;
    }

    public IReadOnlyList<Guid> Trending(Guid userId, int take)
        => Rank(userId, TimeSpan.FromDays(7), kind: null, take);

    public IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind)
    {
        var days = Math.Clamp(_config.Current.TopTenWindowDays, 1, 90);
        return Rank(userId, TimeSpan.FromDays(days), kind, 10);
    }

    private IReadOnlyList<Guid> Rank(Guid userId, TimeSpan window, CatalogKind? kind, int take)
    {
        var cfg = _config.Current;
        var excluded = new HashSet<string>(cfg.ExcludedUserIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var visible = _catalog.VisibleTo(userId);
        var byId = _catalog.All.Where(i => visible.Contains(i.Id) && (kind is null || i.Kind == kind)).ToDictionary(i => i.Id);
        if (byId.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var cutoff = DateTime.UtcNow - window;
        var restrictedCache = new Dictionary<Guid, bool>();
        bool Skip(Guid u)
        {
            if (excluded.Contains(u.ToString("N")) || excluded.Contains(u.ToString()))
            {
                return true;
            }

            if (!cfg.ExcludeKidsFromSharedSignals)
            {
                return false;
            }

            if (!restrictedCache.TryGetValue(u, out var r))
            {
                restrictedCache[u] = r = _users.MaxParentalRatingScore(u) is not null;
            }

            return r;
        }

        var (signals, myRatings) = _store.Read(d => (
            d.Signals.Where(s => s.At >= cutoff && byId.ContainsKey(s.ItemId)).ToList(),
            d.Ratings.Where(kv => kv.Value < 0 && kv.Key.StartsWith(userId.ToString("N") + "|", StringComparison.Ordinal)).Select(kv => kv.Key).ToHashSet()));
        var score = new Dictionary<Guid, (HashSet<Guid> Users, int Plays)>();
        foreach (var s in signals)
        {
            if (Skip(s.UserId))
            {
                continue;
            }

            if (!score.TryGetValue(s.ItemId, out var e))
            {
                score[s.ItemId] = e = (new HashSet<Guid>(), 0);
            }

            e.Users.Add(s.UserId);
            score[s.ItemId] = (e.Users, e.Plays + 1);
        }

        return score
            .Where(kv => !myRatings.Contains(StoreData.UserItemKey(userId, kv.Key)))
            .OrderByDescending(kv => kv.Value.Users.Count)
            .ThenByDescending(kv => kv.Value.Plays)
            .ThenByDescending(kv => byId[kv.Key].Rating ?? 0)
            .ThenBy(kv => byId[kv.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(kv => kv.Key)
            .ToList();
    }
}

public sealed record NewPopularResponse(
    IReadOnlyList<ComingSoonCard> ComingSoon,
    IReadOnlyList<ItemCard> EveryonesWatching,
    IReadOnlyList<ItemCard> Top10Movies,
    IReadOnlyList<ItemCard> Top10Shows);

/// <summary>Builds the New &amp; Popular page for one user.</summary>
public sealed class NewPopularService
{
    private const int EveryonesWatchingSize = 20;

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ITrendingProvider _trending;

    public NewPopularService(PluginStore store, ICatalog catalog, ITrendingProvider trending)
    {
        _store = store;
        _catalog = catalog;
        _trending = trending;
    }

    public NewPopularResponse Build(Guid userId, bool tmdbConfigured, DateTime now)
    {
        var library = _catalog.All;
        var libraryKeys = ComingSoonView.LibraryKeys(library);
        var byId = library.ToDictionary(i => i.Id, i => i);
        var watching = Safe(() => _trending.Trending(userId, EveryonesWatchingSize));
        var movies = Safe(() => _trending.TopTen(userId, CatalogKind.Movie));
        var shows = Safe(() => _trending.TopTen(userId, CatalogKind.Series));
        var visible = _catalog.VisibleTo(userId);

        return _store.Read(d =>
        {
            List<ItemCard> Cards(IReadOnlyList<Guid> ids, bool ranked)
            {
                var list = new List<ItemCard>();
                foreach (var id in ids)
                {
                    // Defensive: the provider is replaceable, so re-check visibility here instead of trusting it.
                    if (!visible.Contains(id) || !byId.TryGetValue(id, out var item))
                    {
                        continue;
                    }

                    var key = StoreData.UserItemKey(userId, id);
                    list.Add(CardMapper.ToCard(item, RecEngine.Badges(item, now), ranked ? list.Count + 1 : null, null, d.Ratings.GetValueOrDefault(key), d.MyList.Contains(key), d));
                }

                return list;
            }

            var coming = tmdbConfigured
                ? ComingSoonView.Cards(d, userId, libraryKeys, now, ComingSoonKind.Upcoming)
                    .OrderBy(c => c.ReleaseDate, StringComparer.Ordinal)
                    .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new List<ComingSoonCard>();
            return new NewPopularResponse(coming, Cards(watching, false), Cards(movies, true), Cards(shows, true));
        });
    }

    private static IReadOnlyList<Guid> Safe(Func<IReadOnlyList<Guid>> f)
    {
        try
        {
            return f();
        }
        catch (Exception)
        {
            return Array.Empty<Guid>(); // one broken list never takes the page down
        }
    }
}
