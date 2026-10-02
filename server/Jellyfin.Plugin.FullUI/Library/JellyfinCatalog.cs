using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Library;

/// <summary>
/// <see cref="ICatalog"/> backed by Jellyfin's library. The title snapshot is rebuilt lazily after
/// <see cref="Invalidate"/>; per-user visibility comes from a per-user library query so that library
/// access and parental limits are enforced by Jellyfin itself.
/// </summary>
public sealed class JellyfinCatalog : ICatalog, IDisposable
{
    /// <summary>
    /// How long a user's list of visible titles is trusted. Permission or parental-rating changes in Jellyfin take at most
    /// this long to show up (kept short on purpose: this list is the privacy filter for every row, search and rating).
    /// </summary>
    public static readonly TimeSpan VisibleTtl = TimeSpan.FromMinutes(2);

    /// <summary>A library scan raises ItemAdded for every file; the snapshot is rebuilt at most this often.</summary>
    public static readonly TimeSpan MinReload = TimeSpan.FromSeconds(30);

    /// <summary>Quiet period before <see cref="Changed"/> is raised after a burst of invalidations.</summary>
    public static readonly TimeSpan ChangedDebounce = TimeSpan.FromSeconds(10);

    private static readonly BaseItemKind[] Kinds = { BaseItemKind.Movie, BaseItemKind.Series };

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<JellyfinCatalog> _log;
    private readonly object _loadLock = new();
    private readonly ConcurrentDictionary<Guid, (DateTime At, IReadOnlySet<Guid> Ids)> _visible = new();
    private readonly Timer _changedTimer;
    private volatile IReadOnlyList<CatalogItem> _items = Array.Empty<CatalogItem>();
    private volatile bool _stale = true;
    private volatile bool _force;
    private volatile bool _everLoaded;
    private volatile bool _morePending;     // the last load ran out of time for people lookups; continue on the next refresh
    private long _loadedAtTicks;
    private int _generation;
    private int _visibleGeneration;
    private int _changedPending;

    public JellyfinCatalog(ILibraryManager library, IUserManager users, ILogger<JellyfinCatalog> log)
    {
        _library = library;
        _users = users;
        _log = log;
        _changedTimer = new Timer(_ => RaiseChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<CatalogItem> All
    {
        get
        {
            // Lock-free fast path: nothing changed since the last load.
            if (!_stale)
            {
                return _items;
            }

            // Changed, but a scan may be adding thousands of items: keep serving the snapshot we have.
            if (_everLoaded && !_force && DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) < MinReload.Ticks)
            {
                return _items;
            }

            // Only one thread loads; the library query runs outside any lock other callers need.
            if (_everLoaded)
            {
                if (!Monitor.TryEnter(_loadLock))
                {
                    return _items;
                }
            }
            else
            {
                Monitor.Enter(_loadLock);
            }

            try
            {
                if (_stale && (!_everLoaded || _force || DateTime.UtcNow.Ticks - Interlocked.Read(ref _loadedAtTicks) >= MinReload.Ticks))
                {
                    var gen = Volatile.Read(ref _generation);
                    _force = false;
                    try
                    {
                        _items = Load();
                        _everLoaded = true;
                        _stale = gen != Volatile.Read(ref _generation) || _morePending;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "FullUI: catalog refresh failed; keeping previous snapshot");
                    }

                    Interlocked.Exchange(ref _loadedAtTicks, DateTime.UtcNow.Ticks);
                }

                return _items;
            }
            finally
            {
                Monitor.Exit(_loadLock);
            }
        }
    }

    public IReadOnlySet<Guid> VisibleTo(Guid userId)
    {
        if (_visible.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < VisibleTtl)
        {
            return hit.Ids;
        }

        try
        {
            var gen = Volatile.Read(ref _visibleGeneration);
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return new HashSet<Guid>();
            }

            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = Kinds,
                Recursive = true,
            };
            var ids = _library.GetItemList(query).Select(i => i.Id).ToHashSet();
            if (gen == Volatile.Read(ref _visibleGeneration))
            {
                _visible[userId] = (DateTime.UtcNow, ids);
            }

            return ids;
        }
        catch (Exception ex)
        {
            // Fail closed: if access cannot be determined, show nothing rather than leak restricted titles.
            _log.LogError(ex, "FullUI: could not determine visible titles for {User}", userId);
            return new HashSet<Guid>();
        }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _stale = true;

        // Tell consumers once per burst, shortly after the burst ends, instead of once per added file.
        if (Interlocked.Exchange(ref _changedPending, 1) == 0)
        {
            try
            {
                _changedTimer.Change(ChangedDebounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void InvalidateNow()
    {
        Interlocked.Increment(ref _generation);
        _force = true;
        _stale = true;
        RaiseChanged();
    }

    public void Dispose() => _changedTimer.Dispose();

    public static string? ParseYouTubeKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        string? key = null;
        if (IsHost(host, "youtu.be"))
        {
            key = uri.AbsolutePath.Trim('/').Split('/')[0];
        }
        else if (IsHost(host, "youtube.com") || IsHost(host, "youtube-nocookie.com"))
        {
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2);
            key = query?[1] ?? (uri.AbsolutePath.StartsWith("/embed/", StringComparison.Ordinal) ? uri.AbsolutePath[7..].Split('/')[0] : null);
        }

        return !string.IsNullOrEmpty(key) && key.Length >= 6 && key.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_') ? key : null;
    }

    /// <summary>A short stable token that changes when the item's primary image file or its modification time changes.</summary>
    internal static string? ImageTagOf(BaseItem item)
    {
        try
        {
            var info = item.GetImageInfo(ImageType.Primary, 0);
            return info is null ? null : ImageTag(info.Path, info.DateModified);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string? ImageTag(string? path, DateTime modified)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path + "|" + modified.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>The host is the domain itself or a subdomain of it ("evilyoutu.be" is neither).</summary>
    private static bool IsHost(string host, string domain)
        => host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private void RaiseChanged()
    {
        Interlocked.Exchange(ref _changedPending, 0);
        Interlocked.Increment(ref _visibleGeneration);
        _visible.Clear();
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: a library-changed listener failed");
        }
    }

    /// <summary>One real or special episode as the aggregator needs it.</summary>
    internal readonly record struct EpisodeRow(Guid SeriesId, int? Season, DateTime Created);

    /// <summary>Per-series result of <see cref="AggregateEpisodes"/>.</summary>
    internal sealed record SeriesAggregate(IReadOnlyList<SeasonInfo> Seasons, IReadOnlyList<DateTime> Recent, DateTime? Latest);

    /// <summary>
    /// Folds all episodes into per-series seasons (episode count, first/last add date), the newest add dates and the latest add date.
    /// Season 0 (specials) is ignored everywhere; episodes without a season number count as season 1.
    /// </summary>
    internal static Dictionary<Guid, SeriesAggregate> AggregateEpisodes(IEnumerable<EpisodeRow> episodes)
    {
        var seasons = new Dictionary<Guid, Dictionary<int, (int Count, DateTime First, DateTime Last)>>();
        var recent = new Dictionary<Guid, List<DateTime>>();
        foreach (var e in episodes)
        {
            if (e.SeriesId == Guid.Empty || e.Season == 0)
            {
                continue;
            }

            var n = e.Season is int sn && sn > 0 ? sn : 1;
            if (!seasons.TryGetValue(e.SeriesId, out var map))
            {
                seasons[e.SeriesId] = map = new Dictionary<int, (int, DateTime, DateTime)>();
            }

            map[n] = map.TryGetValue(n, out var cur)
                ? (cur.Count + 1, e.Created < cur.First ? e.Created : cur.First, e.Created > cur.Last ? e.Created : cur.Last)
                : (1, e.Created, e.Created);

            if (!recent.TryGetValue(e.SeriesId, out var list))
            {
                recent[e.SeriesId] = list = new List<DateTime>();
            }

            list.Add(e.Created);
            if (list.Count > 64)
            {
                list.Sort((a, b) => b.CompareTo(a));
                list.RemoveRange(12, list.Count - 12);
            }
        }

        var result = new Dictionary<Guid, SeriesAggregate>(seasons.Count);
        foreach (var (id, map) in seasons)
        {
            var dates = recent[id];
            dates.Sort((a, b) => b.CompareTo(a));
            result[id] = new SeriesAggregate(
                map.OrderBy(kv => kv.Key).Select(kv => new SeasonInfo(kv.Key, kv.Value.Count, kv.Value.First, kv.Value.Last)).ToList(),
                dates.Take(12).ToList(),
                dates.Count > 0 ? dates[0] : null);
        }

        return result;
    }

    /// <summary>Do not read more than this many episodes (a safety cap for enormous libraries; counts beyond it are simply unknown).</summary>
    private const int MaxEpisodes = 400_000;

    /// <summary>How long one library load may spend on per-title people lookups; the rest continues on the next refresh.</summary>
    private static readonly TimeSpan PeopleBudget = TimeSpan.FromSeconds(4);

    private sealed record People(DateTime Saved, string[] Cast, string[] Directors);

    private readonly ConcurrentDictionary<Guid, People> _people = new();

    private IReadOnlyList<CatalogItem> Load()
    {
        // One cheap pass over the episodes (no sorting, minimal fields): season structure and the newest add dates per series.
        Dictionary<Guid, SeriesAggregate> aggregates;
        try
        {
            var episodes = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false, // "missing episode" placeholders must not count as newly added episodes
                Limit = MaxEpisodes,
                DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(false),
            });
            aggregates = AggregateEpisodes(episodes.OfType<Episode>().Select(ep => new EpisodeRow(ep.SeriesId, ep.ParentIndexNumber, ep.DateCreated)));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: could not read episodes; series progress and new-season detection are limited");
            aggregates = new Dictionary<Guid, SeriesAggregate>();
        }

        var collections = LoadCollections();
        var items = _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = Kinds, Recursive = true });
        var result = new List<CatalogItem>(items.Count);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var peopleSkipped = 0;
        foreach (var i in items)
        {
            try
            {
                var isSeries = i is Series;
                aggregates.TryGetValue(i.Id, out var agg);
                var (cast, directors) = ReadPeople(i, clock, ref peopleSkipped);
                collections.TryGetValue(i.Id, out var col);
                result.Add(new CatalogItem
                {
                    Id = i.Id,
                    Kind = isSeries ? CatalogKind.Series : CatalogKind.Movie,
                    Name = i.Name ?? string.Empty,
                    Year = i.ProductionYear,
                    Rating = i.CommunityRating,
                    OfficialRating = i.OfficialRating,
                    Overview = i.Overview,
                    RuntimeMinutes = i.RunTimeTicks is long t && t > 0 ? (int)(t / TimeSpan.TicksPerMinute) : null,
                    Genres = i.Genres ?? Array.Empty<string>(),
                    Studios = i.Studios ?? Array.Empty<string>(),
                    Tags = i.Tags ?? Array.Empty<string>(),
                    DateAdded = i.DateCreated,
                    LatestEpisodeAdded = isSeries ? agg?.Latest : null,
                    TmdbId = i.ProviderIds.TryGetValue("Tmdb", out var tmdb) && int.TryParse(tmdb, out var tid) ? tid : null,
                    TrailerKey = i.RemoteTrailers?.Select(r => ParseYouTubeKey(r.Url)).FirstOrDefault(k => k is not null),
                    HasBackdrop = i.HasImage(ImageType.Backdrop),
                    HasLogo = i.HasImage(ImageType.Logo),
                    PrimaryImageTag = ImageTagOf(i),
                    Cast = cast,
                    Directors = directors,
                    CollectionId = col.Id,
                    CollectionName = col.Name,
                    Seasons = isSeries ? agg?.Seasons ?? Array.Empty<SeasonInfo>() : Array.Empty<SeasonInfo>(),
                    RecentEpisodeDates = isSeries ? agg?.Recent ?? Array.Empty<DateTime>() : Array.Empty<DateTime>(),
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: skipping unreadable library item {Item}", i.Id);
            }
        }

        _morePending = peopleSkipped > 0;
        if (peopleSkipped > 0)
        {
            // Cast/director lookups hit the database once per title; continue with the rest on the next refresh.
            _log.LogInformation("FullUI: cast data for {Count} titles will be read on the next refresh", peopleSkipped);
        }

        _log.LogInformation("FullUI: catalog loaded with {Count} titles", result.Count);
        return result;
    }

    /// <summary>Movie/series id to its BoxSet (collection). Never throws: collections are a bonus.</summary>
    private Dictionary<Guid, (Guid? Id, string? Name)> LoadCollections()
    {
        var map = new Dictionary<Guid, (Guid? Id, string? Name)>();
        try
        {
            foreach (var box in _library.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.BoxSet }, Recursive = true }))
            {
                try
                {
                    foreach (var child in (box as Folder)?.GetLinkedChildren() ?? Enumerable.Empty<BaseItem>())
                    {
                        map.TryAdd(child.Id, (box.Id, box.Name));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "FullUI: could not read collection {Collection}", box.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: could not read collections");
        }

        return map;
    }

    /// <summary>Top-billed cast (6) and directors (2) from Jellyfin's people data; cached per item version, time-budgeted, never throws.</summary>
    private (string[] Cast, string[] Directors) ReadPeople(BaseItem item, System.Diagnostics.Stopwatch clock, ref int skipped)
    {
        if (_people.TryGetValue(item.Id, out var hit) && hit.Saved == item.DateLastSaved)
        {
            return (hit.Cast, hit.Directors);
        }

        if (clock.Elapsed > PeopleBudget)
        {
            skipped++;
            return hit is null ? (Array.Empty<string>(), Array.Empty<string>()) : (hit.Cast, hit.Directors);
        }

        try
        {
            var people = _library.GetPeople(item);
            var cast = people.Where(p => p.Type == PersonKind.Actor && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
            var directors = people.Where(p => p.Type == PersonKind.Director && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
            _people[item.Id] = new People(item.DateLastSaved, cast, directors);
            return (cast, directors);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FullUI: could not read people for {Item}", item.Id);
            return (Array.Empty<string>(), Array.Empty<string>());
        }
    }
}
