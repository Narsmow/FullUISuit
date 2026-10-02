using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Metrics;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>The server records every playback start as <c>serverPlayStarted</c>, so the Metrics page has plays-from-rows over all plays.</summary>
public class PlayStartedTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempStore _ts = new();
    private readonly FakeConfig _config = new() { Current = new PluginConfiguration() };
    private readonly InteractionLog _log;
    private readonly EventTracker _tracker;

    public PlayStartedTests()
    {
        _log = new InteractionLog(_ts.Store, NullLogger<InteractionLog>.Instance);
        var catalog = new FakeCatalog();
        _tracker = new EventTracker(
            Stub.Make<ISessionManager>(),
            Stub.Make<ILibraryManager>(),
            _ts.Store,
            catalog,
            new HomeService(_ts.Store, catalog, NullLogger<HomeService>.Instance, _config),
            NullLogger<EventTracker>.Instance,
            _log,
            _config);
    }

    public void Dispose() => _ts.Dispose();

    private static User UserOf(Guid id) => new("name-" + id.ToString("N")[..4], "default", "default") { Id = id };

    private static PlaybackProgressEventArgs Args(MediaBrowser.Controller.Entities.BaseItem item, params Guid[] users)
        => new() { Item = item, Users = users.Select(UserOf).ToList() };

    private static readonly Guid Ann = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bob = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    [Fact]
    public void A_movie_start_is_recorded_once_per_user_with_the_movie_id()
    {
        var movie = new Movie { Id = Guid.NewGuid() };

        _tracker.OnPlaybackStart(this, Args(movie, Ann, Bob));

        var events = _log.ReadAll();
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(EventTypes.ServerPlay, e.Type);
            Assert.Equal(movie.Id.ToString("N"), e.ItemId);
            Assert.Null(e.RowType);
        });
        Assert.Equal(new[] { Ann, Bob }, events.Select(e => e.UserId).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void An_episode_start_is_recorded_against_the_series_and_specials_are_not_recorded()
    {
        var series = Guid.NewGuid();
        _tracker.OnPlaybackStart(this, Args(new Episode { Id = Guid.NewGuid(), SeriesId = series, ParentIndexNumber = 2, IndexNumber = 3 }, Ann));
        _tracker.OnPlaybackStart(this, Args(new Episode { Id = Guid.NewGuid(), SeriesId = series, ParentIndexNumber = 0, IndexNumber = 1 }, Ann));

        var only = Assert.Single(_log.ReadAll());
        Assert.Equal(series.ToString("N"), only.ItemId);
    }

    [Fact]
    public void Nothing_is_recorded_when_the_admin_switched_collection_off_or_for_other_media()
    {
        _tracker.OnPlaybackStart(this, Args(new MediaBrowser.Controller.Entities.Audio.Audio { Id = Guid.NewGuid() }, Ann));
        Assert.Empty(_log.ReadAll());

        _config.Current = new PluginConfiguration { CollectInteractionMetrics = false };
        _tracker.OnPlaybackStart(this, Args(new Movie { Id = Guid.NewGuid() }, Ann));
        Assert.Empty(_log.ReadAll());
    }

    [Fact]
    public void A_broken_event_never_throws_into_Jellyfin()
    {
        _tracker.OnPlaybackStart(this, new PlaybackProgressEventArgs());   // no item, no users
    }

    [Fact]
    public void A_client_can_not_send_the_servers_own_event_type()
    {
        Assert.Null(EventValidator.Validate(Ann, new ClientEvent(EventTypes.ServerPlay, null, null, null, null), Now));
        Assert.NotNull(EventValidator.Validate(Ann, new ClientEvent("playStarted", "toppicks", null, null, null), Now));
    }

    [Fact]
    public void Metrics_take_all_plays_from_the_server_and_plays_from_rows_from_the_page()
    {
        var movie = Guid.NewGuid().ToString("N");
        var events = new List<StoredEvent>
        {
            new(Ann, "rowShown", "toppicks", null, null, Now),
            new(Ann, "rowShown", "toppicks", null, null, Now),
            new(Ann, "cardClicked", "toppicks", movie, null, Now),
            new(Ann, "playStarted", "toppicks", movie, null, Now),              // the page: this play came from a row
            new(Ann, EventTypes.ServerPlay, null, movie, null, Now),            // the server: the same play...
            new(Bob, EventTypes.ServerPlay, null, movie, null, Now),            // ...and one started from the stock screen
            new(Bob, EventTypes.ServerPlay, null, movie, null, Now),
        };
        _log.Append(events);

        var report = new MetricsService(_log, _config).Report(Now);

        Assert.Equal(3, report.PlaysTotal);        // server count, not 1 + 3
        Assert.Equal(1, report.PlaysFromRows);
        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.Plays);
        Assert.Equal(1.0, row.TakeRate);           // (1 click + 1 play) / 2 shown
    }

    [Fact]
    public void Metrics_fall_back_to_the_pages_plays_when_the_server_has_none_yet()
    {
        _log.Append(new[] { new StoredEvent(Ann, "playStarted", "toppicks", null, null, Now), new StoredEvent(Ann, "playStarted", null, null, null, Now) });

        Assert.Equal(2, new MetricsService(_log, _config).Report(Now).PlaysTotal);
    }

    [Fact]
    public void The_server_event_survives_a_round_trip_through_the_log_file()
    {
        _log.Append(new[] { new StoredEvent(Ann, EventTypes.ServerPlay, null, "abc", null, Now) });

        var again = new InteractionLog(_ts.Store, NullLogger<InteractionLog>.Instance);
        Assert.Equal(EventTypes.ServerPlay, again.ReadAll().Single().Type);
    }
}
