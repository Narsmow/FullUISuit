using System;
using System.Collections.Generic;
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

    /// <summary>A generated title is kept this long before it is asked for again, so the home screen does not change every night.</summary>
    public static readonly TimeSpan TitleLifetime = TimeSpan.FromDays(7);

    /// <summary>The clock; replaceable in tests.</summary>
    internal Func<DateTime> Now { get; set; } = static () => DateTime.UtcNow;

    /// <summary>Up to 5 titles that best represent the genre: most watched by the household first, then best rated.</summary>
    internal static IReadOnlyList<string> SampleTitles(IReadOnlyList<CatalogItem> items, IReadOnlyDictionary<Guid, int> watchCounts, string genre, int take = 5)
        => items.Where(i => i.Genres.Contains(genre, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(i => watchCounts.GetValueOrDefault(i.Id))
            .ThenByDescending(i => i.Rating ?? 0)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(i => i.Name)
            .ToList();

    public async Task<int> RunAsync(CancellationToken ct, int maxGenres = 12)
    {
        if (!_ollama.Enabled)
        {
            return 0;
        }

        var all = _catalog.All;
        var now = Now();
        var (existing, stamps, watch) = _store.Read(d => (
            new Dictionary<string, string>(d.RowTitles),
            new Dictionary<string, DateTime>(d.RowTitleStamps),
            d.Signals.GroupBy(s => s.ItemId).ToDictionary(g => g.Key, g => g.Select(s => s.UserId).Distinct().Count())));
        var genres = all.SelectMany(i => i.Genres).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(maxGenres).Select(g => g.Key).ToList();
        var n = 0;
        foreach (var genre in genres)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.TryGetValue(genre, out var current) && !string.IsNullOrWhiteSpace(current)
                && stamps.TryGetValue(genre, out var at) && now - at < TitleLifetime)
            {
                continue; // still fresh: no new title this week
            }

            var sample = string.Join(", ", SampleTitles(all, watch, genre));
            var raw = await _ollama.ChatAsync(
                "You write short, evocative row titles for a streaming home screen. Answer with ONLY the title: 2 to 5 words, Title Case, no quotes, no punctuation at the end.",
                $"Genre: {genre}. Example titles in this row: {sample}. Write one fresh row title for this genre, like \"Twisty Sci-Fi Mysteries\".",
                ct).ConfigureAwait(false);
            var title = Validate(raw);
            if (title is null)
            {
                continue;
            }

            _store.Write(d =>
            {
                d.RowTitles[genre] = title;
                d.RowTitleStamps[genre] = now;
            });
            n++;
        }

        return n;
    }
}
