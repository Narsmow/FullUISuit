using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;
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
    private readonly InteractionLog? _interactions;
    private readonly IConfigSource? _config;
    private readonly Ops.ITaskRunLog? _runs;

    /// <summary>Key of this service in the health page's task list.</summary>
    public const string RunKey = "FullUIPlaybackTracking";
    public const string RunName = "Learn from playback";

    public EventTracker(
        ISessionManager sessions,
        ILibraryManager library,
        PluginStore store,
        ICatalog catalog,
        HomeService home,
        ILogger<EventTracker> log,
        InteractionLog? interactions = null,
        IConfigSource? config = null,
        Ops.ITaskRunLog? runs = null)
    {
        _sessions = sessions;
        _library = library;
        _store = store;
        _catalog = catalog;
        _home = home;
        _log = log;
        _interactions = interactions;
        _config = config;
        _runs = runs;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        try
        {
            _sessions.PlaybackStopped += OnPlaybackStopped;
            _library.ItemAdded += OnItemAdded;
            _sessions.PlaybackStart += OnPlaybackStart;
            _runs?.Record(RunKey, RunName, start, DateTime.UtcNow, Ops.TaskOutcome.Success, "Listening for playback and library changes.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: could not subscribe to Jellyfin events; recommendations will not learn from playback");
            _runs?.Record(RunKey, RunName, start, DateTime.UtcNow, Ops.TaskOutcome.Failed, "FullUI could not listen to Jellyfin playback, so recommendations will not learn from watching. Restart Jellyfin; if it persists, check the server log.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessions.PlaybackStopped -= OnPlaybackStopped;
            _library.ItemAdded -= OnItemAdded;
            _sessions.PlaybackStart -= OnPlaybackStart;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: error while unsubscribing events");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a playback stop to the (title id, isEpisode) the engine tracks, or null for other media. Episodes of season 0
    /// (specials: behind-the-scenes, recaps) say nothing about the user's interest in the series and are not recorded.
    /// <c>Completed</c> means "this playback finished"; whether the SERIES is finished is decided by the engine from the
    /// season/episode numbers, never from a single signal.
    /// </summary>
    public static PlaySignal? BuildSignal(Guid userId, Guid itemId, Guid? seriesId, bool isEpisode, long? runtimeTicks, long? positionTicks, bool playedToCompletion, DateTime now, int? season = null, int? episode = null)
    {
        var id = isEpisode ? seriesId : itemId;
        if (id is null || id == Guid.Empty || (isEpisode && season == 0))
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
            Season = isEpisode ? season : null,
            Episode = isEpisode ? episode : null,
        };
    }

    /// <summary>
    /// The title id an interaction event is about: the movie, or the SERIES for an episode (the ids clients use for cards).
    /// Null for anything else (music, live TV, specials, which say nothing about the user's taste).
    /// </summary>
    public static Guid? InteractionTitleId(Guid itemId, Guid? seriesId, bool isEpisode, bool isMovie, int? season)
    {
        if (isEpisode)
        {
            return seriesId is Guid s && s != Guid.Empty && season != 0 ? s : null;
        }

        return isMovie && itemId != Guid.Empty ? itemId : null;
    }

    /// <summary>
    /// Counts every playback start, whatever started it, as a server-side <c>serverPlayStarted</c> usage event. Together with the
    /// web page's own <c>playStarted</c> events (which carry the row they came from) this gives the Metrics page the denominator for
    /// "how many plays did FullUI's rows cause". Respects the admin's "collect usage statistics" switch and never throws.
    /// </summary>
    internal void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (_interactions is null || _config?.Current.CollectInteractionMetrics == false)
            {
                return;
            }

            var item = e.Item;
            var episode = item as Episode;
            var id = InteractionTitleId(item.Id, episode?.SeriesId, episode is not null, item is Movie, episode?.ParentIndexNumber);
            if (id is null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var events = e.Users.Select(u => new StoredEvent(u.Id, EventTypes.ServerPlay, null, id.Value.ToString("N"), null, now)).ToList();
            _interactions.Append(events);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "FullUI: could not record a playback start");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var item = e.Item;
            Guid? seriesId = null;
            var isEpisode = false;
            int? season = null;
            int? episode = null;
            if (item is Episode ep)
            {
                isEpisode = true;
                seriesId = ep.SeriesId;
                season = ep.ParentIndexNumber;
                episode = ep.IndexNumber;
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
                    var sig = BuildSignal(user.Id, item.Id, seriesId, isEpisode, item.RunTimeTicks, e.PlaybackPositionTicks, e.PlayedToCompletion, now, season, episode);
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
            // Placeholders for missing episodes are "added" too; they never change what a user can watch.
            if (e.Item is Movie or Series or Episode && !e.Item.IsVirtualItem)
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
