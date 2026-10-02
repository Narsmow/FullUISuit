using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>
/// Pure recommendation engine: content similarity (cosine over sparse features with IDF weights, plus optional AI embeddings),
/// a decayed per-user profile, optional item-item collaborative filtering and the row builders. No Jellyfin types.
/// </summary>
public sealed partial class RecEngine
{
    // Row positions, exactly the order in docs/api-contract.md. "comingsoon" (5) is inserted by HomeService.
    public const int OrderContinue = 0;
    public const int OrderTopPicks = 1;
    public const int OrderTop10Movies = 2;
    public const int OrderBecause = 3;
    public const int OrderCollection = 3;   // "Next in the X collection" sits right before the Because-you-watched rows
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
    private const int MinChartItems = 3;

    /// <summary>Weight of the embedding similarity next to the feature similarity (when both titles have a vector).</summary>
    public const double EmbeddingWeight = 0.25;

    private readonly RecInput _in;
    private readonly CatalogIndex _idx;
    private readonly Dictionary<Guid, Guid> _alias;              // duplicate edition -> the edition we show
    private readonly IReadOnlyList<CatalogItem> _catalog;        // one entry per title (duplicates merged)
    private readonly List<PlaySignal> _signals;                  // everyone's signals, duplicates remapped
    private readonly ILookup<Guid, PlaySignal> _byUser;
    private readonly List<PlaySignal> _mySignals;
    private readonly UserTables _tables;
    private readonly Dictionary<Guid, int> _myRatings;
    private readonly HashSet<Guid> _myList;
    private readonly HashSet<Guid> _hidden;
    private readonly IReadOnlyList<Guid> _nextUp;
    private readonly Dictionary<Guid, SeriesState> _series;
    private readonly HashSet<Guid> _finishedMovies = new();
    private readonly HashSet<Guid> _sharedExcluded;              // users whose signals are left out of charts, popularity and CF
    private readonly Dictionary<Guid, double> _weights;
    private readonly HashSet<Guid> _touched;                     // titles the user has any signal for
    private readonly SparseVector _profile = new();
    private readonly bool _cold;
    private readonly Dictionary<Guid, double> _pop = new();
    private readonly Dictionary<Guid, int> _usersPerItem = new();
    private readonly Dictionary<Guid, double> _cf = new();
    private readonly Dictionary<Guid, double> _scoreCache = new();
    private float[]? _embProfile;
    private double[] _scoreSorted = Array.Empty<double>();
    private Dictionary<Guid, int> _ranks = new();
    private List<CatalogItem>? _seedsCache;

    private RecEngine(RecInput input)
    {
        _in = input;
        _idx = CatalogIndex.For(input.Catalog);
        _alias = BuildAliases(input, _idx);
        _catalog = _alias.Count == 0 ? input.Catalog : input.Catalog.Where(c => !_alias.ContainsKey(c.Id)).ToList();
        _signals = _alias.Count == 0 ? input.Signals as List<PlaySignal> ?? input.Signals.ToList() : input.Signals.Select(s => Remap(s)).ToList();
        _byUser = _signals.ToLookup(s => s.UserId);
        _mySignals = _byUser[input.UserId].ToList();
        _touched = _mySignals.Select(s => s.ItemId).ToHashSet();
        _tables = UserTables.Build(input.Ratings, input.MyList, _alias);
        _myRatings = _tables.Ratings.GetValueOrDefault(input.UserId) ?? new Dictionary<Guid, int>();
        _myList = _tables.MyList.GetValueOrDefault(input.UserId) ?? new HashSet<Guid>();
        _hidden = input.HiddenItems.Select(Canon).ToHashSet();
        _nextUp = input.NextUpSeries.Select(Canon).Distinct().ToList();

        var viewerIsKid = input.KidUsers.Contains(input.UserId);
        _sharedExcluded = new HashSet<Guid>(input.ExcludedUsers);
        if (!viewerIsKid)
        {
            _sharedExcluded.UnionWith(input.KidUsers);
        }

        _series = SeriesStates.Compute(input, _idx, _mySignals, id => _myRatings.GetValueOrDefault(id), id => _myList.Contains(id), useWatchSource: true);
        foreach (var grp in _mySignals.Where(s => !s.IsEpisode && s.Completed))
        {
            _finishedMovies.Add(grp.ItemId);
        }

        _weights = ComputeWeights(input.Now, _mySignals, _myRatings, _myList, _series);
        foreach (var (id, w) in _weights)
        {
            if (w != 0 && _idx.ById.TryGetValue(id, out var item))
            {
                _profile.AddScaled(Vec(item), w);
            }
        }

        _cold = !_weights.Values.Any(w => w > 0);
        BuildEmbeddingProfile();
        ComputePopularity();
        ComputeCf();
    }

    /// <summary>Builds the user's rows in contract order (without the Coming Soon row).</summary>
    public static IReadOnlyList<RecRow> Build(RecInput input) => new RecEngine(input).Compose();

    private static Dictionary<Guid, Guid> BuildAliases(RecInput input, CatalogIndex idx)
    {
        var alias = new Dictionary<Guid, Guid>();
        if (idx.DuplicateGroups.Count == 0)
        {
            return alias;
        }

        var counts = new Dictionary<Guid, int>();
        foreach (var s in input.Signals)
        {
            counts[s.ItemId] = counts.GetValueOrDefault(s.ItemId) + 1;
        }

        foreach (var group in idx.DuplicateGroups)
        {
            // Stable ordering keeps the catalog's own metadata-quality order for ties.
            var best = group.OrderByDescending(m => input.Visible.Contains(m.Id) ? 1 : 0)
                .ThenByDescending(m => counts.GetValueOrDefault(m.Id))
                .First();
            foreach (var m in group)
            {
                if (m.Id != best.Id)
                {
                    alias[m.Id] = best.Id;
                }
            }
        }

        return alias;
    }

    private Guid Canon(Guid id) => _alias.TryGetValue(id, out var p) ? p : id;

    private PlaySignal Remap(PlaySignal s) => !_alias.TryGetValue(s.ItemId, out var p)
        ? s
        : new PlaySignal { UserId = s.UserId, ItemId = p, IsEpisode = s.IsEpisode, At = s.At, Completion = s.Completion, Completed = s.Completed, Season = s.Season, Episode = s.Episode };

    /// <summary>Ratings and My List entries parsed once per user ("{user:N}|{item:N}" keys; malformed keys are ignored).</summary>
    internal sealed class UserTables
    {
        public Dictionary<Guid, Dictionary<Guid, int>> Ratings { get; } = new();

        public Dictionary<Guid, HashSet<Guid>> MyList { get; } = new();

        public static UserTables Build(IReadOnlyDictionary<string, int> ratings, IReadOnlySet<string> myList, IReadOnlyDictionary<Guid, Guid>? alias = null)
        {
            var t = new UserTables();
            foreach (var (key, rating) in ratings)
            {
                if (!TryParseKey(key, alias, out var user, out var item))
                {
                    continue;
                }

                if (!t.Ratings.TryGetValue(user, out var d))
                {
                    t.Ratings[user] = d = new Dictionary<Guid, int>();
                }

                // Two editions of one title: a thumbs-down wins, otherwise the stronger like.
                d[item] = d.TryGetValue(item, out var cur) ? (cur == -1 || rating == -1 ? -1 : Math.Max(cur, rating)) : rating;
            }

            foreach (var key in myList)
            {
                if (TryParseKey(key, alias, out var user, out var item))
                {
                    if (!t.MyList.TryGetValue(user, out var set))
                    {
                        t.MyList[user] = set = new HashSet<Guid>();
                    }

                    set.Add(item);
                }
            }

            return t;
        }

        private static bool TryParseKey(string key, IReadOnlyDictionary<Guid, Guid>? alias, out Guid user, out Guid item)
        {
            user = item = Guid.Empty;
            if (key.Length != 65 || key[32] != '|'
                || !Guid.TryParseExact(key.AsSpan(0, 32), "N", out user)
                || !Guid.TryParseExact(key.AsSpan(33), "N", out item))
            {
                return false;
            }

            if (alias is not null && alias.TryGetValue(item, out var p))
            {
                item = p;
            }

            return true;
        }
    }

    /// <summary>
    /// Per-title signed weight for one user. Movies: completed=1, partial=completion*0.8, abandoned (&lt;10%)=-0.3, extra
    /// completions add a rewatch bonus. Series: the share of episodes watched (at least 0.15 once started), -0.3 when dropped.
    /// All with a 90-day half-life; then ratings override and My List adds +1.
    /// </summary>
    public static Dictionary<Guid, double> ItemWeights(RecInput input, Guid userId)
    {
        var idx = CatalogIndex.For(input.Catalog);
        var tables = UserTables.Build(input.Ratings, input.MyList);
        var ratings = tables.Ratings.GetValueOrDefault(userId) ?? new Dictionary<Guid, int>();
        var list = tables.MyList.GetValueOrDefault(userId) ?? new HashSet<Guid>();
        var sigs = input.Signals.Where(s => s.UserId == userId).ToList();
        var states = SeriesStates.Compute(input, idx, sigs, id => ratings.GetValueOrDefault(id), id => list.Contains(id),
            useWatchSource: userId == input.UserId);
        return ComputeWeights(input.Now, sigs, ratings, list, states);
    }

    private static Dictionary<Guid, double> ComputeWeights(
        DateTime now,
        IReadOnlyList<PlaySignal> signals,
        IReadOnlyDictionary<Guid, int> ratings,
        IReadOnlySet<Guid> myList,
        IReadOnlyDictionary<Guid, SeriesState> states)
    {
        var result = new Dictionary<Guid, double>();
        foreach (var grp in signals.GroupBy(s => s.ItemId))
        {
            double sum = 0;
            if (grp.Any(s => s.IsEpisode))
            {
                var real = grp.Where(s => s.Season != 0).ToList();   // specials never count
                if (real.Count == 0)
                {
                    continue;
                }

                var latest = real.Max(s => s.At);
                var decay = Math.Pow(0.5, Math.Max(0, (now - latest).TotalDays) / HalfLifeDays);
                double basis;
                if (states.TryGetValue(grp.Key, out var st))
                {
                    basis = st.Dropped ? -0.3 : Math.Max(0.15, st.Share);
                }
                else
                {
                    basis = Math.Max(0.15, Math.Min(1.0, real.Count(s => s.Completed) / 12.0));
                }

                sum = basis * decay;
            }
            else
            {
                var completions = 0;
                foreach (var s in grp.OrderBy(x => x.At))
                {
                    var age = Math.Max(0, (now - s.At).TotalDays);
                    var decay = Math.Pow(0.5, age / HalfLifeDays);
                    double basis;
                    if (s.Completed)
                    {
                        basis = completions++ == 0 ? 1.0 : 0.3;
                    }
                    else if (s.Completion < 0.10)
                    {
                        basis = -0.3;
                    }
                    else
                    {
                        basis = s.Completion * 0.8;
                    }

                    sum += basis * decay;
                }
            }

            result[grp.Key] = Math.Clamp(sum, -1.5, 2.0);
        }

        foreach (var (id, rating) in ratings)
        {
            switch (rating)
            {
                case -1: result[id] = -3; break;
                case 1: result[id] = 1.5; break;
                case 2: result[id] = 3; break;
            }
        }

        foreach (var id in myList)
        {
            var cur = result.GetValueOrDefault(id);
            if (cur > -3)
            {
                result[id] = cur + 1;
            }
        }

        return result;
    }

    private SparseVector Vec(CatalogItem item) => _idx.Vec(item);

    private int MyRating(Guid itemId) => _myRatings.GetValueOrDefault(itemId);

    private bool IsVisible(CatalogItem c) => _in.Visible.Contains(c.Id);

    private bool IsDropped(Guid id) => _series.TryGetValue(id, out var st) && st.Dropped;

    /// <summary>The user has nothing left to watch of this title: a completed movie, or a series they are caught up on.</summary>
    private bool IsFinished(CatalogItem c) => c.Kind == CatalogKind.Movie
        ? _finishedMovies.Contains(c.Id)
        : _series.TryGetValue(c.Id, out var st) && st.CaughtUp;

    /// <summary>
    /// THE eligibility rule every row goes through: visible to the user, not thumbed down, not hidden by the user, not a
    /// dropped show and (except for Watch Again) not already finished.
    /// </summary>
    private bool Eligible(CatalogItem c, bool allowFinished = false) =>
        IsVisible(c) && MyRating(c.Id) >= 0 && !_hidden.Contains(c.Id) && !IsDropped(c.Id) && (allowFinished || !IsFinished(c));

    private bool Shared(Guid user) => !_sharedExcluded.Contains(user);

    private void ComputePopularity()
    {
        var since = _in.Now.AddDays(-30);
        var perItem = new Dictionary<Guid, HashSet<Guid>>();
        var users = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var s in _signals)
        {
            if (!users.TryGetValue(s.ItemId, out var u))
            {
                users[s.ItemId] = u = new HashSet<Guid>();
            }

            u.Add(s.UserId);

            if (s.At >= since && (s.Completed || s.Completion >= 0.5) && Shared(s.UserId))
            {
                if (!perItem.TryGetValue(s.ItemId, out var set))
                {
                    perItem[s.ItemId] = set = new HashSet<Guid>();
                }

                set.Add(s.UserId);
            }
        }

        foreach (var (id, set) in users)
        {
            _usersPerItem[id] = set.Count;
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

    private Dictionary<Guid, double> WeightsOfOtherUser(Guid userId)
    {
        Dictionary<Guid, double> Compute()
        {
            var ratings = _tables.Ratings.GetValueOrDefault(userId) ?? new Dictionary<Guid, int>();
            var list = _tables.MyList.GetValueOrDefault(userId) ?? new HashSet<Guid>();
            var sigs = _byUser[userId].ToList();
            var states = SeriesStates.Compute(_in, _idx, sigs, id => ratings.GetValueOrDefault(id), id => list.Contains(id), useWatchSource: false);
            return ComputeWeights(_in.Now, sigs, ratings, list, states);
        }

        return _in.WeightsCache is null || _in.DataFingerprint is null
            ? Compute()
            : _in.WeightsCache.GetOrAdd(userId, _in.DataFingerprint + "|" + AliasSignature(), Compute);
    }

    private string AliasSignature()
    {
        if (_alias.Count == 0)
        {
            return string.Empty;
        }

        var h = 17;
        foreach (var (k, v) in _alias)
        {
            h ^= (k.GetHashCode() * 31) ^ v.GetHashCode();
        }

        return _alias.Count + ":" + h;
    }

    /// <summary>
    /// Item-item co-occurrence across OTHER users (never the target, never excluded users or - unless the viewer is one - kids),
    /// only with >=2 users with signals. A user only contributes when they share enough positives with the target, and their
    /// contribution is shrunk towards zero for small overlaps.
    /// </summary>
    private void ComputeCf()
    {
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

        var minOverlap = Math.Min(MinOverlap, seeds.Count);
        var positives = new List<(HashSet<Guid> Set, double Shrink)>();
        foreach (var u in _byUser.Select(g => g.Key).Where(u => u != _in.UserId && Shared(u)))
        {
            var set = WeightsOfOtherUser(u).Where(kv => kv.Value >= 0.5).Select(kv => kv.Key).ToHashSet();
            if (set.Count == 0)
            {
                continue;
            }

            var overlap = set.Count(seeds.ContainsKey);
            if (overlap < minOverlap)
            {
                continue;
            }

            positives.Add((set, overlap / (overlap + ShrinkK)));
        }

        var counts = new Dictionary<Guid, int>();
        foreach (var (set, _) in positives)
        {
            foreach (var id in set)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        foreach (var (set, shrink) in positives)
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
                        _cf[c] = _cf.GetValueOrDefault(c) + (shrink * w / Math.Sqrt(counts[s] * (double)counts[c]));
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

    /// <summary>Another user must share at least this many positively-rated titles with the target (or all of the target's, if fewer).</summary>
    public const int MinOverlap = 2;

    /// <summary>Shrinkage constant: a user with overlap n counts n / (n + K).</summary>
    public const double ShrinkK = 2.0;

    public bool CfEnabled { get; private set; }

    /// <summary>Collaborative filtering needs at least two users with signals (excluded users, and kids unless the viewer is one, do not count).</summary>
    public static bool IsCfEnabled(RecInput input)
    {
        var viewerIsKid = input.KidUsers.Contains(input.UserId);
        return input.Signals.Select(s => s.UserId)
            .Where(u => u == input.UserId || (!input.ExcludedUsers.Contains(u) && (viewerIsKid || !input.KidUsers.Contains(u))))
            .Distinct().Count() >= 2;
    }

    private static double Quality(CatalogItem c) => c.Rating is float r ? Math.Clamp(r / 10.0, 0, 1) : 0.5;

    private double Freshness(CatalogItem c)
    {
        var added = c.LatestEpisodeAdded is DateTime l && l > c.DateAdded ? l : c.DateAdded;
        var age = Math.Max(0, (_in.Now - added).TotalDays);
        return Math.Exp(-age / 60.0);
    }

    private void BuildEmbeddingProfile()
    {
        var emb = _in.Embeddings;
        if (emb is null || emb.Count == 0 || _cold)
        {
            return;
        }

        double[]? acc = null;
        foreach (var (id, w) in _weights)
        {
            if (w <= 0 || emb.Unit(id) is not float[] v)
            {
                continue;
            }

            acc ??= new double[v.Length];
            if (acc.Length != v.Length)
            {
                continue;
            }

            for (var i = 0; i < v.Length; i++)
            {
                acc[i] += w * v[i];
            }
        }

        if (acc is null)
        {
            return;
        }

        var n = Math.Sqrt(acc.Sum(x => x * x));
        if (n < 1e-9)
        {
            return;
        }

        _embProfile = acc.Select(x => (float)(x / n)).ToArray();
    }

    /// <summary>0..1 similarity of two raw embedding cosines (nomic-style vectors sit around 0.5 for unrelated titles).</summary>
    private static double EmbeddingSim(double cosine) => Math.Clamp(0.5 + ((cosine - 0.55) * 1.25), 0, 1);

    /// <summary>Feature similarity of two titles, blended with their embedding similarity when both have a vector.</summary>
    private double Similarity(CatalogItem a, CatalogItem b)
    {
        var feat = SparseVector.Cosine(Vec(a), Vec(b));
        var emb = _in.Embeddings;
        if (emb is null || emb.Unit(a.Id) is null)
        {
            return feat;   // no vector for the reference title: features only, for every candidate alike
        }

        // A candidate without a vector gets a neutral embedding term so it neither wins nor loses against ones that have one.
        var e = emb.Cosine(a.Id, b.Id) is double cos ? Math.Clamp((cos - 0.2) / 0.8, 0, 1) : 0.5;
        return ((1 - EmbeddingWeight) * feat) + (EmbeddingWeight * e);
    }

    private double Score(CatalogItem c)
    {
        if (_scoreCache.TryGetValue(c.Id, out var cached))
        {
            return cached;
        }

        var pop = _pop.GetValueOrDefault(c.Id);
        double score;
        if (_cold)
        {
            score = (0.6 * pop) + (0.25 * Quality(c)) + (0.15 * Freshness(c));
        }
        else
        {
            var affinity = (SparseVector.Cosine(_profile, Vec(c)) + 1) / 2;
            if (_embProfile is not null)
            {
                var es = _in.Embeddings?.Unit(c.Id) is float[] cv && EmbeddingIndex.Dot(_embProfile, cv) is double dcos ? EmbeddingSim(dcos) : 0.5;
                affinity = ((1 - EmbeddingWeight) * affinity) + (EmbeddingWeight * es);
            }

            var wAff = CfEnabled ? 0.55 : 0.80;
            var wCf = CfEnabled ? 0.25 : 0.0;
            score = (wAff * affinity) + (wCf * _cf.GetValueOrDefault(c.Id)) + (0.10 * pop) + (0.05 * Freshness(c)) + (0.05 * Quality(c));
        }

        _scoreCache[c.Id] = score;
        return score;
    }

    /// <summary>Eligible titles the user has no signal for yet: what recommendation rows may draw from.</summary>
    private List<CatalogItem> Candidates() =>
        _catalog.Where(c => Eligible(c) && !_touched.Contains(c.Id)).ToList();

    /// <summary>Smallest row we still show: 5 for a normal library, down to 3 when the library is small.</summary>
    public static int MinRowFor(int candidates) => candidates >= 50 ? 5 : candidates >= 24 ? 4 : 3;

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
}
