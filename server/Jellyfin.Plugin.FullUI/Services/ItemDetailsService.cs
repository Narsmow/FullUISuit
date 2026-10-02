using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>
/// Builds the title modal (<c>GET Item/{id}/Details</c>): the card, seasons and episodes with this user's progress, cast and crew,
/// trailers, the next episode and "More like this". Returns null when the title does not exist or the user may not see it.
/// Throws when the library cannot be read; the API turns that into a friendly 5xx. Nothing is cached here and no network call is
/// made: trailers come from what Jellyfin and the nightly TMDB task already stored.
/// </summary>
public sealed class ItemDetailsService
{
    public const int SimilarCount = 12;
    public const int MaxTrailers = 5;

    private readonly HomeService _home;
    private readonly ITitleDetailsSource _source;
    private readonly PluginStore _store;
    private readonly IConfigSource _config;
    private readonly ILogger<ItemDetailsService> _log;

    public ItemDetailsService(HomeService home, ITitleDetailsSource source, PluginStore store, IConfigSource config, ILogger<ItemDetailsService> log)
    {
        _home = home;
        _source = source;
        _store = store;
        _config = config;
        _log = log;
    }

    public ItemDetails? Get(Guid userId, Guid itemId)
    {
        var card = _home.GetItemStrict(userId, itemId);
        if (card is null)
        {
            return null;
        }

        var data = _source.Load(userId, itemId);
        if (data is null)
        {
            return null;
        }

        var isSeries = string.Equals(card.Type, "Series", StringComparison.Ordinal);
        var seasons = isSeries ? Seasons(data.Seasons) : new List<SeasonDto>();
        return new ItemDetails(
            card,
            data.Tagline,
            seasons,
            isSeries ? NextUp(userId, itemId, data.Seasons) : null,
            data.People.Take(JellyfinTitleDetailsSource.MaxPeople)
                .Select(p => new PersonDto(p.Name, p.Role, p.Type, p.Id?.ToString("N"), p.HasImage && p.Id is not null)).ToList(),
            Trailers(itemId, card, data.Trailers),
            Similar(userId, itemId));
    }

    private static List<SeasonDto> Seasons(IReadOnlyList<SeasonData> seasons)
        => seasons
            .OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number)
            .Select(s =>
            {
                var episodes = s.Episodes
                    .OrderBy(e => e.Number ?? int.MaxValue)
                    .ThenBy(e => e.Aired ?? DateTime.MaxValue)
                    .Select(e => new EpisodeDto(
                        e.Id.ToString("N"),
                        e.Number,
                        e.Name,
                        e.Overview,
                        e.RuntimeMinutes,
                        e.Played ? null : e.Progress,
                        e.Played,
                        e.HasImage,
                        e.Aired?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                    .ToList();
                var name = string.IsNullOrWhiteSpace(s.Name) ? (s.Number == 0 ? "Specials" : $"Season {s.Number}") : s.Name;
                return new SeasonDto(s.Id.ToString("N"), s.Number, name, episodes.Count, episodes.Count(e => e.Played), episodes);
            })
            .ToList();

    /// <summary>
    /// The episode to play next: the one Jellyfin's Next Up names; else the episode the user has partly watched; else the first
    /// unwatched regular episode. Null when everything is watched or the show has no episodes.
    /// </summary>
    private NextUpDto? NextUp(Guid userId, Guid seriesId, IReadOnlyList<SeasonData> seasons)
    {
        var regular = seasons.Where(s => s.Number > 0).OrderBy(s => s.Number)
            .SelectMany(s => s.Episodes.OrderBy(e => e.Number ?? int.MaxValue)).ToList();
        EpisodeData? pick = null;
        var entry = _home.NextUpFor(userId, seriesId);
        if (entry is not null)
        {
            pick = regular.FirstOrDefault(e => !e.Played && e.Season == entry.Season && e.Number == entry.Episode);
        }

        pick ??= regular.FirstOrDefault(e => !e.Played && e.Progress is > 0);
        pick ??= regular.FirstOrDefault(e => !e.Played);
        return pick is null ? null : new NextUpDto(pick.Id.ToString("N"), pick.Season, pick.Number, pick.Name, pick.Progress);
    }

    /// <summary>Jellyfin's own trailer links first, then the library snapshot, then keys the nightly TMDB task cached (Trailer/Teaser clips only).</summary>
    private List<TrailerDto> Trailers(Guid itemId, ItemCard card, IReadOnlyList<TrailerData> own)
    {
        var result = new List<TrailerDto>();
        void Add(string? key, string? name)
        {
            if (!string.IsNullOrWhiteSpace(key) && result.Count < MaxTrailers && !result.Any(t => t.Key == key))
            {
                result.Add(new TrailerDto(key, string.IsNullOrWhiteSpace(name) ? "Trailer" : name));
            }
        }

        foreach (var t in own)
        {
            Add(t.Key, t.Name);
        }

        Add(card.TrailerKey, null);
        if (!string.IsNullOrWhiteSpace(_config.Current.TmdbApiKey))
        {
            var mediaType = string.Equals(card.Type, "Series", StringComparison.Ordinal) ? "tv" : "movie";
            _store.Read(d =>
            {
                Add(d.TrailerKeys.GetValueOrDefault($"item:{itemId:N}"), null);
                if (card.TmdbId is int tmdb)
                {
                    Add(d.TrailerKeys.GetValueOrDefault($"{mediaType}:{tmdb}"), null);
                }

                return 0;
            });
        }

        return result;
    }

    private IReadOnlyList<ItemCard> Similar(Guid userId, Guid itemId)
    {
        try
        {
            return _home.Similar(userId, itemId, SimilarCount);
        }
        catch (Exception ex)
        {
            // "More like this" is a bonus: the rest of the page still opens.
            _log.LogWarning(ex, "FullUI: similar titles unavailable for {Item}", itemId);
            return Array.Empty<ItemCard>();
        }
    }
}
