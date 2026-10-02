using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

public sealed partial class RecEngine
{
    private IReadOnlyList<RecRow> Compose()
    {
        var rows = new List<RecRow>();
        var used = new HashSet<Guid>();                 // global dedup across recommendation rows
        var cands = Candidates();
        var minRow = MinRowFor(cands.Count);
        var scored = cands.Select(c => (Item: c, Score: Score(c)))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _scoreSorted = scored.Select(x => x.Score).OrderBy(x => x).ToArray();

        var topMovies = TopTen(CatalogKind.Movie);
        var topShows = TopTen(CatalogKind.Series);
        _ranks = new Dictionary<Guid, int>();
        if (topMovies.Count >= MinChartItems)
        {
            foreach (var (id, i) in topMovies.Select((x, i) => (x.Id, i)))
            {
                _ranks[id] = i + 1;
            }
        }

        if (topShows.Count >= MinChartItems)
        {
            foreach (var (id, i) in topShows.Select((x, i) => (x.Id, i)))
            {
                _ranks[id] = i + 1;
            }
        }

        // Continue Watching
        var cont = ContinueItems();
        if (cont.Count >= 1)
        {
            rows.Add(new RecRow("continue", "Continue Watching", "continue", OrderContinue, cont));
        }

        // Next in a collection the user has started (rows of 1+ are useful: "the next movie of the saga").
        foreach (var (colId, name, items) in CollectionRows().Take(2))
        {
            used.UnionWith(items.Select(i => i.Id));
            rows.Add(new RecRow($"collection-{colId:N}", $"Next in {name}", "collection", OrderCollection, Wrap(items, reason: _ => $"Next in {name}")));
        }

        // Page generation. Top Picks first reserves only its strongest few titles; Because-you-watched and genre rows then draw
        // from their own best matches; Top Picks is topped up from what is left. Small libraries shrink the rows instead of losing them.
        var pickHead = new List<CatalogItem>();
        if (scored.Count >= minRow)
        {
            var head = Math.Min(8, Math.Max(minRow, scored.Count / 4));
            pickHead = scored.Where(x => !used.Contains(x.Item.Id)).Take(head).Select(x => x.Item).ToList();
            used.UnionWith(pickHead.Select(p => p.Id));
        }

        if (!_cold)
        {
            // Keep at least ~10 titles for Top Picks; the rest can fund secondary rows of minRow+ titles each.
            var picksKeep = Math.Min(RowSize / 2, scored.Count);
            var maxSecondary = Math.Max(0, (scored.Count - picksKeep) / minRow);
            var seeds = Seeds();
            var genres = TopGenres().Take(6).ToList();
            var seedRows = Math.Min(3, Math.Min(seeds.Count, maxSecondary));
            var genreRowsMax = Math.Min(3, Math.Min(genres.Count, Math.Max(0, maxSecondary - seedRows)));
            var planned = seedRows + genreRowsMax;
            var remaining = scored.Count(x => !used.Contains(x.Item.Id));
            var target = Math.Clamp((remaining - Math.Max(0, picksKeep - pickHead.Count)) / Math.Max(1, planned), minRow, RowSize);

            var seedCount = 0;
            foreach (var seed in seeds)
            {
                if (seedCount >= seedRows)
                {
                    break;
                }

                var sims = scored
                    .Where(x => !used.Contains(x.Item.Id) && x.Item.Id != seed.Id)
                    .Select(x => (x.Item, Sim: Similarity(seed, x.Item)))
                    .Where(x => x.Sim >= 0.1)
                    .OrderByDescending(x => x.Sim + (0.1 * Quality(x.Item)))
                    .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(target)
                    .Select(x => x.Item)
                    .ToList();
                if (sims.Count >= minRow)
                {
                    used.UnionWith(sims.Select(s => s.Id));
                    var reason = $"Because you watched {seed.Name}";
                    rows.Add(new RecRow($"because-{seed.Id:N}", reason, "because", OrderBecause, Wrap(sims, reason: _ => reason)));
                    seedCount++;
                }
            }

            var genreRows = 0;
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var genre in genres)
            {
                if (genreRows >= genreRowsMax)
                {
                    break;
                }

                var items = scored
                    .Where(x => !used.Contains(x.Item.Id) && x.Item.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    .Take(target).Select(x => x.Item).ToList();
                if (items.Count >= minRow)
                {
                    used.UnionWith(items.Select(i => i.Id));
                    var title = _in.RowTitles.TryGetValue(genre, out var t) && !string.IsNullOrWhiteSpace(t) ? t : $"{genre} Picks for You";
                    var g = genre;
                    rows.Add(new RecRow(UniqueGenreId(genre, usedIds), title, "genre", OrderGenre, Wrap(items, reason: c => c.Rating >= 7.5f ? $"Top rated in {g}" : $"Because you like {g}")));
                    genreRows++;
                }
            }
        }

        // Top Picks (or cold-start popularity), plus one exploration slot that rotates daily.
        var picks = pickHead.Concat(scored.Where(x => !used.Contains(x.Item.Id)).Take(Math.Max(0, RowSize - pickHead.Count)).Select(x => x.Item)).ToList();
        used.UnionWith(picks.Select(p => p.Id));
        CatalogItem? explore = null;
        if (!_cold && picks.Count >= Math.Max(minRow, 6) && ExplorationPick(scored, used) is CatalogItem ex)
        {
            explore = ex;
            used.Add(ex.Id);
            if (picks.Count >= RowSize)
            {
                picks.RemoveAt(picks.Count - 1);
            }

            picks.Insert(Math.Min(4, picks.Count), ex);
        }

        if (picks.Count >= minRow)
        {
            var title = _cold ? $"Popular on {_in.ServerName}" : "Top Picks for You";
            var exploreId = explore?.Id;
            rows.Add(new RecRow("toppicks", title, "toppicks", OrderTopPicks, Wrap(picks, reason: c => c.Id == exploreId ? "Something different today" : PickReason(c))));
        }

        if (topMovies.Count >= MinChartItems)
        {
            rows.Add(new RecRow("top10-movies", $"Top 10 Movies on {_in.ServerName} {TopTenPeriod(_in.TopTenWindowDays)}", "top10", OrderTop10Movies, Wrap(topMovies, ranked: true, reason: _ => ChartReason())));
        }

        // Trending (not deduplicated: it is a chart-like row)
        var trending = Trending();
        if (trending.Count >= minRow)
        {
            rows.Add(new RecRow("trending", "Trending Now", "trending", OrderTrending, Wrap(trending, reason: _ => CfEnabled ? "Popular in your household" : "Trending now")));
        }

        // My List
        var myList = _myList
            .Where(g => _idx.ById.ContainsKey(g))
            .Select(g => _idx.ById[g])
            .Where(c => Eligible(c))
            .OrderByDescending(c => c.DateAdded).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (myList.Count >= 1)
        {
            rows.Add(new RecRow("mylist", "My List", "mylist", OrderMyList, Wrap(myList)));
        }

        if (topShows.Count >= MinChartItems)
        {
            rows.Add(new RecRow("top10-shows", $"Top 10 Shows on {_in.ServerName} {TopTenPeriod(_in.TopTenWindowDays)}", "top10", OrderTop10Shows, Wrap(topShows, ranked: true, reason: _ => ChartReason())));
        }

        var arrivals = NewArrivals();
        if (arrivals.Count >= 1)
        {
            var kinds = arrivals.ToDictionary(a => a.Item.Id, a => a.Season);
            rows.Add(new RecRow("newseasons", "New Episodes", "newseasons", OrderNewSeasons,
                Wrap(arrivals.Select(a => a.Item), reason: c => kinds[c.Id] ? $"New season of {c.Name}" : $"New episodes of {c.Name}")));
        }

        var recent = _catalog.Where(c => Eligible(c))
            .OrderByDescending(c => c.LatestEpisodeAdded is DateTime l && l > c.DateAdded ? l : c.DateAdded)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RowSize).ToList();
        if (recent.Count >= minRow)
        {
            rows.Add(new RecRow("recent", "Recently Added", "recent", OrderRecent, Wrap(recent, reason: _ => "Recently added")));
        }

        var hidden = HiddenGems(scored, used);
        if (hidden.Count >= minRow)
        {
            used.UnionWith(hidden.Select(i => i.Id));
            rows.Add(new RecRow("hidden", "Hidden Gems", "hidden", OrderHidden, Wrap(hidden, reason: c => $"Rated {c.Rating:0.0}, and few people here have watched it")));
        }

        var again = WatchAgain();
        if (again.Count >= MinChartItems)
        {
            rows.Add(new RecRow("again", "Watch Again", "again", OrderAgain, Wrap(again, reason: _ => "You enjoyed this before")));
        }

        return rows.OrderBy(r => r.Order).ToList();
    }

    private string? ChartReason() => CfEnabled ? "Popular in your household" : null;

    /// <summary>Strongest profile genres first (IDF weighting keeps ubiquitous genres from always winning).</summary>
    private IEnumerable<string> TopGenres() => _profile.Values
        .Where(kv => kv.Key.StartsWith("g:", StringComparison.Ordinal) && kv.Value > 0)
        .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => kv.Key[2..]);

    private List<RankedItem> Wrap(IEnumerable<CatalogItem> items, bool ranked = false, Func<CatalogItem, string?>? reason = null)
    {
        var list = new List<RankedItem>();
        var i = 0;
        foreach (var c in items)
        {
            i++;
            list.Add(new RankedItem(c, BadgesFor(c), ranked ? i : null, null, MatchFor(c), reason?.Invoke(c)));
        }

        return list;
    }

    private string[] BadgesFor(CatalogItem c) =>
        Badges(c, _in.Now, _ranks.TryGetValue(c.Id, out var rank) ? rank : null);

    /// <summary>
    /// 1..99: the percentile of the ranker score among the titles the user could still watch. Null on cold start
    /// (no taste yet) or when there are too few titles for a percentile to mean anything.
    /// </summary>
    private int? MatchFor(CatalogItem c)
    {
        if (_cold || _scoreSorted.Length < 5)
        {
            return null;
        }

        var s = Score(c);
        var lo = 0;
        var hi = _scoreSorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_scoreSorted[mid] <= s)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return Math.Clamp(1 + (int)Math.Round(98.0 * lo / _scoreSorted.Length), 1, 99);
    }

    /// <summary>"Because you watched X" for the seed that is most similar to the title, else a popularity hint.</summary>
    private string? PickReason(CatalogItem c)
    {
        if (!_cold)
        {
            CatalogItem? best = null;
            var bestSim = 0.2;
            foreach (var seed in Seeds())
            {
                if (seed.Id == c.Id)
                {
                    continue;
                }

                var sim = Similarity(seed, c);
                if (sim > bestSim)
                {
                    bestSim = sim;
                    best = seed;
                }
            }

            if (best is not null)
            {
                return $"Because you watched {best.Name}";
            }

            if (CfEnabled && _cf.GetValueOrDefault(c.Id) >= 0.3)
            {
                return "Popular in your household";
            }

            if (c.Rating >= 7.5f && c.Genres.Count > 0)
            {
                return $"Top rated in {c.Genres[0]}";
            }

            return null;
        }

        return _pop.GetValueOrDefault(c.Id) > 0 && CfEnabled ? "Popular in your household" : null;
    }

    private CatalogItem? ExplorationPick(List<(CatalogItem Item, double Score)> scored, HashSet<Guid> used)
    {
        var top = TopGenres().Take(3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pool = scored
            .Where(x => !used.Contains(x.Item.Id) && x.Item.Rating >= 6.5f && x.Item.Genres.Count > 0 && !x.Item.Genres.Any(top.Contains))
            .Take(200).Select(x => x.Item).ToList();
        if (pool.Count == 0)
        {
            pool = scored.Where(x => !used.Contains(x.Item.Id) && x.Item.Rating >= 6.5f).Take(100).Select(x => x.Item).ToList();
        }

        if (pool.Count == 0)
        {
            return null;
        }

        var day = (int)(_in.Now.Date - new DateTime(2020, 1, 1)).TotalDays;
        return pool[(int)(DailyHash(_in.UserId, day) % (uint)pool.Count)];
    }

    /// <summary>Stable across processes (unlike string.GetHashCode): the same user gets the same slot all day, a different one tomorrow.</summary>
    public static uint DailyHash(Guid user, int day)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var b in user.ToByteArray())
            {
                h = (h ^ b) * 16777619u;
            }

            for (var i = 0; i < 4; i++)
            {
                h = (h ^ (uint)((day >> (8 * i)) & 0xFF)) * 16777619u;
            }

            return h;
        }
    }

    private List<RankedItem> ContinueItems()
    {
        var result = new List<RankedItem>();
        var seen = new HashSet<Guid>();
        foreach (var latest in _mySignals.Where(s => s.Season != 0).GroupBy(s => s.ItemId)
                     .Select(g => g.OrderByDescending(s => s.At).First()).OrderByDescending(s => s.At))
        {
            if (ResumeProgress(latest, _in.Now) is not double progress)
            {
                continue;
            }

            // A resumable movie may be a rewatch of a finished one: that is still worth continuing.
            if (_idx.ById.TryGetValue(latest.ItemId, out var item) && Eligible(item, allowFinished: true) && seen.Add(item.Id))
            {
                var label = item.Kind == CatalogKind.Series ? SeriesLabel(latest.Season, latest.Episode) : null;
                result.Add(new RankedItem(item, BadgesFor(item), null, progress, MatchFor(item), "Pick up where you left off", label, MinutesLeft(item, progress)));
            }
        }

        // A series whose last episode was finished has nothing "in progress" but still has a next episode to play.
        foreach (var id in _nextUp)
        {
            if (_idx.ById.TryGetValue(id, out var series) && series.Kind == CatalogKind.Series && Eligible(series) && seen.Add(id))
            {
                var next = _in.NextUpEpisodes.GetValueOrDefault(id);
                result.Add(new RankedItem(series, BadgesFor(series), null, null, MatchFor(series), "Your next episode is ready", SeriesLabel(next?.Season, next?.Episode), null));
            }
        }

        return result;
    }

    /// <summary>"S2:E5", or null when the season or episode is unknown.</summary>
    public static string? SeriesLabel(int? season, int? episode) =>
        season is int s && episode is int e && s > 0 && e > 0 ? $"S{s}:E{e}" : null;

    /// <summary>Minutes still to watch for a resumable title (at least 1), or null when the runtime is unknown.</summary>
    public static int? MinutesLeft(CatalogItem item, double progress) =>
        item.RuntimeMinutes is int m && m > 0 ? Math.Max(1, (int)Math.Round(m * (1 - progress))) : null;

    /// <summary>
    /// Titles that can start a "Because you watched" row: finished movies, series the user is caught up on, and loved titles.
    /// A single finished episode of a long series is NOT a seed. Dropped shows never are. Newest first.
    /// </summary>
    private List<CatalogItem> Seeds()
    {
        if (_seedsCache is not null)
        {
            return _seedsCache;
        }

        var last = new Dictionary<Guid, DateTime>();
        foreach (var grp in _mySignals.Where(s => s.Season != 0).GroupBy(s => s.ItemId))
        {
            if (!_idx.ById.TryGetValue(grp.Key, out var item))
            {
                continue;
            }

            if (item.Kind == CatalogKind.Movie)
            {
                var done = grp.Where(s => s.Completed).Select(s => s.At).ToList();
                if (done.Count > 0)
                {
                    last[grp.Key] = done.Max();
                }
            }
            else if (_series.TryGetValue(grp.Key, out var st) && st.CaughtUp && !st.Dropped)
            {
                last[grp.Key] = st.Last;
            }
        }

        foreach (var (id, rating) in _myRatings)
        {
            if (rating == 2 && !last.ContainsKey(id))
            {
                last[id] = _in.Now.AddDays(-1);
            }
        }

        return _seedsCache = last.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
            .Where(kv => _idx.ById.TryGetValue(kv.Key, out var c) && IsVisible(c) && MyRating(kv.Key) >= 0 && !_hidden.Contains(kv.Key))
            .Select(kv => _idx.ById[kv.Key])
            .Take(8).ToList();
    }

    /// <summary>Top 10 for one kind: distinct users first, then completed plays; windowed; shared-excluded users removed; eligible for this viewer.</summary>
    private List<CatalogItem> TopTen(CatalogKind kind)
    {
        var since = _in.Now.AddDays(-Math.Max(1, _in.TopTenWindowDays));
        return _signals
            .Where(s => s.Completed && s.Season != 0 && s.At >= since && Shared(s.UserId))
            .GroupBy(s => s.ItemId)
            .Where(g => _idx.ById.TryGetValue(g.Key, out var c) && c.Kind == kind && Eligible(c))
            .Select(g => (Item: _idx.ById[g.Key], Users: g.Select(s => s.UserId).Distinct().Count(), Plays: g.Count()))
            .OrderByDescending(x => x.Users).ThenByDescending(x => x.Plays)
            .ThenByDescending(x => x.Item.Rating ?? 0).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(x => x.Item).ToList();
    }

    /// <summary>Momentum: recent engagement with a 3-day half-life, one vote per user and title.</summary>
    private List<CatalogItem> Trending()
    {
        var since = _in.Now.AddDays(-14);
        return _signals
            .Where(s => s.At >= since && s.Season != 0 && (s.Completed || s.Completion >= 0.25) && Shared(s.UserId))
            .GroupBy(s => (s.ItemId, s.UserId))
            .Select(g => (g.Key.ItemId, Weight: Math.Pow(0.5, Math.Max(0, (_in.Now - g.Max(s => s.At)).TotalDays) / 3.0)))
            .GroupBy(x => x.ItemId)
            .Select(g => (Id: g.Key, Score: g.Sum(x => x.Weight)))
            .Where(x => _idx.ById.TryGetValue(x.Id, out var c) && Eligible(c))
            .OrderByDescending(x => x.Score).ThenBy(x => _idx.ById[x.Id].Name, StringComparer.OrdinalIgnoreCase)
            .Take(RowSize).Select(x => _idx.ById[x.Id]).ToList();
    }

    /// <summary>
    /// Series the user follows (and has not dropped) where something new actually arrived: a season after the highest one they
    /// watched that was added recently (and they finished the earlier ones), or new episodes while they were caught up.
    /// Libraries without episode data fall back to "an episode was added in the last 14 days".
    /// </summary>
    private List<(CatalogItem Item, bool Season, DateTime When)> NewArrivals()
    {
        var found = new List<(CatalogItem, bool, DateTime)>();
        foreach (var st in _series.Values)
        {
            if (!st.Started || !_idx.ById.TryGetValue(st.SeriesId, out var c) || c.Kind != CatalogKind.Series || !Eligible(c))
            {
                continue;
            }

            if (c.Seasons.Count == 0)
            {
                if (c.LatestEpisodeAdded is DateTime l && (_in.Now - l).TotalDays <= 14 && (_in.Now - c.DateAdded).TotalDays > 30)
                {
                    found.Add((c, false, l));
                }

                continue;
            }

            var newSeason = c.Seasons
                .Where(s => s.Number > st.HighestSeason && st.HighestSeason > 0 && s.Episodes > 0 && (_in.Now - s.FirstAdded).TotalDays <= 45)
                .OrderBy(s => s.Number).FirstOrDefault();
            if (newSeason is not null)
            {
                var earlierTotal = c.Seasons.Where(s => s.Number <= st.HighestSeason).Sum(s => s.Episodes);
                var earlierWatched = st.BySeason.Where(kv => kv.Key > 0 && kv.Key <= st.HighestSeason).Sum(kv => kv.Value);
                if (earlierTotal == 0 || earlierWatched >= 0.8 * earlierTotal)
                {
                    found.Add((c, true, newSeason.FirstAdded));
                    continue;
                }
            }

            var recent = c.RecentEpisodeDates.Count(d => (_in.Now - d).TotalDays <= 14);
            var unwatched = st.Total - st.Watched;
            if (recent > 0 && st.Watched >= 1 && unwatched >= 1 && unwatched <= recent)
            {
                found.Add((c, false, c.RecentEpisodeDates.Max()));
            }
        }

        return found.OrderByDescending(f => f.Item3).ThenBy(f => f.Item1.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (f.Item1, f.Item2, f.Item3)).ToList();
    }

    /// <summary>Fully finished titles only (a movie watched, or every episode of a series), a month or more ago.</summary>
    private List<CatalogItem> WatchAgain()
    {
        var result = new List<CatalogItem>();
        foreach (var grp in _mySignals.Where(s => s.Season != 0).GroupBy(s => s.ItemId))
        {
            if (!_idx.ById.TryGetValue(grp.Key, out var c) || !Eligible(c, allowFinished: true) || !IsFinished(c))
            {
                continue;
            }

            var lastActivity = c.Kind == CatalogKind.Movie
                ? grp.Where(s => s.Completed).Max(s => s.At)
                : _series[c.Id].Last;
            if ((_in.Now - lastActivity).TotalDays >= 30)
            {
                result.Add(c);
            }
        }

        return result.OrderByDescending(c => _weights.GetValueOrDefault(c.Id)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RowSize).ToList();
    }

    /// <summary>
    /// Well rated titles few people here have watched: not yet watched by this user, rated above the library median (and at least 7),
    /// watched by at most a quarter of the active household (never more than one person in a one- or two-user household).
    /// </summary>
    private List<CatalogItem> HiddenGems(List<(CatalogItem Item, double Score)> scored, HashSet<Guid> used)
    {
        var rated = _catalog.Where(c => IsVisible(c) && c.Rating is not null).Select(c => c.Rating!.Value).OrderBy(r => r).ToList();
        if (rated.Count == 0)
        {
            return new List<CatalogItem>();
        }

        var median = rated[rated.Count / 2];
        var activeUsers = _byUser.Count(g => Shared(g.Key));
        var maxWatchers = Math.Max(1, activeUsers / 4);
        return scored
            .Where(x => !used.Contains(x.Item.Id) && x.Item.Rating is float r && r > median && r >= 7.0f
                        && _usersPerItem.GetValueOrDefault(x.Item.Id) <= maxWatchers)
            .OrderByDescending(x => x.Item.Rating).ThenByDescending(x => x.Score)
            .Take(RowSize).Select(x => x.Item).ToList();
    }

    /// <summary>Collections (BoxSets) where the user finished at least one movie and more unwatched ones remain, oldest member first.</summary>
    private IEnumerable<(Guid Id, string Name, List<CatalogItem> Items)> CollectionRows()
    {
        var found = new List<(Guid Id, string Name, List<CatalogItem> Items, DateTime Last)>();
        foreach (var (colId, members) in _idx.Collections)
        {
            var primaries = members.Where(m => !_alias.ContainsKey(m.Id)).ToList();
            var doneAt = primaries.Where(m => _finishedMovies.Contains(m.Id))
                .Select(m => _mySignals.Where(s => s.ItemId == m.Id && s.Completed).Max(s => s.At)).ToList();
            if (doneAt.Count == 0)
            {
                continue;
            }

            var next = primaries.Where(m => Eligible(m) && !_touched.Contains(m.Id)).Take(RowSize).ToList();
            if (next.Count > 0)
            {
                found.Add((colId, primaries.Select(m => m.CollectionName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "this collection", next, doneAt.Max()));
            }
        }

        return found.OrderByDescending(f => f.Last).ThenBy(f => f.Id).Select(f => (f.Id, f.Name, f.Items));
    }
}
