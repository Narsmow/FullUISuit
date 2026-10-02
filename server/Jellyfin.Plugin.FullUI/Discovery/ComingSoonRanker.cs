using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>A library title that seeds recommendations for a user.</summary>
public sealed record Seed(CatalogItem Item, double Weight)
{
    public string MediaType => Item.Kind == CatalogKind.Series ? "tv" : "movie";
}

/// <summary>Pure functions: seed selection, candidate scoring and exclusion for Coming Soon.</summary>
public static class ComingSoonRanker
{
    public const int MaxSeeds = 20;
    public const int KeepPerUser = 30;

    public static string MediaTypeOf(CatalogItem item) => item.Kind == CatalogKind.Series ? "tv" : "movie";

    public static string Key(string mediaType, int tmdbId) => $"{mediaType}:{tmdbId}";

    /// <summary>
    /// Love/like/completed/my-list titles, weighted by strength and recency, most relevant first.
    /// Only titles with a TmdbId; titles the user rated -1 never seed.
    /// </summary>
    public static IReadOnlyList<Seed> SelectSeeds(StoreData data, Guid userId, IEnumerable<CatalogItem> catalog, DateTime now, int max = MaxSeeds)
    {
        var seeds = new List<Seed>();
        foreach (var item in catalog)
        {
            if (item.TmdbId is not > 0)
            {
                continue;
            }

            var ukey = StoreData.UserItemKey(userId, item.Id);
            var w = 0.0;
            if (data.Ratings.TryGetValue(ukey, out var rating))
            {
                if (rating < 0)
                {
                    continue;
                }

                w += rating >= 2 ? 3 : 2;
            }

            if (data.MyList.Contains(ukey))
            {
                w += 1.5;
            }

            var latest = DateTime.MinValue;
            var completed = false;
            var best = 0.0;
            foreach (var s in data.Signals)
            {
                if (s.UserId != userId || s.ItemId != item.Id)
                {
                    continue;
                }

                completed |= s.Completed;
                best = Math.Max(best, s.Completion);
                if (s.At > latest)
                {
                    latest = s.At;
                }
            }

            if (completed)
            {
                w += 2;
            }
            else if (best > 0.5)
            {
                w += 1;
            }

            if (w <= 0)
            {
                continue;
            }

            if (latest > DateTime.MinValue)
            {
                var ageDays = Math.Max(0, (now - latest).TotalDays);
                w += Math.Max(0, 1 - (ageDays / 180.0));
            }

            seeds.Add(new Seed(item, w));
        }

        return seeds.OrderByDescending(s => s.Weight).ThenBy(s => s.Item.Name, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    /// <summary>Most frequent genres across the seeds (weighted), top <paramref name="n"/>.</summary>
    public static IReadOnlyList<string> TopGenres(IEnumerable<Seed> seeds, int n = 3)
    {
        var score = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in seeds)
        {
            foreach (var g in s.Item.Genres)
            {
                score[g] = score.GetValueOrDefault(g) + s.Weight;
            }
        }

        return score.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Take(n).Select(kv => kv.Key).ToList();
    }

    private static string Norm(string s)
    {
        var t = s.ToLowerInvariant().Replace("sci-fi", "science fiction").Replace("scifi", "science fiction");
        return t.Replace("&", "and").Trim();
    }

    /// <summary>Maps library genre names to TMDB genre ids (tolerant: "Sci-Fi" ~ "Science Fiction", "Action" ~ "Action &amp; Adventure").</summary>
    public static ISet<int> MapGenreIds(IEnumerable<string> libraryGenres, IEnumerable<TmdbGenre> tmdbGenres)
    {
        var ids = new HashSet<int>();
        var tg = tmdbGenres.Select(g => (g.Id, Name: Norm(g.Name))).ToList();
        foreach (var lg in libraryGenres)
        {
            var n = Norm(lg);
            if (n.Length == 0)
            {
                continue;
            }

            foreach (var (id, name) in tg)
            {
                if (name == n || name.Split(' ').Contains(n) || n.Split(' ').Contains(name))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    public sealed class Candidate
    {
        public Candidate(TmdbTitle title)
        {
            Title = title;
        }

        public TmdbTitle Title { get; }

        public int SeedHits { get; set; }

        public bool Upcoming { get; set; }
    }

    /// <summary>
    /// Excludes library titles, titles voted -1, and titles with no poster; scores the rest and keeps the best.
    /// score = 2 per recommending seed + vote_average/10*2 + recency (upcoming soon / recent release) + 0.5 genre match.
    /// </summary>
    public static IReadOnlyList<ComingSoonEntry> Rank(
        IEnumerable<Candidate> candidates,
        ISet<string> libraryKeys,
        ISet<string> downvotedKeys,
        ISet<int> topGenreIds,
        DateTime now,
        int take = KeepPerUser)
    {
        var scored = new List<ComingSoonEntry>();
        foreach (var c in candidates)
        {
            var t = c.Title;
            var key = Key(t.MediaType, t.Id);
            if (libraryKeys.Contains(key) || downvotedKeys.Contains(key) || string.IsNullOrWhiteSpace(t.PosterPath))
            {
                continue;
            }

            var score = (c.SeedHits * 2.0) + (Math.Clamp(t.VoteAverage, 0, 10) / 10.0 * 2.0) + RecencyBonus(t.ReleaseDate, now);
            if (topGenreIds.Count > 0 && t.GenreIds.Any(topGenreIds.Contains))
            {
                score += 0.5;
            }

            scored.Add(new ComingSoonEntry
            {
                TmdbId = t.Id,
                MediaType = t.MediaType,
                Title = t.Title,
                Overview = t.Overview,
                PosterPath = t.PosterPath,
                BackdropPath = t.BackdropPath,
                ReleaseDate = t.ReleaseDate,
                Score = Math.Round(score, 3),
            });
        }

        return scored.OrderByDescending(e => e.Score).ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase).Take(take).ToList();
    }

    /// <summary>+1.5 for releases within the next 120 days, decaying for the past: +1 within a year, then 0.</summary>
    public static double RecencyBonus(string? releaseDate, DateTime now)
    {
        if (!DateTime.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
        {
            return 0;
        }

        var days = (d - now).TotalDays;
        if (days >= 0)
        {
            return days <= 120 ? 1.5 : Math.Max(0, 1.5 - ((days - 120) / 240.0));
        }

        var age = -days;
        return age <= 365 ? 1.0 - (age / 365.0 * 0.5) : 0;
    }
}
