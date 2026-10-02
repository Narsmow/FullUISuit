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

    /// <summary>genres 1.0, studios 0.5, tags 0.7, decade 0.5, official rating 0.4.</summary>
    public static SparseVector FromItem(CatalogItem item)
    {
        var v = new SparseVector();
        foreach (var g in item.Genres)
        {
            v.Add("g:" + g, 1.0);
        }

        foreach (var s in item.Studios)
        {
            v.Add("s:" + s, 0.5);
        }

        foreach (var t in item.Tags)
        {
            v.Add("t:" + t, 0.7);
        }

        if (item.Year is int y && y > 0)
        {
            v.Add("d:" + (y / 10 * 10), 0.5);
        }

        if (!string.IsNullOrWhiteSpace(item.OfficialRating))
        {
            v.Add("r:" + item.OfficialRating.Trim().ToUpperInvariant(), 0.4);
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
