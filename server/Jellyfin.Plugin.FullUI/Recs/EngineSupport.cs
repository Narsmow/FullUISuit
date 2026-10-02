using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>Unit-length AI embeddings by title id, built once and shared between requests.</summary>
public sealed class EmbeddingIndex
{
    private readonly Dictionary<Guid, float[]> _unit;

    private EmbeddingIndex(Dictionary<Guid, float[]> unit) => _unit = unit;

    public int Count => _unit.Count;

    public static EmbeddingIndex Create(IEnumerable<KeyValuePair<Guid, float[]>> vectors)
    {
        var d = new Dictionary<Guid, float[]>();
        foreach (var (id, v) in vectors)
        {
            if (v.Length == 0)
            {
                continue;
            }

            double n = 0;
            foreach (var x in v)
            {
                n += x * (double)x;
            }

            if (n < 1e-12 || double.IsNaN(n) || double.IsInfinity(n))
            {
                continue;
            }

            var inv = (float)(1.0 / Math.Sqrt(n));
            var u = new float[v.Length];
            for (var i = 0; i < v.Length; i++)
            {
                u[i] = v[i] * inv;
            }

            d[id] = u;
        }

        return new EmbeddingIndex(d);
    }

    /// <summary>The unit vector for a title, or null when it has none.</summary>
    public float[]? Unit(Guid id) => _unit.GetValueOrDefault(id);

    /// <summary>Cosine of two titles, or null when either has no vector (or their sizes differ).</summary>
    public double? Cosine(Guid a, Guid b)
    {
        var x = Unit(a);
        var y = Unit(b);
        return x is null || y is null ? null : Dot(x, y);
    }

    public static double? Dot(float[] x, float[] y)
    {
        if (x.Length != y.Length)
        {
            return null;
        }

        double s = 0;
        for (var i = 0; i < x.Length; i++)
        {
            s += x[i] * (double)y[i];
        }

        return s;
    }
}

/// <summary>Remembers each user's per-title weights between requests; entries are only trusted while the data fingerprint matches.</summary>
public sealed class UserWeightsCache
{
    private readonly ConcurrentDictionary<Guid, (string Fingerprint, Dictionary<Guid, double> Weights)> _cache = new();

    public int Misses { get; private set; }

    public Dictionary<Guid, double> GetOrAdd(Guid userId, string? fingerprint, Func<Dictionary<Guid, double>> factory)
    {
        if (fingerprint is null)
        {
            return factory();
        }

        if (_cache.TryGetValue(userId, out var hit) && hit.Fingerprint == fingerprint)
        {
            return hit.Weights;
        }

        var w = factory();
        Misses++;
        _cache[userId] = (fingerprint, w);
        return w;
    }
}

/// <summary>
/// Spots users with a restrictive parental cap without any Jellyfin API: such a user can see (almost) none of the mature-rated
/// titles that exist in the library. This is a heuristic on purpose (the parental-cap properties of the user entity changed
/// names within the 10.11 line); it only runs behind the ExcludeKidsFromSharedSignals setting.
/// </summary>
public static class KidDetector
{
    private static readonly HashSet<string> Mature = new(StringComparer.OrdinalIgnoreCase)
    {
        "R", "NC-17", "X", "TV-MA", "18", "18+", "16", "FSK-16", "FSK-18", "MA15+", "R18+", "R-18", "NC-16",
    };

    /// <summary>True when the library has at least 5 mature titles and the user can see at most 5% of them.</summary>
    public static bool IsRestrictive(IReadOnlyList<CatalogItem> catalog, IReadOnlySet<Guid> visible)
    {
        var total = 0;
        var seen = 0;
        foreach (var c in catalog)
        {
            if (!string.IsNullOrWhiteSpace(c.OfficialRating) && Mature.Contains(c.OfficialRating.Trim()))
            {
                total++;
                if (visible.Contains(c.Id))
                {
                    seen++;
                }
            }
        }

        return total >= 5 && seen <= total * 0.05;
    }
}

/// <summary>Per-library-snapshot data that is expensive to rebuild: id lookup, IDF weights, item vectors, duplicate groups, collections.</summary>
public sealed class CatalogIndex
{
    private static readonly ConditionalWeakTable<IReadOnlyList<CatalogItem>, CatalogIndex> Cache = new();
    private readonly ConcurrentDictionary<Guid, SparseVector> _vecs = new();
    private readonly Dictionary<string, double> _idf = new();
    private readonly double _idfDefault;
    private readonly CatalogItem? _first;
    private readonly CatalogItem? _last;

    private CatalogIndex(IReadOnlyList<CatalogItem> items)
    {
        Items = items;
        Count = items.Count;
        _first = items.Count > 0 ? items[0] : null;
        _last = items.Count > 0 ? items[^1] : null;
        ById = new Dictionary<Guid, CatalogItem>(items.Count);
        foreach (var c in items)
        {
            ById.TryAdd(c.Id, c);
        }

        // IDF: a feature present on every title carries little information, a rare one a lot (multiplier 0.5 .. 1.5).
        var df = new Dictionary<string, int>();
        foreach (var c in items)
        {
            foreach (var k in SparseVector.FromItem(c).Values.Keys)
            {
                df[k] = df.GetValueOrDefault(k) + 1;
            }
        }

        var n = Math.Max(2, items.Count);
        var denom = Math.Log(n + 1);
        foreach (var (k, d) in df)
        {
            _idf[k] = 0.5 + Math.Clamp(Math.Log((n + 1.0) / (d + 1.0)) / denom, 0, 1);
        }

        _idfDefault = 1.5;

        var groups = new Dictionary<string, List<CatalogItem>>();
        var byCollection = new Dictionary<Guid, List<CatalogItem>>();
        foreach (var c in items)
        {
            var key = DuplicateKey(c);
            if (key is not null)
            {
                if (!groups.TryGetValue(key, out var g))
                {
                    groups[key] = g = new List<CatalogItem>();
                }

                g.Add(c);
            }

            if (c.CollectionId is Guid col)
            {
                if (!byCollection.TryGetValue(col, out var m))
                {
                    byCollection[col] = m = new List<CatalogItem>();
                }

                m.Add(c);
            }
        }

        DuplicateGroups = groups.Values.Where(g => g.Count > 1)
            .Select(g => g.OrderByDescending(MetaScore).ThenBy(x => x.Id).ToArray())
            .ToList();
        Collections = byCollection.Where(kv => kv.Value.Count > 1)
            .ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(m => m.Year ?? int.MaxValue).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public IReadOnlyList<CatalogItem> Items { get; }

    public int Count { get; }

    public Dictionary<Guid, CatalogItem> ById { get; }

    /// <summary>Groups of two or more editions of the same title (same TMDB id), best edition first.</summary>
    public IReadOnlyList<CatalogItem[]> DuplicateGroups { get; }

    /// <summary>Collection id to its movies, oldest first; only collections with two or more members.</summary>
    public IReadOnlyDictionary<Guid, CatalogItem[]> Collections { get; }

    public static CatalogIndex For(IReadOnlyList<CatalogItem> catalog)
    {
        if (Cache.TryGetValue(catalog, out var idx) && idx.IsCurrent(catalog))
        {
            return idx;
        }

        idx = new CatalogIndex(catalog);
        Cache.AddOrUpdate(catalog, idx);
        return idx;
    }

    public double Idf(string key) => _idf.TryGetValue(key, out var v) ? v : _idfDefault;

    public SparseVector Vec(CatalogItem item) =>
        _vecs.GetOrAdd(item.Id, _ => SparseVector.FromItem(item, Idf));

    private bool IsCurrent(IReadOnlyList<CatalogItem> catalog) =>
        catalog.Count == Count && (Count == 0 || (ReferenceEquals(catalog[0], _first) && ReferenceEquals(catalog[^1], _last)));

    private static int MetaScore(CatalogItem c) =>
        (c.HasBackdrop ? 2 : 0) + (c.HasLogo ? 1 : 0) + (string.IsNullOrWhiteSpace(c.Overview) ? 0 : 2) + (c.Rating is null ? 0 : 1)
        + (c.Genres.Count > 0 ? 2 : 0) + (c.Cast.Count > 0 ? 1 : 0) + (string.IsNullOrEmpty(c.TrailerKey) ? 0 : 1) + (c.PrimaryImageTag is null ? 0 : 1);

    private static string? DuplicateKey(CatalogItem c)
    {
        if (c.TmdbId is int t && t > 0)
        {
            return $"{(int)c.Kind}:t{t}";
        }

        if (c.Year is int y && y > 0 && !string.IsNullOrWhiteSpace(c.Name))
        {
            return $"{(int)c.Kind}:n{c.Name.Trim().ToLowerInvariant()}|{y}";
        }

        return null;
    }
}
