using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Ai;

/// <summary>Computes embeddings for catalog items that lack one, in small batches (CPU friendly).</summary>
public sealed class EmbeddingIndexer
{
    private const int BatchSize = 8;
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IOllamaClient _ollama;

    public EmbeddingIndexer(PluginStore store, ICatalog catalog, IOllamaClient ollama)
    {
        _store = store;
        _catalog = catalog;
        _ollama = ollama;
    }

    public static string TextFor(CatalogItem i)
    {
        var sb = new StringBuilder();
        sb.Append(i.Name);
        if (i.Year is not null)
        {
            sb.Append(" (").Append(i.Year).Append(')');
        }

        sb.Append(i.Kind == CatalogKind.Series ? ". TV series." : ". Movie.");
        if (i.Genres.Count > 0)
        {
            sb.Append(" Genres: ").Append(string.Join(", ", i.Genres)).Append('.');
        }

        if (i.Directors.Count > 0)
        {
            sb.Append(" Directed by ").Append(string.Join(", ", i.Directors)).Append('.');
        }

        if (i.Cast.Count > 0)
        {
            sb.Append(" Starring ").Append(string.Join(", ", i.Cast.Take(4))).Append('.');
        }

        if (i.Tags.Count > 0)
        {
            sb.Append(" Tags: ").Append(string.Join(", ", i.Tags.Take(15))).Append('.');
        }

        if (!string.IsNullOrWhiteSpace(i.Overview))
        {
            var o = i.Overview!.Trim();
            sb.Append(' ').Append(o.Length > 700 ? o[..700] : o);
        }

        return sb.ToString();
    }

    /// <summary>Short stable hash of the text a vector was computed from.</summary>
    public static string HashOf(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>
    /// Version of how texts are turned into embedding input. It is part of every stored hash, so bumping it re-embeds everything.
    /// 2 = nomic-embed-text task prefixes ("search_document: " for titles, "search_query: " for searches).
    /// </summary>
    public const int SchemeVersion = 2;

    /// <summary>nomic-embed-text was trained with task prefixes; other models get the plain text.</summary>
    public static bool UsesTaskPrefixes(string? model) => model?.Contains("nomic", StringComparison.OrdinalIgnoreCase) == true;

    public static string DocumentPrefix(string? model) => UsesTaskPrefixes(model) ? "search_document: " : string.Empty;

    public static string QueryPrefix(string? model) => UsesTaskPrefixes(model) ? "search_query: " : string.Empty;

    /// <summary>The exact text sent to the embedding model for a title.</summary>
    public static string DocumentText(CatalogItem i, string? model) => DocumentPrefix(model) + TextFor(i);

    /// <summary>The hash a vector must carry to be considered current for this title and model.</summary>
    public static string ExpectedHash(CatalogItem i, string? model) => HashOf($"v{SchemeVersion}|{DocumentText(i, model)}");

    /// <returns>Number of embeddings newly stored (new titles plus titles re-embedded because the model or their text changed).</returns>
    public async Task<int> RunAsync(IProgress<double>? progress, CancellationToken ct, int maxItems = int.MaxValue)
    {
        if (!_ollama.Enabled)
        {
            return 0;
        }

        var items = _catalog.All;
        var model = _ollama.EmbedModel;
        var ids = items.Select(i => i.Id.ToString("N")).ToHashSet();
        var hashes = items.ToDictionary(i => i.Id.ToString("N"), i => ExpectedHash(i, model));
        _store.WriteEmbeddings(e =>
        {
            foreach (var k in e.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                e.Remove(k);
            }

            // Vectors migrated from an older version carry no model/hash: assume they match the current setup once.
            foreach (var (k, v) in e.Where(kv => kv.Value.Model.Length == 0 && hashes.ContainsKey(kv.Key)).ToList())
            {
                e[k] = new EmbeddingEntry { Vector = v.Vector, Model = model, Hash = hashes[k] };
            }
        });

        // Missing, produced by another embedding model, or the title text changed since it was embedded.
        var current = _store.ReadEmbeddings(e => e.ToDictionary(kv => kv.Key, kv => (kv.Value.Model, kv.Value.Hash)));
        var todo = items
            .Where(i =>
            {
                var k = i.Id.ToString("N");
                return !current.TryGetValue(k, out var c)
                    || !string.Equals(c.Model, model, StringComparison.OrdinalIgnoreCase)
                    || c.Hash != hashes[k];
            })
            .Take(maxItems).ToList();
        var done = 0;
        for (var offset = 0; offset < todo.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = todo.Skip(offset).Take(BatchSize).ToList();
            var vecs = await _ollama.EmbedAsync(batch.Select(i => DocumentText(i, model)).ToList(), ct).ConfigureAwait(false);
            if (vecs is null || vecs.Count != batch.Count)
            {
                break; // Ollama unreachable or model missing: try again next run
            }

            _store.WriteEmbeddings(e =>
            {
                for (var n = 0; n < batch.Count; n++)
                {
                    var k = batch[n].Id.ToString("N");
                    e[k] = new EmbeddingEntry { Vector = vecs[n], Model = model, Hash = hashes[k] };
                }
            });
            done += batch.Count;
            progress?.Report(100.0 * Math.Min(offset + BatchSize, todo.Count) / Math.Max(1, todo.Count));
        }

        return done;
    }
}
