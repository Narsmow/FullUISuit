using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// Who is in a title. Implemented by the library layer once the catalog carries cast; until then <see cref="NullCastIndex"/>
/// is used and search matches names only. Implementations must be cheap to call for every library item.
/// </summary>
public interface ICastIndex
{
    /// <summary>Top-billed people (actors, directors) of the title, or empty when unknown.</summary>
    IReadOnlyList<string> CastOf(Guid itemId);
}

public sealed class NullCastIndex : ICastIndex
{
    public IReadOnlyList<string> CastOf(Guid itemId) => Array.Empty<string>();
}

public sealed record SuggestTitle(string Id, string Name, int? Year, string Type);

public sealed record SuggestPerson(string Name, int Titles, string[] ItemIds);

public sealed record SuggestResponse(IReadOnlyList<SuggestTitle> Titles, IReadOnlyList<SuggestPerson> People, IReadOnlyList<string> Genres);

/// <summary>Pure text matching used by suggestions and by search typo tolerance: normalising, trigrams, edit distance.</summary>
public static class FuzzyText
{
    /// <summary>Lower case, accents removed, punctuation turned into spaces, single spaces.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var lastSpace = true;
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                sb.Append(' ');
                lastSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    public static string[] Tokens(string normalized) => normalized.Length == 0 ? Array.Empty<string>() : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static HashSet<string> Trigrams(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var padded = "  " + normalized + " ";
        for (var i = 0; i + 3 <= padded.Length; i++)
        {
            set.Add(padded.Substring(i, 3));
        }

        return set;
    }

    public static double TrigramSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var inter = a.Count(b.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    /// <summary>Edit distance (insert, delete, replace, swap of two neighbours), giving up (returns max+1) once it must exceed <paramref name="max"/>.</summary>
    public static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max)
        {
            return max + 1;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            var rowMin = int.MaxValue;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
                rowMin = Math.Min(rowMin, v);
            }

            if (rowMin > max)
            {
                return max + 1;
            }
        }

        return d[a.Length, b.Length];
    }

    /// <summary>
    /// How well <paramref name="query"/> matches <paramref name="name"/> (both normalised): 1 exact, 0.9 name starts with the
    /// query, 0.8 a word starts with it, 0.7 contains it, 0.4..0.6 close enough to be a typo, 0 no match.
    /// </summary>
    public static double Score(string query, string name, HashSet<string>? nameTrigrams = null)
    {
        if (query.Length == 0 || name.Length == 0)
        {
            return 0;
        }

        if (name == query)
        {
            return 1.0;
        }

        if (name.StartsWith(query, StringComparison.Ordinal))
        {
            return 0.9;
        }

        var qTokens = Tokens(query);
        var nTokens = Tokens(name);
        if (nTokens.Any(t => t.StartsWith(query, StringComparison.Ordinal)) || (" " + name).Contains(" " + query, StringComparison.Ordinal))
        {
            return 0.8;
        }

        if (query.Length >= 3 && name.Contains(query, StringComparison.Ordinal))
        {
            return 0.7;
        }

        // Typos: every query word must be close to some word of the name (prefix typed so far counts too).
        if (query.Length >= 3)
        {
            var allClose = qTokens.All(q => nTokens.Any(t => CloseWord(q, t)));
            if (allClose)
            {
                return 0.6;
            }

            var sim = TrigramSimilarity(Trigrams(query), nameTrigrams ?? Trigrams(name));
            if (sim >= 0.45)
            {
                return 0.4 + Math.Min(0.15, (sim - 0.45));
            }
        }

        return 0;
    }

    private static bool CloseWord(string q, string t)
    {
        if (q.Length < 3)
        {
            return t.StartsWith(q, StringComparison.Ordinal);
        }

        var allow = q.Length <= 4 ? 1 : 2;
        if (Levenshtein(q, t, allow) <= allow)
        {
            return true;
        }

        // The word may still be being typed: compare against the same-length start of the candidate.
        return t.Length > q.Length && Levenshtein(q, t[..q.Length], allow) <= allow;
    }
}

/// <summary>Typeahead over titles, people and genres, restricted to what the caller may see. The name index is rebuilt only when the library snapshot changes.</summary>
public sealed class SuggestService
{
    private const int MaxTitles = 8;
    private const int MaxPeople = 5;
    private const int MaxGenres = 4;

    private readonly ICatalog _catalog;
    private readonly ICastIndex _cast;
    private readonly object _lock = new();
    private IReadOnlyList<CatalogItem>? _builtFor;
    private int _builtCount = -1;
    private List<Entry> _titles = new();
    private Dictionary<string, PersonEntry> _people = new();

    public SuggestService(ICatalog catalog, ICastIndex cast)
    {
        _catalog = catalog;
        _cast = cast;
    }

    private sealed record Entry(CatalogItem Item, string Norm, HashSet<string> Trigrams);

    private sealed class PersonEntry
    {
        public PersonEntry(string display, string norm)
        {
            Display = display;
            Norm = norm;
            Trigrams = FuzzyText.Trigrams(norm);
        }

        public string Display { get; }

        public string Norm { get; }

        public HashSet<string> Trigrams { get; }

        public List<Guid> Items { get; } = new();
    }

    private void EnsureIndex()
    {
        var all = _catalog.All;
        lock (_lock)
        {
            if (ReferenceEquals(_builtFor, all) && _builtCount == all.Count)
            {
                return;
            }

            var titles = all.Select(i =>
            {
                var n = FuzzyText.Normalize(i.Name);
                return new Entry(i, n, FuzzyText.Trigrams(n));
            }).ToList();
            var people = new Dictionary<string, PersonEntry>(StringComparer.Ordinal);
            foreach (var i in all)
            {
                IReadOnlyList<string> cast;
                try
                {
                    cast = _cast.CastOf(i.Id);
                }
                catch (Exception)
                {
                    continue; // one broken lookup never breaks search
                }

                foreach (var name in cast.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30))
                {
                    var norm = FuzzyText.Normalize(name);
                    if (norm.Length == 0)
                    {
                        continue;
                    }

                    if (!people.TryGetValue(norm, out var p))
                    {
                        people[norm] = p = new PersonEntry(name.Trim(), norm);
                    }

                    p.Items.Add(i.Id);
                }
            }

            _titles = titles;
            _people = people;
            _builtFor = all;
            _builtCount = all.Count;
        }
    }

    public SuggestResponse Suggest(Guid userId, string? q)
    {
        var query = FuzzyText.Normalize(q);
        if (query.Length < 2)
        {
            return new SuggestResponse(Array.Empty<SuggestTitle>(), Array.Empty<SuggestPerson>(), Array.Empty<string>());
        }

        EnsureIndex();
        var visible = _catalog.VisibleTo(userId);
        List<Entry> titles;
        List<PersonEntry> people;
        lock (_lock)
        {
            titles = _titles;
            people = _people.Values.ToList();
        }

        var titleHits = titles
            .Where(e => visible.Contains(e.Item.Id))
            .Select(e => (e.Item, Score: FuzzyText.Score(query, e.Norm, e.Trigrams)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.Rating ?? 0)
            .ThenBy(x => x.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxTitles)
            .Select(x => new SuggestTitle(x.Item.Id.ToString("N"), x.Item.Name, x.Item.Year, x.Item.Kind == CatalogKind.Movie ? "Movie" : "Series"))
            .ToList();

        var peopleHits = people
            .Select(p => (Person: p, Score: FuzzyText.Score(query, p.Norm, p.Trigrams), Visible: p.Items.Where(visible.Contains).Distinct().ToList()))
            .Where(x => x.Score > 0 && x.Visible.Count > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Visible.Count)
            .ThenBy(x => x.Person.Display, StringComparer.OrdinalIgnoreCase)
            .Take(MaxPeople)
            .Select(x => new SuggestPerson(x.Person.Display, x.Visible.Count, x.Visible.Take(5).Select(g => g.ToString("N")).ToArray()))
            .ToList();

        var genres = titles
            .Where(e => visible.Contains(e.Item.Id))
            .SelectMany(e => e.Item.Genres)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Score: FuzzyText.Score(query, FuzzyText.Normalize(g.Key)), Count: g.Count()))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Count)
            .Take(MaxGenres)
            .Select(x => x.Name)
            .ToList();

        return new SuggestResponse(titleHits, peopleHits, genres);
    }

    /// <summary>
    /// Extra results for keyword search: titles with a person whose name matches the query well (exact, starts with, or a close
    /// typo), and - when the normal search found nothing - titles whose name is a close typo of the query. Visible titles only.
    /// </summary>
    public IReadOnlyList<CatalogItem> Extras(Guid userId, string query, bool includeTypoTitles)
    {
        var q = FuzzyText.Normalize(query);
        if (q.Length < 3)
        {
            return Array.Empty<CatalogItem>();
        }

        EnsureIndex();
        var visible = _catalog.VisibleTo(userId);
        List<Entry> titles;
        List<PersonEntry> people;
        lock (_lock)
        {
            titles = _titles;
            people = _people.Values.ToList();
        }

        var byId = titles.ToDictionary(t => t.Item.Id, t => t.Item);
        var found = new List<CatalogItem>();
        foreach (var p in people.Select(p => (P: p, Score: FuzzyText.Score(q, p.Norm, p.Trigrams))).Where(x => x.Score >= 0.6).OrderByDescending(x => x.Score).Take(3))
        {
            found.AddRange(p.P.Items.Where(visible.Contains).Distinct().Select(id => byId[id]).OrderByDescending(i => i.Rating ?? 0));
        }

        if (includeTypoTitles)
        {
            found.AddRange(titles.Where(e => visible.Contains(e.Item.Id)).Select(e => (e.Item, Score: FuzzyText.Score(q, e.Norm, e.Trigrams)))
                .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(10).Select(x => x.Item));
        }

        return found.DistinctBy(i => i.Id).ToList();
    }
}
