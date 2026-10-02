using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>"Which series has a next episode ready for this user?" Isolated so the engine and tests never touch Jellyfin types.</summary>
public interface INextUpSource
{
    /// <summary>Series ids, most relevant first. Empty on any failure.</summary>
    IReadOnlyList<Guid> NextUpSeries(Guid userId);
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

    public IReadOnlyList<Guid> NextUpSeries(Guid userId)
    {
        try
        {
            var user = _users.GetUserById(userId);
            if (user is null)
            {
                return Array.Empty<Guid>();
            }

            var result = _tv.GetNextUp(
                new NextUpQuery { User = user, Limit = 30, EnableTotalRecordCount = false },
                new DtoOptions(false));
            return result.Items.OfType<Episode>()
                .Select(e => e.SeriesId)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: could not read Next Up for {User}", userId);
            return Array.Empty<Guid>();
        }
    }
}
