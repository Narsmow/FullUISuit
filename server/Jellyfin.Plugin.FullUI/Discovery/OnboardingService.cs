using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>First-run "pick a few titles you like" so a new user's home is not empty. Offered once, and never to users who already have history.</summary>
public sealed class OnboardingService
{
    /// <summary>A user with this many different titles of history (plays, ratings, My List) is not offered onboarding.</summary>
    public const int HistoryThreshold = 5;

    public const int SuggestionCount = 24;
    public const double MinRating = 6.5;
    private const int MaxLiked = 100;
    private const int MaxGenres = 30;

    private readonly PluginStore _store;
    private readonly ICatalog _catalog;

    public OnboardingService(PluginStore store, ICatalog catalog)
    {
        _store = store;
        _catalog = catalog;
    }

    /// <summary>How many different titles the user has any signal for (play, rating or My List).</summary>
    public int SignalCount(Guid userId)
    {
        var prefix = userId.ToString("N") + "|";
        return _store.Read(d =>
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in d.Signals.Where(s => s.UserId == userId))
            {
                ids.Add(s.ItemId.ToString("N"));
            }

            foreach (var k in d.Ratings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                ids.Add(k[prefix.Length..]);
            }

            foreach (var k in d.MyList.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                ids.Add(k[prefix.Length..]);
            }

            return ids.Count;
        });
    }

    public bool IsEligible(Guid userId)
    {
        var done = _store.Read(d => d.Onboarding.TryGetValue(userId.ToString("N"), out var st) && st.Completed);
        return !done && SignalCount(userId) < HistoryThreshold;
    }

    /// <summary>
    /// About 24 visible, well-rated titles spread over genres (and movies / shows), none the user already has a signal for.
    /// Round-robin over genres, best rated first within each, so one genre cannot take over the picker.
    /// </summary>
    public IReadOnlyList<CatalogItem> Suggestions(Guid userId)
    {
        var visible = _catalog.VisibleTo(userId);
        var prefix = userId.ToString("N") + "|";
        var seen = _store.Read(d =>
        {
            var s = new HashSet<string>(StringComparer.Ordinal);
            foreach (var x in d.Signals.Where(x => x.UserId == userId))
            {
                s.Add(x.ItemId.ToString("N"));
            }

            foreach (var k in d.Ratings.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                s.Add(k[prefix.Length..]);
            }

            foreach (var k in d.MyList.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            {
                s.Add(k[prefix.Length..]);
            }

            return s;
        });

        var pool = _catalog.All
            .Where(i => visible.Contains(i.Id) && !seen.Contains(i.Id.ToString("N")) && i.Rating is >= (float)MinRating)
            .OrderByDescending(i => i.Rating)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Group by primary genre; titles without one share a bucket so they can still be offered.
        var buckets = pool.GroupBy(i => i.Genres.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Genre: g.Key, Items: new Queue<CatalogItem>(g)))
            .OrderByDescending(b => b.Items.Count)
            .ThenBy(b => b.Genre, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var picked = new List<CatalogItem>();
        var pickedIds = new HashSet<Guid>();
        var movies = 0;
        var shows = 0;
        while (picked.Count < SuggestionCount && buckets.Any(b => b.Items.Count > 0))
        {
            foreach (var b in buckets)
            {
                if (picked.Count >= SuggestionCount)
                {
                    break;
                }

                // Within a genre take the best title; keep movies and shows roughly balanced when both exist.
                CatalogItem? next = null;
                while (b.Items.Count > 0)
                {
                    var c = b.Items.Peek();
                    if (pickedIds.Contains(c.Id))
                    {
                        b.Items.Dequeue();
                        continue;
                    }

                    next = c;
                    break;
                }

                if (next is null)
                {
                    continue;
                }

                var skewed = next.Kind == CatalogKind.Movie ? movies > shows + 4 : shows > movies + 4;
                if (skewed && b.Items.Any(x => x.Kind != next.Kind && !pickedIds.Contains(x.Id)))
                {
                    next = b.Items.First(x => x.Kind != next.Kind && !pickedIds.Contains(x.Id));
                }

                picked.Add(next);
                pickedIds.Add(next.Id);
                if (next.Kind == CatalogKind.Movie)
                {
                    movies++;
                }
                else
                {
                    shows++;
                }
            }
        }

        return picked;
    }

    /// <summary>
    /// Saves the picks: each liked title that is visible to the user and not yet rated becomes a +1 rating, chosen genres are
    /// stored as the user's genre preferences, and onboarding is marked done so it is not offered again. A skip only marks it done.
    /// </summary>
    public OnboardingOutcome Submit(Guid userId, IEnumerable<string>? liked, IEnumerable<string>? genres, bool skipped, DateTime now)
    {
        var visible = _catalog.VisibleTo(userId);
        var all = _catalog.All;
        var known = all.ToDictionary(i => i.Id);

        var likedIds = new List<Guid>();
        if (!skipped)
        {
            foreach (var raw in (liked ?? Array.Empty<string>()).Take(MaxLiked * 2))
            {
                if (Guid.TryParse(raw, out var g) && visible.Contains(g) && known.ContainsKey(g) && !likedIds.Contains(g))
                {
                    likedIds.Add(g);
                }

                if (likedIds.Count >= MaxLiked)
                {
                    break;
                }
            }
        }

        var canonical = all.SelectMany(i => i.Genres).Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Key, StringComparer.OrdinalIgnoreCase);
        var chosen = new List<string>();
        if (!skipped)
        {
            foreach (var g in (genres ?? Array.Empty<string>()).Take(MaxGenres * 2))
            {
                if (g is not null && canonical.TryGetValue(g.Trim(), out var name) && !chosen.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    chosen.Add(name);
                }

                if (chosen.Count >= MaxGenres)
                {
                    break;
                }
            }
        }

        var seeded = 0;
        _store.Write(d =>
        {
            foreach (var id in likedIds)
            {
                var key = StoreData.UserItemKey(userId, id);
                if (!d.Ratings.ContainsKey(key))
                {
                    d.Ratings[key] = 1;
                    seeded++;
                }
            }

            d.Onboarding[userId.ToString("N")] = new OnboardingState { Completed = true, Skipped = skipped, At = now, Genres = chosen };
        });
        return new OnboardingOutcome(seeded, chosen.Count, skipped);
    }

    /// <summary>The user's chosen genres (empty when none or skipped).</summary>
    public IReadOnlyList<string> GenrePreferences(Guid userId)
        => _store.Read(d => d.Onboarding.TryGetValue(userId.ToString("N"), out var st) ? st.Genres.ToList() : new List<string>());
}

public sealed record OnboardingOutcome(int Seeded, int Genres, bool Skipped);
