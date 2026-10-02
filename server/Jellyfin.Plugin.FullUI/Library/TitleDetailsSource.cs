using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Library;

/// <summary>One episode as Jellyfin knows it, for one user. <c>Progress</c> is 0..1 only when partly watched.</summary>
public sealed record EpisodeData(Guid Id, int? Season, int? Number, string Name, string? Overview, int? RuntimeMinutes, double? Progress, bool Played, bool HasImage, DateTime? Aired);

/// <summary>A season with its episodes. <c>Number</c> 0 = specials.</summary>
public sealed record SeasonData(Guid Id, int Number, string Name, IReadOnlyList<EpisodeData> Episodes);

/// <summary>A cast or crew member. <c>Type</c> is Actor, Director or Writer.</summary>
public sealed record PersonData(string Name, string? Role, string Type, Guid? Id, bool HasImage);

/// <summary>A trailer url Jellyfin has stored for the title, already reduced to a YouTube video id.</summary>
public sealed record TrailerData(string Key, string? Name);

/// <summary>Everything the title modal needs from Jellyfin itself (the recommendation parts are added by the service).</summary>
public sealed record TitleData(string? Tagline, IReadOnlyList<SeasonData> Seasons, IReadOnlyList<PersonData> People, IReadOnlyList<TrailerData> Trailers);

/// <summary>"Seasons, episodes, cast and trailers of this title as this user sees them." Isolated so the service never touches Jellyfin types.</summary>
public interface ITitleDetailsSource
{
    /// <summary>The data, or null when the title or user does not exist. Throws when the library cannot be read.</summary>
    TitleData? Load(Guid userId, Guid itemId);
}

/// <summary>Backed by ILibraryManager with user-scoped queries, so library access and parental limits are enforced by Jellyfin.</summary>
public sealed class JellyfinTitleDetailsSource : ITitleDetailsSource
{
    public const int MaxPeople = 20;
    private const int MaxActors = 14;
    private const int MaxDirectors = 3;
    private const int MaxWriters = 3;

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<JellyfinTitleDetailsSource> _log;

    public JellyfinTitleDetailsSource(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogger<JellyfinTitleDetailsSource> log)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _log = log;
    }

    public TitleData? Load(Guid userId, Guid itemId)
    {
        var user = _users.GetUserById(userId);
        var item = _library.GetItemById(itemId);
        if (user is null || item is null)
        {
            return null;
        }

        var seasons = item is Series series ? Seasons(user, series) : Array.Empty<SeasonData>();
        return new TitleData(
            string.IsNullOrWhiteSpace(item.Tagline) ? null : item.Tagline.Trim(),
            seasons,
            SafePeople(item),
            Trailers(item));
    }

    private IReadOnlyList<SeasonData> Seasons(Jellyfin.Database.Implementations.Entities.User user, Series series)
    {
        var seasonItems = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Season },
            AncestorIds = new[] { series.Id },
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Season>().ToList();
        var episodes = _library.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            Recursive = true,
            IsVirtualItem = false,
        }).OfType<Episode>().ToList();

        var seasonByNumber = seasonItems.Where(s => s.IndexNumber is not null).GroupBy(s => s.IndexNumber!.Value).ToDictionary(g => g.Key, g => g.First());
        var result = new List<SeasonData>();
        foreach (var group in episodes.GroupBy(e => e.ParentIndexNumber ?? 1).OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key))
        {
            var number = group.Key;
            seasonByNumber.TryGetValue(number, out var seasonItem);
            var seasonId = seasonItem?.Id ?? group.Select(e => e.SeasonId).FirstOrDefault(i => i != Guid.Empty);
            var name = number == 0 ? "Specials" : string.IsNullOrWhiteSpace(seasonItem?.Name) ? $"Season {number}" : seasonItem!.Name;
            var list = group
                .OrderBy(e => e.IndexNumber ?? int.MaxValue)
                .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => ToEpisode(user, e, number))
                .ToList();
            result.Add(new SeasonData(seasonId == Guid.Empty ? series.Id : seasonId, number, name, list));
        }

        return result;
    }

    private EpisodeData ToEpisode(Jellyfin.Database.Implementations.Entities.User user, Episode e, int season)
    {
        var played = false;
        double? progress = null;
        try
        {
            var data = _userData.GetUserData(user, e);
            if (data is not null)
            {
                played = data.Played;
                if (!played && data.PlaybackPositionTicks > 0 && e.RunTimeTicks is long total && total > 0)
                {
                    progress = Math.Round(Math.Clamp((double)data.PlaybackPositionTicks / total, 0.01, 0.99), 3);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FullUI: watch state unavailable for episode {Item}", e.Id);
        }

        return new EpisodeData(
            e.Id,
            season,
            e.IndexNumber,
            string.IsNullOrWhiteSpace(e.Name) ? (e.IndexNumber is int n ? $"Episode {n}" : "Episode") : e.Name,
            string.IsNullOrWhiteSpace(e.Overview) ? null : e.Overview,
            e.RunTimeTicks is long t && t > 0 ? (int)Math.Max(1, t / TimeSpan.TicksPerMinute) : null,
            progress,
            played,
            SafeHasImage(e),
            e.PremiereDate);
    }

    private static bool SafeHasImage(BaseItem item)
    {
        try
        {
            return item.HasImage(ImageType.Primary);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Actors first (billing order), then directors and writers; at most 20. A people failure never fails the page.</summary>
    private IReadOnlyList<PersonData> SafePeople(BaseItem item)
    {
        try
        {
            var all = _library.GetPeople(item);
            var picked = new List<(string Name, string? Role, string Type)>();
            void Take(PersonKind kind, string label, int max)
            {
                foreach (var p in all.Where(p => p.Type == kind && !string.IsNullOrWhiteSpace(p.Name)).Take(max))
                {
                    if (picked.Count < MaxPeople && !picked.Any(x => x.Type == label && string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        picked.Add((p.Name, string.IsNullOrWhiteSpace(p.Role) ? null : p.Role, label));
                    }
                }
            }

            Take(PersonKind.Actor, "Actor", MaxActors);
            Take(PersonKind.Director, "Director", MaxDirectors);
            Take(PersonKind.Writer, "Writer", MaxWriters);
            var result = new List<PersonData>(picked.Count);
            var lookupFailures = 0;
            foreach (var (name, role, type) in picked)
            {
                Guid? id = null;
                var hasImage = false;
                if (lookupFailures < 3)
                {
                    try
                    {
                        var person = _library.GetPerson(name);
                        if (person is not null)
                        {
                            id = person.Id;
                            hasImage = SafeHasImage(person);
                        }
                    }
                    catch (Exception ex)
                    {
                        lookupFailures++;
                        _log.LogDebug(ex, "FullUI: could not look up person {Name}", name);
                    }
                }

                result.Add(new PersonData(name, role, type, id, hasImage));
            }

            return result;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FullUI: could not read people for {Item}", item.Id);
            return Array.Empty<PersonData>();
        }
    }

    private static IReadOnlyList<TrailerData> Trailers(BaseItem item)
    {
        try
        {
            return (item.RemoteTrailers ?? Array.Empty<MediaBrowser.Model.Entities.MediaUrl>())
                .Select(r => (Key: JellyfinCatalog.ParseYouTubeKey(r.Url), r.Name))
                .Where(t => t.Key is not null)
                .Select(t => new TrailerData(t.Key!, string.IsNullOrWhiteSpace(t.Name) ? null : t.Name))
                .ToList();
        }
        catch (Exception)
        {
            return Array.Empty<TrailerData>();
        }
    }
}
