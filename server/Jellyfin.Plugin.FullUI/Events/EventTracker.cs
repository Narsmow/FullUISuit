using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Events;

/// <summary>Records play signals from playback stops and keeps the catalog fresh when items are added.</summary>
public sealed class EventTracker : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly PluginStore _store;
    private readonly ICatalog _catalog;
    private readonly HomeService _home;
    private readonly ILogger<EventTracker> _log;

    public EventTracker(
        ISessionManager sessions,
        ILibraryManager library,
        PluginStore store,
        ICatalog catalog,
        HomeService home,
        ILogger<EventTracker> log)
    {
        _sessions = sessions;
        _library = library;
        _store = store;
        _catalog = catalog;
        _home = home;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _library.ItemAdded += OnItemAdded;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: could not subscribe to Jellyfin events; recommendations will not learn from playback");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _library.ItemAdded -= OnItemAdded;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: error while unsubscribing events");
        }

        return Task.CompletedTask;
    }

    /// <summary>Maps a playback stop to the (title id, isEpisode) the engine tracks, or null for other media.</summary>
    public static PlaySignal? BuildSignal(Guid userId, Guid itemId, Guid? seriesId, bool isEpisode, long? runtimeTicks, long? positionTicks, bool playedToCompletion, DateTime now)
    {
        var id = isEpisode ? seriesId : itemId;
        if (id is null || id == Guid.Empty)
        {
            return null;
        }

        double completion = runtimeTicks is long rt && rt > 0 && positionTicks is long pos
            ? Math.Clamp((double)pos / rt, 0, 1)
            : (playedToCompletion ? 1 : 0);
        return new PlaySignal
        {
            UserId = userId,
            ItemId = id.Value,
            IsEpisode = isEpisode,
            At = now,
            Completion = completion,
            Completed = playedToCompletion || completion >= 0.9,
        };
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var item = e.Item;
            Guid? seriesId = null;
            var isEpisode = false;
            if (item is Episode ep)
            {
                isEpisode = true;
                seriesId = ep.SeriesId;
            }
            else if (item is not Movie)
            {
                return;
            }

            var now = DateTime.UtcNow;
            foreach (var user in e.Users)
            {
                try
                {
                    var sig = BuildSignal(user.Id, item.Id, seriesId, isEpisode, item.RunTimeTicks, e.PlaybackPositionTicks, e.PlayedToCompletion, now);
                    if (sig is null)
                    {
                        continue;
                    }

                    _store.Write(d => d.Signals.Add(sig));
                    _home.Invalidate(user.Id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "FullUI: failed to record playback for one user");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: failed to record playback signal");
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is Movie or Series or Episode)
            {
                _catalog.Invalidate();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: error handling item-added event");
        }
    }
}
