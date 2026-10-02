using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Ai;

/// <summary>Asks the chat model for short evocative genre row titles; invalid answers are dropped silently.</summary>
public sealed class RowTitleGenerator
{
    private static readonly Regex Allowed = new(@"^[\p{L}\p{N} &'\-:,!]+$", RegexOptions.Compiled);
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly IOllamaClient _ollama;

    public RowTitleGenerator(PluginStore store, ICatalog catalog, IOllamaClient ollama)
    {
        _store = store;
        _catalog = catalog;
        _ollama = ollama;
    }

    /// <summary>Returns a cleaned title, or null if the model output is unusable.</summary>
    public static string? Validate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var first = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var s = first.Trim().Trim('"', '\'', '`', '*', '“', '”').Trim().TrimEnd('.');
        if (s.Length is < 6 or > 40)
        {
            return null;
        }

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words is < 2 or > 6 || !Allowed.IsMatch(s))
        {
            return null;
        }

        return s;
    }

    public async Task<int> RunAsync(CancellationToken ct, int maxGenres = 12)
    {
        if (!_ollama.Enabled)
        {
            return 0;
        }

        var genres = _catalog.All.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(maxGenres).Select(g => g.Key).ToList();
        var n = 0;
        foreach (var genre in genres)
        {
            ct.ThrowIfCancellationRequested();
            var sample = string.Join(", ", _catalog.All.Where(i => i.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase)).Take(5).Select(i => i.Name));
            var raw = await _ollama.ChatAsync(
                "You write short, evocative row titles for a streaming home screen. Answer with ONLY the title: 2 to 5 words, Title Case, no quotes, no punctuation at the end.",
                $"Genre: {genre}. Example titles in this row: {sample}. Write one fresh row title for this genre, like \"Twisty Sci-Fi Mysteries\".",
                ct).ConfigureAwait(false);
            var title = Validate(raw);
            if (title is null)
            {
                continue;
            }

            _store.Write(d => d.RowTitles[genre] = title);
            n++;
        }

        return n;
    }
}
