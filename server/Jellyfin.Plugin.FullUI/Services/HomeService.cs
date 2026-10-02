using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Ai;
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
    private readonly IWatchStateSource? _watch;
    private readonly ConcurrentDictionary<Guid, (DateTime At, HomeResponse Home)> _cache = new();
    private readonly UserWeightsCache _weights = new();
    private readonly object _sharedLock = new();
    private (IReadOnlyList<CatalogItem> Catalog, DateTime At, HashSet<Guid> Checked, HashSet<Guid> Kids)? _kids;
    private (IReadOnlyList<CatalogItem> Catalog, string Model, int Count, DateTime At, EmbeddingIndex Index)? _emb;

    public HomeService(PluginStore store, ICatalog catalog, ILogger<HomeService> log, IConfigSource? config = null, INextUpSource? nextUp = null, IWatchStateSource? watch = null)
    {
        _store = store;
        _catalog = catalog;
        _log = log;
        _config = config;
        _nextUp = nextUp;
        _watch = watch;
        _catalog.Changed += (_, _) => Invalidate();
    }

    private PluginConfiguration? Cfg => _config?.Current ?? Plugin.Instance?.Configuration;

    /// <summary>Drops one user's cached home (or everyone's when null).</summary>
    public void Invalidate(Guid? userId = null)
    {
        if (userId is Guid id)
        {
            _cache.TryRemove(id, out _);
            _watch?.Invalidate(id);
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
        if (!CatalogIndex.For(_catalog.All).ById.TryGetValue(itemId, out var item) || !_catalog.VisibleTo(userId).Contains(itemId))
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
                d,
                inRow?.MatchPercent,
                inRow?.Reason,
                inRow?.SeriesLabel,
                inRow?.MinutesLeft);
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
        var nextUpEntries = _nextUp?.NextUpEntries(userId) ?? Array.Empty<NextUpEntry>();
        var nextUp = nextUpEntries.Select(e => e.SeriesId).ToList();
        var nextUpEpisodes = nextUpEntries.Where(e => e.Season is not null || e.Episode is not null)
            .ToDictionary(e => e.SeriesId, e => new NextUpEpisode(e.Season, e.Episode));
        var watch = SafeWatch(userId);
        var now = DateTime.UtcNow;
        var kids = cfg?.ExcludeKidsFromSharedSignals ?? true
            ? KidUsers(catalog, userId)
            : new HashSet<Guid>();
        var embeddings = EmbeddingsFor(catalog, cfg);
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
                Now = now,
                ServerName = serverName,
                TopTenWindowDays = cfg?.TopTenWindowDays > 0 ? Math.Min(cfg.TopTenWindowDays, 90) : 7,
                ExcludedUsers = excluded,
                RowTitles = new Dictionary<string, string>(d.RowTitles),
                NextUpSeries = nextUp,
                NextUpEpisodes = nextUpEpisodes,
                SeriesWatch = watch,
                KidUsers = kids,
                Embeddings = embeddings,
                WeightsCache = _weights,
                DataFingerprint = Fingerprint(d, now),
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
                        d,
                        r.Match,
                        r.Reason,
                        r.SeriesLabel,
                        r.MinutesLeft)).DistinctBy(c => c.Id).ToList();
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

    private IReadOnlyDictionary<Guid, SeriesWatchInfo> SafeWatch(Guid userId)
    {
        try
        {
            return _watch?.SeriesWatch(userId) ?? new Dictionary<Guid, SeriesWatchInfo>();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: watched-episode data unavailable for {User}", userId);
            return new Dictionary<Guid, SeriesWatchInfo>();
        }
    }

    /// <summary>
    /// Changes whenever anything the cached per-user weights depend on changes (new signals, ratings, My List) and hourly
    /// (the weights decay with time). Cheap: counts plus the newest signal time.
    /// </summary>
    private static string Fingerprint(StoreData d, DateTime now)
    {
        var ratings = 0;
        foreach (var (k, v) in d.Ratings)
        {
            ratings ^= HashCode.Combine(k, v);
        }

        var last = d.Signals.Count > 0 ? d.Signals[^1].At.Ticks : 0;
        return $"{d.Signals.Count}:{last}:{d.Ratings.Count}:{ratings}:{d.MyList.Count}:{now:yyyyMMddHH}";
    }

    /// <summary>
    /// Users with a restrictive parental cap (heuristic, see <see cref="KidDetector"/>), for users that have any signal plus the viewer.
    /// Cached for the lifetime of a catalog snapshot, at most two minutes (the visible-title lists change that fast).
    /// </summary>
    private HashSet<Guid> KidUsers(IReadOnlyList<CatalogItem> catalog, Guid viewer)
    {
        lock (_sharedLock)
        {
            if (_kids is { } k && ReferenceEquals(k.Catalog, catalog) && DateTime.UtcNow - k.At < JellyfinCatalog.VisibleTtl && k.Checked.Contains(viewer))
            {
                return k.Kids;
            }
        }

        var users = _store.Read(d => d.Signals.Select(s => s.UserId).Append(viewer).Distinct().ToList());
        var kids = new HashSet<Guid>();
        foreach (var u in users)
        {
            try
            {
                if (KidDetector.IsRestrictive(catalog, _catalog.VisibleTo(u)))
                {
                    kids.Add(u);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: could not classify a user's parental cap");
            }
        }

        lock (_sharedLock)
        {
            _kids = (catalog, DateTime.UtcNow, users.ToHashSet(), kids);
        }

        return kids;
    }

    /// <summary>The AI embeddings that are current for this library and model (hash and model match), or null when Ollama is off or none exist.</summary>
    private EmbeddingIndex? EmbeddingsFor(IReadOnlyList<CatalogItem> catalog, PluginConfiguration? cfg)
    {
        if (cfg?.OllamaEnabled != true)
        {
            return null;
        }

        try
        {
            var model = cfg.OllamaEmbedModel ?? string.Empty;
            var count = _store.ReadEmbeddings(e => e.Count);
            if (count == 0)
            {
                return null;
            }

            lock (_sharedLock)
            {
                if (_emb is { } c && ReferenceEquals(c.Catalog, catalog) && c.Model == model && c.Count == count && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10))
                {
                    return c.Index;
                }
            }

            var byKey = CatalogIndex.For(catalog).ById;
            var vectors = _store.ReadEmbeddings(e =>
            {
                var list = new List<KeyValuePair<Guid, float[]>>(e.Count);
                foreach (var (key, entry) in e)
                {
                    if (Guid.TryParseExact(key, "N", out var id) && byKey.TryGetValue(id, out var item)
                        && string.Equals(entry.Model, model, StringComparison.OrdinalIgnoreCase)
                        && entry.Hash == EmbeddingIndexer.ExpectedHash(item, model))
                    {
                        list.Add(new KeyValuePair<Guid, float[]>(id, entry.Vector));
                    }
                }

                return list;
            });
            var index = EmbeddingIndex.Create(vectors);
            lock (_sharedLock)
            {
                _emb = (catalog, model, count, DateTime.UtcNow, index);
            }

            return index;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: embeddings unavailable; using feature similarity only");
            return null;
        }
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
