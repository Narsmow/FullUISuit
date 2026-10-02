using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <returns>Number of embeddings newly stored.</returns>
    public async Task<int> RunAsync(IProgress<double>? progress, CancellationToken ct, int maxItems = int.MaxValue)
    {
        if (!_ollama.Enabled)
        {
            return 0;
        }

        var items = _catalog.All;
        var ids = items.Select(i => i.Id.ToString("N")).ToHashSet();
        _store.Write(d =>
        {
            foreach (var k in d.Embeddings.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                d.Embeddings.Remove(k);
            }
        });

        var have = _store.Read(d => d.Embeddings.Keys.ToHashSet());
        var todo = items.Where(i => !have.Contains(i.Id.ToString("N"))).Take(maxItems).ToList();
        var done = 0;
        for (var offset = 0; offset < todo.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = todo.Skip(offset).Take(BatchSize).ToList();
            var vecs = await _ollama.EmbedAsync(batch.Select(TextFor).ToList(), ct).ConfigureAwait(false);
            if (vecs is null || vecs.Count != batch.Count)
            {
                break; // Ollama unreachable or model missing: try again next run
            }

            _store.Write(d =>
            {
                for (var n = 0; n < batch.Count; n++)
                {
                    d.Embeddings[batch[n].Id.ToString("N")] = vecs[n];
                }
            });
            done += batch.Count;
            progress?.Report(100.0 * Math.Min(offset + BatchSize, todo.Count) / Math.Max(1, todo.Count));
        }

        return done;
    }
}
