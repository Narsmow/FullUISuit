using System;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>Maps catalog items and stored votes to the DTOs clients receive.</summary>
public static class CardMapper
{
    public static ItemCard ToCard(
        CatalogItem item,
        string[] badges,
        int? rank,
        double? progress,
        int myRating,
        bool inMyList,
        StoreData store)
    {
        var trailer = item.TrailerKey;
        if (string.IsNullOrEmpty(trailer))
        {
            store.TrailerKeys.TryGetValue($"item:{item.Id:N}", out trailer);
        }

        return new ItemCard(
            item.Id.ToString("N"),
            item.Name,
            item.Kind == CatalogKind.Movie ? "Movie" : "Series",
            item.Year,
            item.Rating,
            item.OfficialRating,
            item.Overview,
            item.Genres.ToArray(),
            item.RuntimeMinutes,
            badges,
            trailer,
            item.TmdbId,
            progress,
            item.HasBackdrop,
            item.HasLogo,
            myRating,
            inMyList,
            rank);
    }

    public static ComingSoonCard ToCard(ComingSoonEntry e, int myVote) =>
        new(e.TmdbId, e.MediaType, e.Title, e.Overview, e.PosterPath, e.BackdropPath, e.ReleaseDate, e.TrailerKey, myVote);

    public static ComingSoonCard ToCard(VoteEntry v, StoreData store)
    {
        store.TrailerKeys.TryGetValue($"{v.MediaType}:{v.TmdbId}", out var trailer);
        return new ComingSoonCard(v.TmdbId, v.MediaType, v.Title, v.Overview, v.PosterPath, v.BackdropPath, v.ReleaseDate, trailer, v.Vote);
    }
}
