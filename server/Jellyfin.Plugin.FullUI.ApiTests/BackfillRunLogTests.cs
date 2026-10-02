using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Ops;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>The playback tracker and the watch-history import report their runs to the health page through <see cref="ITaskRunLog"/>.</summary>
public class BackfillRunLogTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly TaskRunLog _runs;

    public BackfillRunLogTests()
    {
        _runs = new TaskRunLog(_ts.Store);
    }

    public void Dispose() => _ts.Dispose();

    private static User UserOf(Guid id) => new("u" + id.ToString("N")[..4], "default", "default") { Id = id };

    private PlaybackBackfillService Backfill(IUserManager users, ILibraryManager? library = null)
        => new(
            library ?? Stub.Make<ILibraryManager>(("GetItemList", _ => new List<BaseItem>())),
            users,
            Stub.Make<IUserDataManager>(),
            _ts.Store,
            new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, new FakeConfig()),
            NullLogger<PlaybackBackfillService>.Instance,
            _runs);

    private static IUserManager UsersWith(params Guid[] ids)
        => Stub.Make<IUserManager>(
            ("GetUsersIds", _ => ids.AsEnumerable()),
            ("get_UsersIds", _ => ids.AsEnumerable()),
            ("GetUserById", a => ids.Contains((Guid)a![0]!) ? UserOf((Guid)a[0]!) : null));

    private TaskRunRecord Last(string key) => _runs.Latest().Single(r => r.Key == key);

    [Fact]
    public void The_import_records_that_there_was_nothing_to_do()
    {
        Backfill(UsersWith()).RunOnce();

        var run = Last(PlaybackBackfillService.RunKey);
        Assert.Equal(TaskOutcome.Success, run.Outcome);
        Assert.Equal("Nothing new to import.", run.Message);
        Assert.True(run.EndUtc >= run.StartUtc);
    }

    [Fact]
    public void The_import_records_how_many_users_were_imported()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Backfill(UsersWith(a, b)).RunOnce();

        var run = Last(PlaybackBackfillService.RunKey);
        Assert.Equal(TaskOutcome.Success, run.Outcome);
        Assert.Contains("2 user(s)", run.Message);
        Assert.True(_ts.Store.Read(d => d.BackfilledUsers.Contains(a.ToString("N"))));
    }

    [Fact]
    public void A_user_that_cannot_be_imported_is_a_problem_not_a_failure_and_is_retried_later()
    {
        var known = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var users = Stub.Make<IUserManager>(
            ("GetUsersIds", _ => new[] { known, missing }.AsEnumerable()),
            ("get_UsersIds", _ => new[] { known, missing }.AsEnumerable()),
            ("GetUserById", a => (Guid)a![0]! == known ? UserOf(known) : null));

        Backfill(users).RunOnce();

        var run = Last(PlaybackBackfillService.RunKey);
        Assert.Equal(TaskOutcome.Problem, run.Outcome);
        Assert.StartsWith("1 of 2 user(s)", run.Message);
        Assert.False(_ts.Store.Read(d => d.BackfilledUsers.Contains(missing.ToString("N"))));
    }

    [Fact]
    public void A_whole_import_that_throws_is_recorded_as_failed_with_a_friendly_message()
    {
        var users = Stub.Make<IUserManager>(("GetUsersIds", _ => throw new InvalidOperationException("secret failure at C:\\jellyfin")), ("get_UsersIds", _ => throw new InvalidOperationException("secret failure at C:\\jellyfin")));

        Backfill(users).RunOnce();

        var run = Last(PlaybackBackfillService.RunKey);
        Assert.Equal(TaskOutcome.Failed, run.Outcome);
        Assert.DoesNotContain("secret", run.Message);
    }

    [Fact]
    public async Task The_playback_tracker_records_that_it_is_listening()
    {
        var tracker = Tracker(Stub.Make<ISessionManager>());
        await tracker.StartAsync(default);

        var run = Last(EventTracker.RunKey);
        Assert.Equal(TaskOutcome.Success, run.Outcome);
        Assert.Contains("Listening", run.Message);
    }

    [Fact]
    public async Task The_playback_tracker_records_a_failed_start_without_throwing()
    {
        var sessions = Stub.Make<ISessionManager>(("add_PlaybackStopped", _ => throw new InvalidOperationException("boom at C:\\x")));
        var tracker = Tracker(sessions);

        await tracker.StartAsync(default);

        var run = Last(EventTracker.RunKey);
        Assert.Equal(TaskOutcome.Failed, run.Outcome);
        Assert.DoesNotContain("boom", run.Message);
    }

    [Fact]
    public void The_health_page_lists_both_even_before_they_have_run()
    {
        var keys = HealthService.KnownTasks.Select(t => t.Key).ToList();
        Assert.Contains(EventTracker.RunKey, keys);
        Assert.Contains(PlaybackBackfillService.RunKey, keys);
    }

    private EventTracker Tracker(ISessionManager sessions)
        => new(
            sessions,
            Stub.Make<ILibraryManager>(),
            _ts.Store,
            _catalog,
            new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, new FakeConfig()),
            NullLogger<EventTracker>.Instance,
            null,
            new FakeConfig(),
            _runs);
}
