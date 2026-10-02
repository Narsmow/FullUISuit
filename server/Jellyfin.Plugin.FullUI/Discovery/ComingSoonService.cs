using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>Builds per-user Coming Soon lists from TMDB and fills trailer keys. No-op without a TMDB key.</summary>
public sealed class ComingSoonService
{
    private const int MaxLibraryTrailersPerRun = 200;

    /// <summary>After this many failed trailer lookups in a row the run stops asking (TMDB is down); it tries again next run.</summary>
    private const int MaxConsecutiveTrailerFailures = 5;

    /// <summary>How long "TMDB has no trailer for this title" is remembered (this process only). Failures are never remembered.</summary>
    internal static readonly TimeSpan NoTrailerTtl = TimeSpan.FromDays(7);

    // Titles for which TMDB answered "no trailer"; remembered so they do not hog every run's quota, but they expire.
    private static readonly ConcurrentDictionary<string, DateTime> NoTrailer = new();

    private int _trailerFailures;

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly ITmdbClient _tmdb;
    private readonly IUserDirectory _users;
    private readonly ILogger<ComingSoonService> _log;

    public ComingSoonService(PluginStore store, ICatalog catalog, ITmdbClient tmdb, IUserDirectory users, ILogger<ComingSoonService> log)
    {
        _store = store;
        _catalog = catalog;
        _tmdb = tmdb;
        _users = users;
        _log = log;
    }

    public async Task RunAsync(IProgress<double>? progress, CancellationToken ct, DateTime? nowOverride = null)
    {
        if (!_tmdb.Configured)
        {
            _log.LogInformation("FullUI: TMDB key not set, skipping Coming Soon");
            progress?.Report(100);
            return;
        }

        var now = nowOverride ?? DateTime.UtcNow;
        _trailerFailures = 0;
        var items = _catalog.All;
        var libraryKeys = items.Where(i => i.TmdbId is > 0).Select(i => ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value)).ToHashSet();

        var genreMap = new Dictionary<string, IReadOnlyList<TmdbGenre>>();
        var upcoming = new List<TmdbTitle>();
        try
        {
            genreMap["movie"] = await _tmdb.GenresAsync("movie", ct).ConfigureAwait(false);
            genreMap["tv"] = await _tmdb.GenresAsync("tv", ct).ConfigureAwait(false);
            upcoming.AddRange(await _tmdb.UpcomingAsync("movie", ct).ConfigureAwait(false));
            upcoming.AddRange(await _tmdb.UpcomingAsync("tv", ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("FullUI: TMDB upcoming lists unavailable ({Type}); using recommendations only", ex.GetType().Name);
        }

        var users = _users.UserIds;
        for (var ui = 0; ui < users.Count; ui++)
        {
            ct.ThrowIfCancellationRequested();
            var userId = users[ui];
            try
            {
                var entries = await BuildForUserAsync(userId, items, libraryKeys, genreMap, upcoming, now, ct).ConfigureAwait(false);
                // An empty result usually means TMDB was unreachable: keep the previous list instead of wiping it.
                _store.Write(d =>
                {
                    var k = userId.ToString("N");
                    if (entries.Count > 0 || !d.ComingSoon.ContainsKey(k))
                    {
                        d.ComingSoon[k] = entries.ToList();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: coming soon failed for one user");
            }

            progress?.Report(60.0 * (ui + 1) / Math.Max(1, users.Count));
        }

        try
        {
            await FillTrailersAsync(items, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("FullUI: trailer lookup stopped early ({Type})", ex.GetType().Name);
        }

        progress?.Report(100);
    }

    private async Task<IReadOnlyList<ComingSoonEntry>> BuildForUserAsync(
        Guid userId,
        IReadOnlyList<CatalogItem> items,
        ISet<string> libraryKeys,
        Dictionary<string, IReadOnlyList<TmdbGenre>> genreMap,
        List<TmdbTitle> upcoming,
        DateTime now,
        CancellationToken ct)
    {
        // Copy only this user's rows while holding the store lock; the (heavier) seed scoring runs on the copy, outside it.
        var (mine, downvoted) = _store.Read(d =>
        {
            var prefix = userId.ToString("N") + "|";
            var snap = new StoreData();
            snap.Signals.AddRange(d.Signals.Where(s => s.UserId == userId));
            foreach (var kv in d.Ratings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)))
            {
                snap.Ratings[kv.Key] = kv.Value;
            }

            snap.MyList.UnionWith(d.MyList.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)));
            var down = d.Votes.Where(v => v.UserId == userId && v.Vote < 0).Select(v => ComingSoonRanker.Key(v.MediaType, v.TmdbId)).ToHashSet();
            return (snap, down);
        });
        var seeds = ComingSoonRanker.SelectSeeds(mine, userId, items, now);

        var candidates = new Dictionary<string, ComingSoonRanker.Candidate>();
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var seen = new HashSet<string>();
            IReadOnlyList<TmdbTitle> recs;
            IReadOnlyList<TmdbTitle> similar;
            try
            {
                recs = await _tmdb.RecommendationsAsync(seed.MediaType, seed.Item.TmdbId!.Value, ct).ConfigureAwait(false);
                similar = await _tmdb.SimilarAsync(seed.MediaType, seed.Item.TmdbId!.Value, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogDebug("FullUI: skipping one seed ({Type})", ex.GetType().Name);
                continue;
            }

            foreach (var t in recs.Concat(similar))
            {
                var key = ComingSoonRanker.Key(t.MediaType, t.Id);
                if (!seen.Add(key))
                {
                    continue; // one seed counts once per candidate
                }

                if (!candidates.TryGetValue(key, out var c))
                {
                    candidates[key] = c = new ComingSoonRanker.Candidate(t);
                }

                c.SeedHits++;
            }
        }

        var topGenres = ComingSoonRanker.TopGenres(seeds);
        var topIds = new HashSet<int>();
        foreach (var g in genreMap.Values)
        {
            topIds.UnionWith(ComingSoonRanker.MapGenreIds(topGenres, g));
        }

        foreach (var t in upcoming)
        {
            // Upcoming is filtered by the user's top genres (when we know any).
            if (topIds.Count > 0 && !t.GenreIds.Any(topIds.Contains))
            {
                continue;
            }

            var key = ComingSoonRanker.Key(t.MediaType, t.Id);
            if (!candidates.TryGetValue(key, out var c))
            {
                candidates[key] = c = new ComingSoonRanker.Candidate(t);
            }

            c.Upcoming = true;
        }

        var ranked = ComingSoonRanker.Rank(candidates.Values, libraryKeys, downvoted, topIds, now);
        var known = _store.Read(d => d.TrailerKeys.ToDictionary(kv => kv.Key, kv => kv.Value));
        foreach (var e in ranked)
        {
            var tk = ComingSoonRanker.Key(e.MediaType, e.TmdbId);
            if (known.TryGetValue(tk, out var existing))
            {
                e.TrailerKey = existing;
                continue;
            }

            if (IsKnownNoTrailer(tk) || TrailerLookupsPaused)
            {
                continue;
            }

            var key = await SafeTrailerAsync(e.MediaType, e.TmdbId, tk, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key))
            {
                e.TrailerKey = key;
                known[tk] = key;
                _store.Write(d => d.TrailerKeys[tk] = key);
            }
        }

        return ranked;
    }

    private bool TrailerLookupsPaused => _trailerFailures >= MaxConsecutiveTrailerFailures;

    private static bool IsKnownNoTrailer(string key)
        => NoTrailer.TryGetValue(key, out var at) && DateTime.UtcNow - at < NoTrailerTtl;

    /// <summary>
    /// Looks a trailer up. A genuine "TMDB has none" is remembered (for a week); a failed lookup is not, and several in a
    /// row pause trailer lookups for the rest of this run.
    /// </summary>
    private async Task<string?> SafeTrailerAsync(string mediaType, int id, string rememberKey, CancellationToken ct)
    {
        try
        {
            var r = await _tmdb.LookupTrailerAsync(mediaType, id, ct).ConfigureAwait(false);
            if (r.Failed)
            {
                _trailerFailures++;
                return null;
            }

            _trailerFailures = 0;
            if (string.IsNullOrEmpty(r.Key))
            {
                NoTrailer[rememberKey] = DateTime.UtcNow;
            }

            return r.Key;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _trailerFailures++;
            _log.LogDebug("FullUI: trailer lookup failed ({Type})", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Trailer keys for library items that have no Jellyfin RemoteTrailer ("item:{id:N}").</summary>
    private async Task FillTrailersAsync(IReadOnlyList<CatalogItem> items, CancellationToken ct)
    {
        var have = _store.Read(d => d.TrailerKeys.Keys.ToHashSet());
        var todo = items
            .Where(i => i.TmdbId is > 0 && string.IsNullOrEmpty(i.TrailerKey) && !have.Contains("item:" + i.Id.ToString("N")) && !IsKnownNoTrailer("item:" + i.Id.ToString("N")))
            .Take(MaxLibraryTrailersPerRun)
            .ToList();
        foreach (var i in todo)
        {
            ct.ThrowIfCancellationRequested();
            if (TrailerLookupsPaused)
            {
                _log.LogWarning("FullUI: TMDB is not answering; the rest of the trailer lookups wait for the next run");
                break;
            }

            var skey = "item:" + i.Id.ToString("N");
            var key = await SafeTrailerAsync(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value, skey, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(key))
            {
                _store.Write(d => d.TrailerKeys[skey] = key);
            }
        }
    }
}
