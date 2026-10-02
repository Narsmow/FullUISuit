using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// The single definition of "which Coming Soon cards does this user see". Used by both the Home row and
/// <c>GET /FullUI/ComingSoon</c> so they can never disagree: titles already in the library are dropped and
/// titles the user voted "not for me" stay hidden.
/// </summary>
public static class ComingSoonView
{
    public static ISet<string> LibraryKeys(IEnumerable<CatalogItem> catalog)
        => catalog.Where(i => i.TmdbId is > 0)
            .Select(i => ComingSoonRanker.Key(ComingSoonRanker.MediaTypeOf(i), i.TmdbId!.Value))
            .ToHashSet();

    /// <summary>Call inside <c>PluginStore.Read</c>. Best score first.</summary>
    public static List<ComingSoonCard> Cards(StoreData d, Guid userId, ISet<string> libraryKeys)
    {
        if (!d.ComingSoon.TryGetValue(userId.ToString("N"), out var entries))
        {
            return new List<ComingSoonCard>();
        }

        var votes = d.Votes.Where(v => v.UserId == userId)
            .GroupBy(v => VoteService.StatusKey(v.MediaType, v.TmdbId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.At).First().Vote);
        return entries
            .Where(e => !libraryKeys.Contains(ComingSoonRanker.Key(e.MediaType, e.TmdbId)))
            .Select(e => (Entry: e, Vote: votes.GetValueOrDefault(VoteService.StatusKey(e.MediaType, e.TmdbId))))
            .Where(x => x.Vote != -1) // "not for me" titles are not shown again
            .OrderByDescending(x => x.Entry.Score)
            .Select(x => CardMapper.ToCard(x.Entry, x.Vote))
            .ToList();
    }
}
