using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Ai;

public sealed record SearchHit(CatalogItem Item, double Score);

/// <summary>Natural-language search: cosine rank over embeddings when Ollama works, else keyword search.</summary>
public sealed class NlSearch
{
    private static readonly Regex Splitter = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IOllamaClient _ollama;

    public NlSearch(PluginStore store, ICatalog catalog, IOllamaClient ollama)
    {
        _store = store;
        _catalog = catalog;
        _ollama = ollama;
    }

    public async Task<(string mode, IReadOnlyList<SearchHit> hits)> SearchAsync(Guid userId, string query, CancellationToken ct, int take = 40)
    {
        var visible = _catalog.VisibleTo(userId);
        var items = _catalog.All.Where(i => visible.Contains(i.Id)).ToList();
        if (_ollama.Enabled)
        {
            var q = await _ollama.EmbedQueryAsync(EmbeddingIndexer.QueryPrefix(_ollama.EmbedModel) + query, ct).ConfigureAwait(false);
            if (q is { Count: 1 })
            {
                var model = _ollama.EmbedModel;
                // Vectors from a different model have another dimension/meaning: ignore them until they are re-embedded.
                var emb = _store.ReadEmbeddings(e => e
                    .Where(kv => kv.Value.Model.Length == 0 || model.Length == 0 || string.Equals(kv.Value.Model, model, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value.Vector));
                var hits = Semantic(q[0], emb, items, query, take);
                if (hits.Count > 0)
                {
                    // Titles without a usable vector yet (just added, or model changed) must still be findable by words.
                    var unindexed = items.Where(i => !emb.ContainsKey(i.Id.ToString("N"))).ToList();
                    var extra = unindexed.Count == 0
                        ? Array.Empty<SearchHit>()
                        : Keyword(unindexed, query, take).Where(h => h.Item.Name.Length > 0).ToArray();
                    return ("semantic", hits.Concat(extra).Take(take).ToList());
                }
            }
        }

        return ("keyword", Keyword(items, query, take));
    }

    public static IReadOnlyList<SearchHit> Semantic(float[] queryVec, IReadOnlyDictionary<string, float[]> embeddings, IEnumerable<CatalogItem> visible, string query, int take, double minScore = 0.3)
    {
        var tokens = Tokens(query);
        var res = new List<SearchHit>();
        foreach (var i in visible)
        {
            if (!embeddings.TryGetValue(i.Id.ToString("N"), out var v))
            {
                continue;
            }

            var s = Cosine(queryVec, v);
            if (tokens.Count > 0 && tokens.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                s += 0.15; // literal title matches float to the top
            }

            if (s >= minScore)
            {
                res.Add(new SearchHit(i, s));
            }
        }

        return res.OrderByDescending(h => h.Score).Take(take).ToList();
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static IReadOnlyList<string> Tokens(string q)
        => Splitter.Split(q.ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();

    /// <summary>Keyword search over name, overview, genres, tags and year. All words must match; else any word.</summary>
    public static IReadOnlyList<SearchHit> Keyword(IEnumerable<CatalogItem> visible, string query, int take = 40)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0)
        {
            return Array.Empty<SearchHit>();
        }

        var scored = visible.Select(i => (item: i, parts: tokens.Select(t => TokenScore(i, t)).ToList())).ToList();
        var all = scored.Where(x => x.parts.All(p => p > 0)).Select(x => new SearchHit(x.item, x.parts.Sum() + 100)).ToList();
        var hits = all.Count > 0
            ? all
            : scored.Where(x => x.parts.Any(p => p > 0)).Select(x => new SearchHit(x.item, x.parts.Sum())).ToList();
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Item.Name, StringComparer.OrdinalIgnoreCase).Take(take).ToList();
    }

    private static double TokenScore(CatalogItem i, string t)
    {
        double s = 0;
        if (i.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))
        {
            s += 5;
        }
        else if (i.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
        {
            s += 3;
        }

        if (i.Genres.Any(g => g.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            s += 2;
        }

        if (i.Tags.Any(g => g.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            s += 2;
        }

        if (i.Year is not null && i.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) == t)
        {
            s += 2;
        }

        if (i.Overview?.Contains(t, StringComparison.OrdinalIgnoreCase) == true)
        {
            s += 1;
        }

        return s;
    }
}
