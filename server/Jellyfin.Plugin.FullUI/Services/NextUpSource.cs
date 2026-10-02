using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FullUI.Recs;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>The next episode Jellyfin has ready for a series.</summary>
public sealed record NextUpEntry(Guid SeriesId, int? Season, int? Episode);

/// <summary>"Which series has a next episode ready for this user?" Isolated so the engine and tests never touch Jellyfin types.</summary>
public interface INextUpSource
{
    /// <summary>Series ids, most relevant first. Empty on any failure.</summary>
    IReadOnlyList<Guid> NextUpSeries(Guid userId);

    /// <summary>Like <see cref="NextUpSeries"/> but with the episode numbers (for the "S2:E5" label). Sources that do not know them may leave them null.</summary>
    IReadOnlyList<NextUpEntry> NextUpEntries(Guid userId) =>
        NextUpSeries(userId).Select(id => new NextUpEntry(id, null, null)).ToList();
}

/// <summary>Backed by Jellyfin's own Next Up logic (the same list the stock home screen shows).</summary>
public sealed class JellyfinNextUpSource : INextUpSource
{
    private readonly ITVSeriesManager _tv;
    private readonly IUserManager _users;
    private readonly ILogger<JellyfinNextUpSource> _log;

    public JellyfinNextUpSource(ITVSeriesManager tv, IUserManager users, ILogger<JellyfinNextUpSource> log)
    {
        _tv = tv;
        _users = users;
        _log = log;
    }

    public IReadOnlyList<Guid> NextUpSeries(Guid userId) => NextUpEntries(userId).Select(e => e.SeriesId).ToList();

    public IReadOnlyList<NextUpEntry> NextUpEntries(Guid userId)
    {
        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return Array.Empty<NextUpEntry>();
            }

            var result = _tv.GetNextUp(
                new NextUpQuery { User = user, Limit = 30, EnableTotalRecordCount = false },
                new DtoOptions(false));
            return result.Items.OfType<Episode>()
                .Where(e => e.SeriesId != Guid.Empty)
                .GroupBy(e => e.SeriesId)
                .Select(g => g.First())
                .Select(e => new NextUpEntry(e.SeriesId, e.ParentIndexNumber, e.IndexNumber))
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: could not read Next Up for {User}", userId);
            return Array.Empty<NextUpEntry>();
        }
    }
}

/// <summary>What Jellyfin knows about which episodes a user has watched (more complete than the signals FullUI stored itself).</summary>
public interface IWatchStateSource
{
    /// <summary>Watched episodes per series for the user (season 0 specials excluded). Empty on any failure.</summary>
    IReadOnlyDictionary<Guid, SeriesWatchInfo> SeriesWatch(Guid userId);

    /// <summary>Forget what was cached for the user (they just finished playing something).</summary>
    void Invalidate(Guid userId);
}

/// <summary>Reads the user's played episodes from the library (one query per user, cached for a minute).</summary>
public sealed class JellyfinWatchStateSource : IWatchStateSource
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<JellyfinWatchStateSource> _log;
    private readonly ConcurrentDictionary<Guid, (DateTime At, IReadOnlyDictionary<Guid, SeriesWatchInfo> Info)> _cache = new();

    public JellyfinWatchStateSource(ILibraryManager library, IUserManager users, ILogger<JellyfinWatchStateSource> log)
    {
        _library = library;
        _users = users;
        _log = log;
    }

    public void Invalidate(Guid userId) => _cache.TryRemove(userId, out _);

    public IReadOnlyDictionary<Guid, SeriesWatchInfo> SeriesWatch(Guid userId)
    {
        if (_cache.TryGetValue(userId, out var hit) && DateTime.UtcNow - hit.At < Ttl)
        {
            return hit.Info;
        }

        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return new Dictionary<Guid, SeriesWatchInfo>();
            }

            var played = _library.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsPlayed = true,
                IsVirtualItem = false,
                DtoOptions = new DtoOptions(false),
            });
            var info = played.OfType<Episode>()
                .Where(e => e.SeriesId != Guid.Empty && e.ParentIndexNumber != 0)
                .GroupBy(e => e.SeriesId)
                .ToDictionary(
                    g => g.Key,
                    g => new SeriesWatchInfo(g.GroupBy(e => e.ParentIndexNumber is int s && s > 0 ? s : 1)
                        .ToDictionary(sg => sg.Key, sg => sg.Count())));
            IReadOnlyDictionary<Guid, SeriesWatchInfo> result = info;
            _cache[userId] = (DateTime.UtcNow, result);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: could not read watched episodes for {User}", userId);
            return new Dictionary<Guid, SeriesWatchInfo>();
        }
    }
}
