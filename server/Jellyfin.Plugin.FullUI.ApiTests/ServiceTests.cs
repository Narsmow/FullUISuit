using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Events;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Recs;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class CatalogTests
{
    private static (JellyfinCatalog Catalog, Func<int> Queries) Build()
    {
        var queries = 0;
        var library = Stub.Make<ILibraryManager>(("GetItemList", _ =>
        {
            Interlocked.Increment(ref queries);
            return new List<BaseItem>();
        }));
        return (new JellyfinCatalog(library, Stub.Make<IUserManager>(), NullLogger<JellyfinCatalog>.Instance), () => Volatile.Read(ref queries));
    }

    [Fact]
    public void ManyInvalidations_DoNotCauseManyLibraryReloads()
    {
        // B-07: every ItemAdded during a scan used to trigger a full-library reload on the next request.
        var (catalog, queries) = Build();
        _ = catalog.All;
        var afterFirst = queries();
        Assert.True(afterFirst > 0);

        for (var i = 0; i < 500; i++)
        {
            catalog.Invalidate();
            _ = catalog.All;
        }

        Assert.Equal(afterFirst, queries());
    }

    [Fact]
    public void InvalidateNow_ReloadsImmediately()
    {
        var (catalog, queries) = Build();
        _ = catalog.All;
        var before = queries();

        catalog.InvalidateNow();
        _ = catalog.All;

        Assert.True(queries() > before);
    }

    [Fact]
    public void Invalidate_IsCheap_AndNeverBlocksOnALoad()
    {
        var (catalog, _) = Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 100_000; i++)
        {
            catalog.Invalidate();
        }

        Assert.True(sw.ElapsedMilliseconds < 2000, "Invalidate() must be a cheap flag set");
    }

    [Fact]
    public void ChangedIsRaised_OncePerBurst_NotOncePerItem()
    {
        var (catalog, _) = Build();
        var raised = 0;
        catalog.Changed += (_, _) => Interlocked.Increment(ref raised);

        for (var i = 0; i < 1000; i++)
        {
            catalog.Invalidate();
        }

        Assert.Equal(0, Volatile.Read(ref raised)); // debounced: nothing yet
        catalog.InvalidateNow();
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://evilyoutu.be/dQw4w9WgXcQ", null)]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ", null)]
    [InlineData("https://youtube.com.evil.example/watch?v=dQw4w9WgXcQ", null)]
    public void YouTubeKeys_RequireTheRealDomain(string url, string? expected)
        => Assert.Equal(expected, JellyfinCatalog.ParseYouTubeKey(url));

    [Fact]
    public void VisibleTtl_IsShort_SoPermissionChangesShowUpQuickly()
        => Assert.True(JellyfinCatalog.VisibleTtl <= TimeSpan.FromMinutes(2));
}

public class BackfillTests
{
    private static readonly Guid U = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000009");
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private const long Hour = 36_000_000_000L;

    private static WatchRecord Movie(long position, bool played, bool favorite = false, int daysAgo = 3)
        => new(Guid.NewGuid(), null, false, 2 * Hour, position, played, favorite, Now.AddDays(-daysAgo));

    [Fact]
    public void PlayedPausedAndFavouriteMovies_BecomeSignalsAndRatings()
    {
        var done = Movie(0, true);
        var paused = Movie(Hour / 2, false);
        var loved = Movie(0, false, favorite: true);
        var d = new StoreData();

        var added = PlaybackBackfill.Apply(d, U, new[] { done, paused, loved }, Now);

        Assert.Equal(2, added);
        var doneSig = d.Signals.Single(s => s.ItemId == done.ItemId);
        Assert.True(doneSig.Completed);
        Assert.Equal(1.0, doneSig.Completion);
        Assert.Equal(Now.AddDays(-3), doneSig.At);
        var pausedSig = d.Signals.Single(s => s.ItemId == paused.ItemId);
        Assert.False(pausedSig.Completed);
        Assert.Equal(0.25, pausedSig.Completion, 3);
        Assert.Equal(2, d.Ratings[StoreData.UserItemKey(U, loved.ItemId)]);
        Assert.DoesNotContain(d.Signals, s => s.ItemId == loved.ItemId);
        Assert.Contains(U.ToString("N"), d.BackfilledUsers);
    }

    [Fact]
    public void RunsOnlyOncePerUser_AndNeverOverwritesAnExistingRating()
    {
        var m = Movie(0, true, favorite: true);
        var d = new StoreData();
        d.Ratings[StoreData.UserItemKey(U, m.ItemId)] = -1; // the user already thumbed it down in FullUI

        Assert.Equal(1, PlaybackBackfill.Apply(d, U, new[] { m }, Now));
        Assert.Equal(-1, d.Ratings[StoreData.UserItemKey(U, m.ItemId)]);
        Assert.Equal(0, PlaybackBackfill.Apply(d, U, new[] { m }, Now));
        Assert.Single(d.Signals);
    }

    [Fact]
    public void EpisodesRollUpToTheirSeries_AndAreCapped()
    {
        var series = Guid.NewGuid();
        var episodes = Enumerable.Range(0, 40)
            .Select(i => new WatchRecord(Guid.NewGuid(), series, true, Hour / 2, 0, true, false, Now.AddDays(-i)))
            .ToList();
        var d = new StoreData();

        PlaybackBackfill.Apply(d, U, episodes, Now);

        Assert.All(d.Signals, s => Assert.Equal(series, s.ItemId));
        Assert.All(d.Signals, s => Assert.True(s.IsEpisode));
        Assert.Equal(PlaybackBackfill.MaxSignalsPerTitle, d.Signals.Count);
        Assert.Equal(Now, d.Signals.Max(s => s.At).AddDays(0).Add(TimeSpan.Zero));
    }

    [Fact]
    public void ImportedHistory_FillsContinueWatching_InsteadOfAColdStart()
    {
        var catalog = Enumerable.Range(0, 8).Select(i => Make.Item("M" + i, 100 + i)).ToList();
        var paused = catalog[0];
        var d = new StoreData();
        PlaybackBackfill.Apply(d, U, new[] { new WatchRecord(paused.Id, null, false, 2 * Hour, Hour, false, false, Now.AddDays(-1)) }, Now);

        var rows = RecEngine.Build(new RecInput
        {
            UserId = U,
            Catalog = catalog,
            Visible = catalog.Select(c => c.Id).ToHashSet(),
            Signals = d.Signals,
            Now = Now,
        });

        var cont = rows.Single(r => r.Id == "continue");
        Assert.Equal(paused.Id, cont.Items.Single().Item.Id);
        Assert.Equal(0.5, cont.Items.Single().Progress!.Value, 3);
    }
}

public class NextUpAndRowTests
{
    private static readonly Guid U = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CatalogItem Series(string name) => Make.Item(name, null, CatalogKind.Series, new[] { "Drama" });

    private static IReadOnlyList<RecRow> Build(List<CatalogItem> catalog, IEnumerable<PlaySignal> signals, IEnumerable<Guid> nextUp, Dictionary<string, int>? ratings = null)
        => RecEngine.Build(new RecInput
        {
            UserId = U,
            Catalog = catalog,
            Visible = catalog.Select(c => c.Id).ToHashSet(),
            Signals = signals.ToList(),
            Ratings = ratings ?? new Dictionary<string, int>(),
            NextUpSeries = nextUp.ToList(),
            Now = Now,
        });

    [Fact]
    public void SeriesWithAFinishedEpisodeAndANextOne_StaysInContinueWatching()
    {
        // B-20: "last watched episode completed" used to make the series vanish from Continue Watching.
        var show = Series("Show");
        var other = Series("Other");
        var catalog = new List<CatalogItem> { show, other };
        var finished = new PlaySignal { UserId = U, ItemId = show.Id, IsEpisode = true, At = Now.AddDays(-1), Completion = 1, Completed = true };

        var without = Build(catalog, new[] { finished }, Array.Empty<Guid>());
        var with = Build(catalog, new[] { finished }, new[] { show.Id });

        Assert.DoesNotContain(without, r => r.Id == "continue");
        var row = with.Single(r => r.Id == "continue");
        Assert.Equal(show.Id, row.Items.Single().Item.Id);
        Assert.Null(row.Items.Single().Progress);
    }

    [Fact]
    public void NextUp_DoesNotDuplicateAnInProgressSeries_NorShowThumbedDownOrUnknownOnes()
    {
        var show = Series("Show");
        var disliked = Series("Disliked");
        var catalog = new List<CatalogItem> { show, disliked };
        var half = new PlaySignal { UserId = U, ItemId = show.Id, IsEpisode = true, At = Now.AddDays(-1), Completion = 0.5 };

        var rows = Build(catalog, new[] { half }, new[] { show.Id, disliked.Id, Guid.NewGuid() },
            new Dictionary<string, int> { [StoreData.UserItemKey(U, disliked.Id)] = -1 });

        Assert.Equal(new[] { show.Id }, rows.Single(r => r.Id == "continue").Items.Select(i => i.Item.Id).ToArray());
    }

    [Theory]
    [InlineData(7, "This Week")]
    [InlineData(1, "Today")]
    [InlineData(30, "in the Last 30 Days")]
    [InlineData(14, "in the Last 14 Days")]
    public void TopTenTitle_FollowsTheConfiguredWindow(int days, string expected)
        => Assert.Equal(expected, RecEngine.TopTenPeriod(days));

    [Fact]
    public void TopTenRow_TitleSaysWhatTheWindowIs()
    {
        var movies = Enumerable.Range(0, 4).Select(i => Make.Item("M" + i, i + 1)).ToList();
        var other = Guid.NewGuid();
        var signals = movies.SelectMany(m => new[] { U, other }.Select(u => new PlaySignal { UserId = u, ItemId = m.Id, At = Now.AddDays(-2), Completion = 1, Completed = true })).ToList();

        var rows = RecEngine.Build(new RecInput { UserId = Guid.NewGuid(), Catalog = movies, Visible = movies.Select(m => m.Id).ToHashSet(), Signals = signals, Now = Now, ServerName = "Srv", TopTenWindowDays = 30 });

        Assert.Equal("Top 10 Movies on Srv in the Last 30 Days", rows.Single(r => r.Id == "top10-movies").Title);
    }

    [Fact]
    public void ResumeProgress_AppliesTheSameRuleEverywhere()
    {
        PlaySignal Sig(double completion, bool completed = false, int daysAgo = 1) => new() { Completion = completion, Completed = completed, At = Now.AddDays(-daysAgo) };

        Assert.Equal(0.5, RecEngine.ResumeProgress(Sig(0.5), Now));
        Assert.Null(RecEngine.ResumeProgress(Sig(0.01), Now));
        Assert.Null(RecEngine.ResumeProgress(Sig(0.97), Now));
        Assert.Null(RecEngine.ResumeProgress(Sig(0.5, completed: true), Now));
        Assert.Null(RecEngine.ResumeProgress(Sig(0.5, daysAgo: 120), Now));
    }
}

public class RequestMaintenanceTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeUsers _users = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void VoteCap_StopsRunawayAccounts_ButStillAllowsChangesAndClears()
    {
        var votes = new VoteService(_ts.Store);
        var u = Guid.NewGuid();
        for (var i = 1; i <= VoteService.MaxVotesPerUser; i++)
        {
            Assert.Equal(VoteResult.Ok, votes.Cast(u, new(i, "movie", 1, "T" + i, null, null, null, null)));
        }

        Assert.Equal(VoteResult.LimitReached, votes.Cast(u, new(99999, "movie", 1, "One too many", null, null, null, null)));
        Assert.Equal(VoteResult.Ok, votes.Cast(u, new(5, "movie", -1, "Changed my mind", null, null, null, null)));
        Assert.Equal(VoteResult.Ok, votes.Cast(u, new(6, "movie", 0, null, null, null, null, null)));   // clearing frees a slot
        Assert.Equal(VoteResult.Ok, votes.Cast(u, new(99999, "movie", 1, "Now it fits", null, null, null, null)));
        Assert.Equal(VoteResult.Invalid, votes.Cast(u, new(int.MaxValue, "movie", 1, "x", null, null, null, null)));
        Assert.Equal(VoteResult.Ok, votes.Cast(Guid.NewGuid(), new(1, "movie", 1, "Other user is unaffected", null, null, null, null)));
    }

    [Fact]
    public void PurgeDeletedUsers_RemovesOnlyTheirData()
    {
        var keep = Guid.NewGuid();
        var gone = Guid.NewGuid();
        _users.Names[keep] = "keep";
        var item = Guid.NewGuid();
        _ts.Store.Write(d =>
        {
            foreach (var u in new[] { keep, gone })
            {
                d.Votes.Add(new VoteEntry { UserId = u, TmdbId = 1, Vote = 1, At = DateTime.UtcNow });
                d.Notifications.Add(new NotificationEntry { UserId = u, Text = "n", At = DateTime.UtcNow });
                d.Signals.Add(new PlaySignal { UserId = u, ItemId = item, At = DateTime.UtcNow });
                d.Ratings[StoreData.UserItemKey(u, item)] = 1;
                d.MyList.Add(StoreData.UserItemKey(u, item));
                d.ComingSoon[u.ToString("N")] = new() { new ComingSoonEntry { TmdbId = 1 } };
            }
        });

        var purged = new RequestService(_ts.Store, _users, new FakeConfig()).PurgeDeletedUsers();

        Assert.Equal(1, purged);
        _ts.Store.Read(d =>
        {
            Assert.Equal(keep, Assert.Single(d.Votes).UserId);
            Assert.Equal(keep, Assert.Single(d.Notifications).UserId);
            Assert.Equal(keep, Assert.Single(d.Signals).UserId);
            Assert.Single(d.Ratings);
            Assert.Single(d.MyList);
            Assert.Equal(keep.ToString("N"), Assert.Single(d.ComingSoon).Key);
            return 0;
        });
    }

    [Fact]
    public void PurgeDeletedUsers_DoesNothing_WhenTheUserListCouldNotBeRead()
    {
        _ts.Store.Write(d => d.Votes.Add(new VoteEntry { UserId = Guid.NewGuid(), TmdbId = 1, Vote = 1, At = DateTime.UtcNow }));

        Assert.Equal(0, new RequestService(_ts.Store, new FakeUsers(), new FakeConfig()).PurgeDeletedUsers());
        Assert.Single(_ts.Store.Read(d => d.Votes.ToList()));
    }

    [Fact]
    public void Csv_StartsWithAUtf8Bom_SoExcelReadsNonAsciiTitles()
    {
        var controller = new Jellyfin.Plugin.FullUI.Api.AdminController(
            new RequestService(_ts.Store, _users, new FakeConfig()), null!, null!, null!, null!, null!, NullLogger<Jellyfin.Plugin.FullUI.Api.AdminController>.Instance);
        _ts.Store.Write(d => d.Votes.Add(new VoteEntry { UserId = Guid.NewGuid(), TmdbId = 1, MediaType = "movie", Vote = 1, Title = "Amélie", At = DateTime.UtcNow }));

        var file = Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(controller.GetCsv("votes"));

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.FileContents.Take(3).ToArray());
        Assert.Contains("Amélie", System.Text.Encoding.UTF8.GetString(file.FileContents));
    }
}
