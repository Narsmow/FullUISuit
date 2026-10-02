using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>
/// Pure recommendation engine: content similarity (cosine over sparse features), a decayed per-user
/// profile, optional item-item collaborative filtering and the row builders. No Jellyfin types.
/// </summary>
public sealed class RecEngine
{
    // Row positions, exactly the order in docs/api-contract.md. "comingsoon" (5) is inserted by HomeService.
    public const int OrderContinue = 0;
    public const int OrderTopPicks = 1;
    public const int OrderTop10Movies = 2;
    public const int OrderBecause = 3;
    public const int OrderTrending = 4;
    public const int OrderComingSoon = 5;
    public const int OrderMyList = 6;
    public const int OrderGenre = 7;
    public const int OrderTop10Shows = 8;
    public const int OrderNewSeasons = 9;
    public const int OrderRecent = 10;
    public const int OrderHidden = 11;
    public const int OrderAgain = 12;

    public const double HalfLifeDays = 90;
    private const int RowSize = 20;
    private const int MinRowItems = 5;
    private const int MinChartItems = 3;

    private readonly RecInput _in;
    private readonly Dictionary<Guid, CatalogItem> _byId;
    private readonly Dictionary<Guid, SparseVector> _vecs = new();
    private readonly List<PlaySignal> _mySignals;
    private readonly Dictionary<Guid, double> _weights;
    private readonly HashSet<Guid> _touched;     // titles the user has any signal for
    private readonly SparseVector _profile = new();
    private readonly bool _cold;
    private readonly Dictionary<Guid, double> _pop = new();
    private readonly Dictionary<Guid, int> _usersPerItem = new();
    private readonly Dictionary<Guid, double> _cf = new();
    private Dictionary<Guid, int> _ranks = new();

    private RecEngine(RecInput input)
    {
        _in = input;
        _byId = input.Catalog.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _mySignals = input.Signals.Where(s => s.UserId == input.UserId).ToList();
        _touched = _mySignals.Select(s => s.ItemId).ToHashSet();
        _weights = ItemWeights(input, input.UserId);

        foreach (var (id, w) in _weights)
        {
            if (w != 0 && _byId.TryGetValue(id, out var item))
            {
                _profile.AddScaled(Vec(item), w);
            }
        }

        _cold = !_weights.Values.Any(w => w > 0);
        ComputePopularity();
        ComputeCf();
    }

    /// <summary>Builds the user's rows in contract order (without the Coming Soon row).</summary>
    public static IReadOnlyList<RecRow> Build(RecInput input) => new RecEngine(input).Compose();

    /// <summary>
    /// Per-title signed weight for one user. Completed=1, partial=completion*0.8, movie abandoned (&lt;10%)=-0.3,
    /// extra completions add a rewatch bonus, all with a 90-day half-life; then ratings override and My List adds +1.
    /// </summary>
    public static Dictionary<Guid, double> ItemWeights(RecInput input, Guid userId)
    {
        var result = new Dictionary<Guid, double>();
        foreach (var grp in input.Signals.Where(s => s.UserId == userId).GroupBy(s => s.ItemId))
        {
            double sum = 0;
            var completions = 0;
            foreach (var s in grp.OrderBy(x => x.At))
            {
                var age = Math.Max(0, (input.Now - s.At).TotalDays);
                var decay = Math.Pow(0.5, age / HalfLifeDays);
                double basis;
                if (s.Completed)
                {
                    basis = completions++ == 0 ? 1.0 : 0.3;
                }
                else if (!s.IsEpisode && s.Completion < 0.10)
                {
                    basis = -0.3;
                }
                else
                {
                    basis = s.Completion * 0.8;
                }

                sum += basis * decay;
            }

            result[grp.Key] = Math.Clamp(sum, -1.5, 2.0);
        }

        var prefix = userId.ToString("N") + "|";
        foreach (var (key, rating) in input.Ratings)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(key.AsSpan(prefix.Length), out var id))
            {
                continue;
            }

            switch (rating)
            {
                case -1: result[id] = -3; break;
                case 1: result[id] = 1.5; break;
                case 2: result[id] = 3; break;
            }
        }

        foreach (var key in input.MyList)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(key.AsSpan(prefix.Length), out var id))
            {
                var cur = result.GetValueOrDefault(id);
                if (cur > -3)
                {
                    result[id] = cur + 1;
                }
            }
        }

        return result;
    }

    private SparseVector Vec(CatalogItem item)
    {
        if (!_vecs.TryGetValue(item.Id, out var v))
        {
            _vecs[item.Id] = v = SparseVector.FromItem(item);
        }

        return v;
    }

    private int MyRating(Guid itemId) =>
        _in.Ratings.GetValueOrDefault(StoreData.UserItemKey(_in.UserId, itemId));

    private bool IsVisible(CatalogItem c) => _in.Visible.Contains(c.Id);

    private void ComputePopularity()
    {
        var since = _in.Now.AddDays(-30);
        var perItem = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var s in _in.Signals)
        {
            if (!_usersPerItem.ContainsKey(s.ItemId))
            {
                _usersPerItem[s.ItemId] = 0;
            }

            if (s.At >= since && (s.Completed || s.Completion >= 0.5) && !_in.ExcludedUsers.Contains(s.UserId))
            {
                if (!perItem.TryGetValue(s.ItemId, out var set))
                {
                    perItem[s.ItemId] = set = new HashSet<Guid>();
                }

                set.Add(s.UserId);
            }
        }

        foreach (var grp in _in.Signals.GroupBy(s => s.ItemId))
        {
            _usersPerItem[grp.Key] = grp.Select(s => s.UserId).Distinct().Count();
        }

        var max = perItem.Count == 0 ? 0 : perItem.Values.Max(s => s.Count);
        if (max == 0)
        {
            return;
        }

        foreach (var (id, set) in perItem)
        {
            _pop[id] = (double)set.Count / max;
        }
    }

    /// <summary>Item-item co-occurrence across OTHER users (never the target), only with >=2 users with signals.</summary>
    private void ComputeCf()
    {
        var users = _in.Signals.Select(s => s.UserId).Distinct().ToList();
        CfEnabled = IsCfEnabled(_in);
        if (!CfEnabled)
        {
            return;
        }

        var seeds = _weights.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        if (seeds.Count == 0)
        {
            return;
        }

        var positives = new List<HashSet<Guid>>();
        foreach (var u in users.Where(u => u != _in.UserId))
        {
            var set = ItemWeights(_in, u).Where(kv => kv.Value >= 0.5).Select(kv => kv.Key).ToHashSet();
            if (set.Count > 0)
            {
                positives.Add(set);
            }
        }

        var counts = new Dictionary<Guid, int>();
        foreach (var set in positives)
        {
            foreach (var id in set)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        foreach (var set in positives)
        {
            foreach (var s in set)
            {
                if (!seeds.TryGetValue(s, out var w))
                {
                    continue;
                }

                foreach (var c in set)
                {
                    if (c != s)
                    {
                        _cf[c] = _cf.GetValueOrDefault(c) + (w / Math.Sqrt(counts[s] * (double)counts[c]));
                    }
                }
            }
        }

        var max = _cf.Count == 0 ? 0 : _cf.Values.Max();
        if (max > 0)
        {
            foreach (var k in _cf.Keys.ToList())
            {
                _cf[k] /= max;
            }
        }
    }

    public bool CfEnabled { get; private set; }

    /// <summary>Collaborative filtering needs at least two users with signals.</summary>
    public static bool IsCfEnabled(RecInput input) => input.Signals.Select(s => s.UserId).Distinct().Count() >= 2;

    private static double Quality(CatalogItem c) => c.Rating is float r ? Math.Clamp(r / 10.0, 0, 1) : 0.5;

    private double Freshness(CatalogItem c)
    {
        var added = c.LatestEpisodeAdded is DateTime l && l > c.DateAdded ? l : c.DateAdded;
        var age = Math.Max(0, (_in.Now - added).TotalDays);
        return Math.Exp(-age / 60.0);
    }

    private double Score(CatalogItem c)
    {
        var pop = _pop.GetValueOrDefault(c.Id);
        if (_cold)
        {
            return (0.6 * pop) + (0.25 * Quality(c)) + (0.15 * Freshness(c));
        }

        var affinity = (SparseVector.Cosine(_profile, Vec(c)) + 1) / 2;
        var wAff = CfEnabled ? 0.55 : 0.80;
        var wCf = CfEnabled ? 0.25 : 0.0;
        return (wAff * affinity) + (wCf * _cf.GetValueOrDefault(c.Id)) + (0.10 * pop) + (0.05 * Freshness(c)) + (0.05 * Quality(c));
    }

    /// <summary>Unwatched, visible, not thumbed-down titles that may appear in recommendation rows.</summary>
    private List<CatalogItem> Candidates() =>
        _in.Catalog.Where(c => IsVisible(c) && !_touched.Contains(c.Id) && MyRating(c.Id) >= 0).ToList();

    private IReadOnlyList<RecRow> Compose()
    {
        var rows = new List<RecRow>();
        var used = new HashSet<Guid>();
        var cands = Candidates();
        var scored = cands.Select(c => (Item: c, Score: Score(c))).OrderByDescending(x => x.Score).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase).ToList();

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

        // Top Picks (or cold-start popularity). Personalised rows are deduplicated against each other.
        var picks = scored.Where(x => !used.Contains(x.Item.Id)).Take(RowSize).Select(x => x.Item).ToList();
        if (picks.Count >= MinRowItems)
        {
            used.UnionWith(picks.Select(p => p.Id));
            var title = _cold ? $"Popular on {_in.ServerName}" : "Top Picks for You";
            rows.Add(new RecRow("toppicks", title, "toppicks", OrderTopPicks, Wrap(picks)));
        }

        if (topMovies.Count >= MinChartItems)
        {
            rows.Add(new RecRow("top10-movies", $"Top 10 Movies on {_in.ServerName} {TopTenPeriod(_in.TopTenWindowDays)}", "top10", OrderTop10Movies, Wrap(topMovies, ranked: true)));
        }

        // Because you watched X (x3)
        if (!_cold)
        {
            var seedCount = 0;
            foreach (var seed in Seeds())
            {
                if (seedCount >= 3)
                {
                    break;
                }

                var seedVec = Vec(seed);
                var sims = scored
                    .Where(x => !used.Contains(x.Item.Id) && x.Item.Id != seed.Id)
                    .Select(x => (x.Item, Sim: SparseVector.Cosine(seedVec, Vec(x.Item))))
                    .Where(x => x.Sim >= 0.1)
                    .OrderByDescending(x => x.Sim + (0.1 * Quality(x.Item)))
                    .Take(RowSize)
                    .Select(x => x.Item)
                    .ToList();
                if (sims.Count >= MinRowItems)
                {
                    used.UnionWith(sims.Select(s => s.Id));
                    rows.Add(new RecRow($"because-{seed.Id:N}", $"Because you watched {seed.Name}", "because", OrderBecause, Wrap(sims)));
                    seedCount++;
                }
            }
        }

        // Trending (not deduplicated: it is a chart-like row)
        var trending = Trending();
        if (trending.Count >= MinRowItems)
        {
            rows.Add(new RecRow("trending", "Trending Now", "trending", OrderTrending, Wrap(trending)));
        }

        // My List
        var myList = _in.MyList
            .Where(k => k.StartsWith(_in.UserId.ToString("N") + "|", StringComparison.Ordinal))
            .Select(k => Guid.TryParse(k.AsSpan(33), out var g) ? g : Guid.Empty)
            .Where(g => _byId.ContainsKey(g))
            .Select(g => _byId[g])
            .Where(IsVisible)
            .OrderByDescending(c => c.DateAdded)
            .ToList();
        if (myList.Count >= 1)
        {
            rows.Add(new RecRow("mylist", "My List", "mylist", OrderMyList, Wrap(myList)));
        }

        // Genre rows from the profile's strongest genres
        if (!_cold)
        {
            var genreRows = 0;
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            var genres = _profile.Values.Where(kv => kv.Key.StartsWith("g:", StringComparison.Ordinal) && kv.Value > 0)
                .OrderByDescending(kv => kv.Value).Select(kv => kv.Key[2..]).Take(6);
            foreach (var genre in genres)
            {
                if (genreRows >= 3)
                {
                    break;
                }

                var items = scored
                    .Where(x => !used.Contains(x.Item.Id) && x.Item.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
                    .Take(RowSize).Select(x => x.Item).ToList();
                if (items.Count >= MinRowItems)
                {
                    used.UnionWith(items.Select(i => i.Id));
                    var title = _in.RowTitles.TryGetValue(genre, out var t) && !string.IsNullOrWhiteSpace(t) ? t : $"{genre} Picks for You";
                    rows.Add(new RecRow(UniqueGenreId(genre, usedIds), title, "genre", OrderGenre, Wrap(items)));
                    genreRows++;
                }
            }
        }

        if (topShows.Count >= MinChartItems)
        {
            rows.Add(new RecRow("top10-shows", $"Top 10 Shows on {_in.ServerName} {TopTenPeriod(_in.TopTenWindowDays)}", "top10", OrderTop10Shows, Wrap(topShows, ranked: true)));
        }

        var newSeasons = NewSeasons();
        if (newSeasons.Count >= 1)
        {
            rows.Add(new RecRow("newseasons", "New Episodes", "newseasons", OrderNewSeasons, Wrap(newSeasons)));
        }

        var recent = _in.Catalog.Where(IsVisible)
            .OrderByDescending(c => c.LatestEpisodeAdded is DateTime l && l > c.DateAdded ? l : c.DateAdded)
            .Take(RowSize).ToList();
        if (recent.Count >= MinRowItems)
        {
            rows.Add(new RecRow("recent", "Recently Added", "recent", OrderRecent, Wrap(recent)));
        }

        var hidden = scored.Where(x => !used.Contains(x.Item.Id) && x.Item.Rating >= 7.5f && _usersPerItem.GetValueOrDefault(x.Item.Id) <= 1)
            .OrderByDescending(x => x.Item.Rating).ThenByDescending(x => x.Score)
            .Take(RowSize).Select(x => x.Item).ToList();
        if (hidden.Count >= MinRowItems)
        {
            used.UnionWith(hidden.Select(i => i.Id));
            rows.Add(new RecRow("hidden", "Hidden Gems", "hidden", OrderHidden, Wrap(hidden)));
        }

        var again = WatchAgain();
        if (again.Count >= MinChartItems)
        {
            rows.Add(new RecRow("again", "Watch Again", "again", OrderAgain, Wrap(again)));
        }

        return rows.OrderBy(r => r.Order).ToList();
    }

    /// <summary>"This Week" for the default 7 days; otherwise the title says what the window really is.</summary>
    public static string TopTenPeriod(int windowDays) => windowDays switch
    {
        <= 1 => "Today",
        7 => "This Week",
        _ => $"in the Last {windowDays} Days",
    };

    /// <summary>
    /// "genre-{slug}", unique within the response: "Sci-Fi" and "Sci Fi" would otherwise both become "genre-sci-fi"
    /// (clients key their lists on the row id and crash on duplicates).
    /// </summary>
    public static string UniqueGenreId(string genre, ISet<string> used)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(genre.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
        if (slug.Length == 0)
        {
            slug = "genre";
        }

        var id = "genre-" + slug;
        for (var n = 2; !used.Add(id); n++)
        {
            id = $"genre-{slug}-{n}";
        }

        return id;
    }

    private List<RankedItem> Wrap(IEnumerable<CatalogItem> items, bool ranked = false)
    {
        var list = new List<RankedItem>();
        var i = 0;
        foreach (var c in items)
        {
            i++;
            list.Add(new RankedItem(c, BadgesFor(c), ranked ? i : null, null));
        }

        return list;
    }

    private string[] BadgesFor(CatalogItem c) =>
        Badges(c, _in.Now, _ranks.TryGetValue(c.Id, out var rank) ? rank : null);

    /// <summary>Badges for one title: chart rank, Recently Added (&lt;=14d), New Episodes, Top Rated (&gt;=8.0).</summary>
    public static string[] Badges(CatalogItem c, DateTime now, int? topTenRank = null)
    {
        var badges = new List<string>();
        if (topTenRank is int rank)
        {
            badges.Add($"#{rank} in {(c.Kind == CatalogKind.Movie ? "Movies" : "Shows")}");
        }

        if ((now - c.DateAdded).TotalDays <= 14)
        {
            badges.Add("Recently Added");
        }

        if (c.Kind == CatalogKind.Series && c.LatestEpisodeAdded is DateTime l
            && (now - l).TotalDays <= 14 && (now - c.DateAdded).TotalDays > 30)
        {
            badges.Add("New Episodes");
        }

        if (c.Rating >= 8.0f)
        {
            badges.Add("Top Rated");
        }

        return badges.ToArray();
    }

    /// <summary>
    /// 0..1 when the newest signal for a title is a resumable one (3%-95%, not completed, within 90 days), else null.
    /// </summary>
    public static double? ResumeProgress(PlaySignal latest, DateTime now)
    {
        if (latest.Completed || latest.Completion < 0.03 || latest.Completion >= 0.95 || (now - latest.At).TotalDays > 90)
        {
            return null;
        }

        return latest.Completion;
    }

    private List<RankedItem> ContinueItems()
    {
        var result = new List<RankedItem>();
        var seen = new HashSet<Guid>();
        foreach (var grp in _mySignals.GroupBy(s => s.ItemId).Select(g => g.OrderByDescending(s => s.At).First()).OrderByDescending(s => s.At))
        {
            if (ResumeProgress(grp, _in.Now) is not double progress)
            {
                continue;
            }

            if (_byId.TryGetValue(grp.ItemId, out var item) && IsVisible(item) && seen.Add(item.Id))
            {
                result.Add(new RankedItem(item, BadgesFor(item), null, progress));
            }
        }

        // A series whose last episode was finished has nothing "in progress" but still has a next episode to play.
        foreach (var id in _in.NextUpSeries)
        {
            if (_byId.TryGetValue(id, out var series) && series.Kind == CatalogKind.Series && IsVisible(series)
                && MyRating(id) >= 0 && seen.Add(id))
            {
                result.Add(new RankedItem(series, BadgesFor(series), null, null));
            }
        }

        return result;
    }

    /// <summary>Latest completed/loved titles, newest first, as "Because you watched" seeds.</summary>
    private IEnumerable<CatalogItem> Seeds()
    {
        var last = _mySignals.Where(s => s.Completed).GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Max(s => s.At));
        foreach (var (key, rating) in _in.Ratings)
        {
            if (rating == 2 && key.StartsWith(_in.UserId.ToString("N") + "|", StringComparison.Ordinal)
                && Guid.TryParse(key.AsSpan(33), out var id) && !last.ContainsKey(id))
            {
                last[id] = _in.Now.AddDays(-1);
            }
        }

        return last.OrderByDescending(kv => kv.Value)
            .Where(kv => MyRating(kv.Key) >= 0 && _byId.ContainsKey(kv.Key))
            .Select(kv => _byId[kv.Key])
            .Take(8);
    }

    /// <summary>Top 10 for one kind: distinct users first, then completed plays; windowed, excluded users removed.</summary>
    private List<CatalogItem> TopTen(CatalogKind kind)
    {
        var since = _in.Now.AddDays(-Math.Max(1, _in.TopTenWindowDays));
        return _in.Signals
            .Where(s => s.Completed && s.At >= since && !_in.ExcludedUsers.Contains(s.UserId))
            .GroupBy(s => s.ItemId)
            .Where(g => _byId.TryGetValue(g.Key, out var c) && c.Kind == kind && IsVisible(c))
            .Select(g => (Item: _byId[g.Key], Users: g.Select(s => s.UserId).Distinct().Count(), Plays: g.Count()))
            .OrderByDescending(x => x.Users).ThenByDescending(x => x.Plays)
            .ThenByDescending(x => x.Item.Rating ?? 0).ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10).Select(x => x.Item).ToList();
    }

    /// <summary>Momentum: recent engagement with a 3-day half-life, one vote per user and title.</summary>
    private List<CatalogItem> Trending()
    {
        var since = _in.Now.AddDays(-14);
        return _in.Signals
            .Where(s => s.At >= since && (s.Completed || s.Completion >= 0.25) && !_in.ExcludedUsers.Contains(s.UserId))
            .GroupBy(s => (s.ItemId, s.UserId))
            .Select(g => (g.Key.ItemId, Weight: Math.Pow(0.5, Math.Max(0, (_in.Now - g.Max(s => s.At)).TotalDays) / 3.0)))
            .GroupBy(x => x.ItemId)
            .Select(g => (Id: g.Key, Score: g.Sum(x => x.Weight)))
            .Where(x => _byId.TryGetValue(x.Id, out var c) && IsVisible(c))
            .OrderByDescending(x => x.Score).ThenBy(x => _byId[x.Id].Name, StringComparer.OrdinalIgnoreCase)
            .Take(RowSize).Select(x => _byId[x.Id]).ToList();
    }

    private List<CatalogItem> NewSeasons()
    {
        return _touched
            .Where(id => _byId.TryGetValue(id, out var c) && c.Kind == CatalogKind.Series && IsVisible(c)
                         && c.LatestEpisodeAdded is DateTime l && (_in.Now - l).TotalDays <= 14
                         && (_in.Now - c.DateAdded).TotalDays > 30 && MyRating(id) >= 0)
            .Select(id => _byId[id])
            .OrderByDescending(c => c.LatestEpisodeAdded)
            .ToList();
    }

    private List<CatalogItem> WatchAgain()
    {
        return _mySignals.Where(s => s.Completed).GroupBy(s => s.ItemId)
            .Where(g => (_in.Now - g.Max(s => s.At)).TotalDays >= 30 && _byId.TryGetValue(g.Key, out var c) && IsVisible(c) && MyRating(g.Key) >= 0)
            .Select(g => _byId[g.Key])
            .OrderByDescending(c => _weights.GetValueOrDefault(c.Id)).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(RowSize).ToList();
    }
}
