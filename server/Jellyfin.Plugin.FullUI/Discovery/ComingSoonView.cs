using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>Which of the stored Coming Soon entries a caller wants.</summary>
public enum ComingSoonKind
{
    /// <summary>Release date is today or later (or a new season that has not aired). The only kind the Home row shows.</summary>
    Upcoming,

    /// <summary>Already released but not in the library: "Recommended for you (not in library)".</summary>
    Released,
}

/// <summary>
/// The single definition of "which Coming Soon cards does this user see". Used by both the Home row and
/// <c>GET /FullUI/ComingSoon</c> so they can never disagree: titles already in the library are dropped,
/// titles the user voted "not for me" stay hidden, and a title is "Coming Soon" only while its release date is
/// today or later (the stored flag is not trusted: a title stored as upcoming last week may have been released since).
/// </summary>
public static class ComingSoonView
{
    public static ISet<string> LibraryKeys(IEnumerable<CatalogItem> catalog)
        => catalog.Where(i => i.TmdbId is > 0)
            .Select(i => ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value))
            .ToHashSet();

    /// <summary>Call inside <c>PluginStore.Read</c>. Upcoming cards, best score first.</summary>
    public static List<ComingSoonCard> Cards(StoreData d, Guid userId, ISet<string> libraryKeys)
        => Cards(d, userId, libraryKeys, DateTime.UtcNow, ComingSoonKind.Upcoming);

    /// <summary>Call inside <c>PluginStore.Read</c>. Best score first.</summary>
    public static List<ComingSoonCard> Cards(StoreData d, Guid userId, ISet<string> libraryKeys, DateTime now, ComingSoonKind kind)
    {
        if (!d.ComingSoon.TryGetValue(userId.ToString("N"), out var entries))
        {
            return new List<ComingSoonCard>();
        }

        var votes = d.Votes.Where(v => v.UserId == userId)
            .GroupBy(v => VoteService.StatusKey(v.MediaType, v.TmdbId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.At).First().Vote);
        var reminded = RemindedKeys(d, userId);
        var wantUpcoming = kind == ComingSoonKind.Upcoming;
        return entries
            .Where(e => e.SeasonNumber is not null || !libraryKeys.Contains(ComingSoonRanker.Key(e.MediaType, e.TmdbId)))
            .Where(e => ComingSoonRanker.IsUpcoming(e.ReleaseDate, now) == wantUpcoming)
            .Where(e => wantUpcoming || e.SeasonNumber is null)
            .Select(e => (Entry: e, Vote: votes.GetValueOrDefault(VoteService.StatusKey(e.MediaType, e.TmdbId))))
            .Where(x => x.Vote != -1) // "not for me" titles are not shown again
            .OrderByDescending(x => x.Entry.Score)
            .Select(x => CardMapper.ToCard(x.Entry, x.Vote) with
            {
                Upcoming = wantUpcoming,
                Reminded = reminded.Contains(ReminderKey(x.Entry.MediaType, x.Entry.TmdbId, x.Entry.SeasonNumber)),
            })
            .ToList();
    }

    public static string ReminderKey(string mediaType, int tmdbId, int? season) => $"{mediaType}:{tmdbId}:{season ?? 0}";

    private static HashSet<string> RemindedKeys(StoreData d, Guid userId)
        => d.Reminders.Where(r => r.UserId == userId).Select(r => ReminderKey(r.MediaType, r.TmdbId, r.SeasonNumber)).ToHashSet();
}
