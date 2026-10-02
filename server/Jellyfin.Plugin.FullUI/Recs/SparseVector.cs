using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>Sparse feature vector with cached L2 norm.</summary>
public sealed class SparseVector
{
    private double? _norm;

    public Dictionary<string, double> Values { get; } = new();

    public double Norm => _norm ??= Math.Sqrt(SumSquares());

    public void Add(string key, double weight)
    {
        Values[key] = Values.GetValueOrDefault(key) + weight;
        _norm = null;
    }

    public void AddScaled(SparseVector other, double scale)
    {
        foreach (var (k, v) in other.Values)
        {
            Values[k] = Values.GetValueOrDefault(k) + (v * scale);
        }

        _norm = null;
    }

    public static double Cosine(SparseVector a, SparseVector b)
    {
        if (a.Values.Count == 0 || b.Values.Count == 0)
        {
            return 0;
        }

        var (small, large) = a.Values.Count <= b.Values.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (k, v) in small.Values)
        {
            if (large.Values.TryGetValue(k, out var w))
            {
                dot += v * w;
            }
        }

        var denom = a.Norm * b.Norm;
        return denom < 1e-9 ? 0 : dot / denom;
    }

    /// <summary>
    /// genres 1.0, studios 0.5, tags 0.7, decade 0.5, official rating 0.4, cast 0.25 each, director 0.5,
    /// collection 0.9, original language 0.3. Without <paramref name="idf"/> every feature keeps its base weight.
    /// </summary>
    public static SparseVector FromItem(CatalogItem item, Func<string, double>? idf = null)
    {
        var v = new SparseVector();
        void Put(string key, double weight) => v.Add(key, idf is null ? weight : weight * idf(key));

        foreach (var g in item.Genres)
        {
            Put("g:" + g, 1.0);
        }

        foreach (var s in item.Studios)
        {
            Put("s:" + s, 0.5);
        }

        foreach (var t in item.Tags)
        {
            Put("t:" + t, 0.7);
        }

        if (item.Year is int y && y > 0)
        {
            Put("d:" + (y / 10 * 10), 0.5);
        }

        if (!string.IsNullOrWhiteSpace(item.OfficialRating))
        {
            Put("r:" + item.OfficialRating.Trim().ToUpperInvariant(), 0.4);
        }

        foreach (var c in item.Cast)
        {
            Put("c:" + c, 0.25);
        }

        foreach (var d in item.Directors)
        {
            Put("dir:" + d, 0.5);
        }

        if (item.CollectionId is Guid col)
        {
            Put("col:" + col.ToString("N"), 0.9);
        }

        if (!string.IsNullOrWhiteSpace(item.OriginalLanguage))
        {
            Put("l:" + item.OriginalLanguage.Trim().ToLowerInvariant(), 0.3);
        }

        return v;
    }

    private double SumSquares()
    {
        double s = 0;
        foreach (var v in Values.Values)
        {
            s += v * v;
        }

        return s;
    }
}
