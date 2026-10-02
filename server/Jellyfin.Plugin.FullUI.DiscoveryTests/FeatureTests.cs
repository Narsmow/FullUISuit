using System.Text.Json;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class ReminderTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new() { Current = new() { TmdbApiKey = "k", ServerName = "Home Cinema" } };
    private readonly Guid _ann = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public void Dispose() => _ts.Dispose();

    private ReminderService Svc() => new(_ts.Store, _catalog, _config, NullLogger<ReminderService>.Instance);

    private void Soon(Guid user, int id, string title, string date, string type = "movie", int? season = null)
        => _ts.Store.Write(d =>
        {
            var k = user.ToString("N");
            if (!d.ComingSoon.TryGetValue(k, out var l))
            {
                d.ComingSoon[k] = l = new();
            }

            l.Add(new ComingSoonEntry { TmdbId = id, MediaType = type, Title = title, PosterPath = "/p.jpg", ReleaseDate = date, Upcoming = true, SeasonNumber = season });
        });

    [Fact]
    public void Remind_needs_a_title_from_the_users_own_list_and_is_private_to_them()
    {
        Soon(_ann, 1, "Dune 3", "2026-12-18");
        var svc = Svc();
        Assert.Equal(RemindResult.Ok, svc.Set(_ann, 1, "movie", true, Now));
        Assert.Equal(RemindResult.UnknownTitle, svc.Set(_ann, 999, "movie", true, Now));
        Assert.Equal(RemindResult.UnknownTitle, svc.Set(_bob, 1, "movie", true, Now)); // bob's list does not have it
        Assert.Equal(RemindResult.UnknownTitle, svc.Set(_ann, 1, "podcast", true, Now));

        var mine = svc.List(_ann);
        Assert.Equal("Dune 3", Assert.Single(mine).Title);
        Assert.Empty(svc.List(_bob));

        Assert.Equal(RemindResult.Ok, svc.Set(_ann, 1, "movie", true, Now)); // idempotent
        Assert.Single(svc.List(_ann));
        svc.Set(_ann, 1, "movie", false, Now);
        Assert.Empty(svc.List(_ann));
    }

    [Fact]
    public void Release_day_creates_an_out_today_notification_once()
    {
        Soon(_ann, 1, "Dune 3", "2026-10-02");
        var svc = Svc();
        svc.Set(_ann, 1, "movie", true, Now.AddDays(-5));
        Assert.Equal(1, svc.Process(Now));
        Assert.Equal(0, svc.Process(Now.AddHours(1))); // not again

        var n = Assert.Single(_ts.Store.Read(d => d.Notifications.Where(x => x.UserId == _ann).ToList()));
        Assert.Equal("Dune 3 is out today", n.Text);
        Assert.True(svc.List(_ann)[0].Notified);
        Assert.Empty(_ts.Store.Read(d => d.Notifications.Where(x => x.UserId == _bob).ToList()));
    }

    [Fact]
    public void Before_release_day_nothing_is_sent()
    {
        Soon(_ann, 1, "Dune 3", "2026-12-18");
        var svc = Svc();
        svc.Set(_ann, 1, "movie", true, Now);
        Assert.Equal(0, svc.Process(Now));
        Assert.Empty(_ts.Store.Read(d => d.Notifications));
    }

    [Fact]
    public void A_title_that_appears_in_the_library_says_now_on_the_server()
    {
        Soon(_ann, 1, "Dune 3", "2026-12-18");
        var svc = Svc();
        svc.Set(_ann, 1, "movie", true, Now);
        var item = Make.Item("Dune 3", 1);
        _catalog.Items.Add(item);

        Assert.Equal(1, svc.Process(Now));
        var n = Assert.Single(_ts.Store.Read(d => d.Notifications.ToList()));
        Assert.Equal("Dune 3 is now on Home Cinema", n.Text);
        Assert.Equal(item.Id, n.ItemId);
    }

    [Fact]
    public void A_library_title_the_user_cannot_see_does_not_trigger_the_notification()
    {
        Soon(_ann, 1, "Dune 3", "2026-12-18");
        var svc = Svc();
        svc.Set(_ann, 1, "movie", true, Now);
        _catalog.Items.Add(Make.Item("Dune 3", 1));
        _catalog.Visible = new HashSet<Guid>();
        Assert.Equal(0, svc.Process(Now));
    }

    [Fact]
    public void A_new_season_reminder_fires_on_its_day_not_because_the_show_is_already_in_the_library()
    {
        Soon(_ann, 7, "Show: Season 3", "2026-11-20", "tv", season: 3);
        _catalog.Items.Add(Make.Item("Show", 7, CatalogKind.Series));
        var svc = Svc();
        svc.Set(_ann, 7, "tv", true, Now);
        Assert.Equal(0, svc.Process(Now));
        Assert.Equal(1, svc.Process(new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void One_users_failure_does_not_stop_the_others()
    {
        Soon(_ann, 1, "A", "2026-10-02");
        Soon(_bob, 2, "B", "2026-10-02");
        var svc = Svc();
        svc.Set(_ann, 1, "movie", true, Now);
        svc.Set(_bob, 2, "movie", true, Now);
        _catalog.VisibleThrowsFor = _ann;
        _catalog.Items.Add(Make.Item("A", 1));
        _catalog.Items.Add(Make.Item("B", 2));
        Assert.Equal(1, svc.Process(Now)); // bob still got his
        Assert.Contains(_ts.Store.Read(d => d.Notifications.Select(n => n.UserId).ToList()), u => u == _bob);
    }
}

public class NewPopularTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new() { Current = new() { TmdbApiKey = "k" } };
    private readonly FakeUsers _users = new();

    public void Dispose() => _ts.Dispose();

    private sealed class Fixed : ITrendingProvider
    {
        public List<Guid> Watching { get; } = new();

        public List<Guid> Movies { get; } = new();

        public List<Guid> Shows { get; } = new();

        public IReadOnlyList<Guid> Trending(Guid userId, int take) => Watching;

        public IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind) => kind == CatalogKind.Movie ? Movies : Shows;
    }

    [Fact]
    public void Coming_soon_is_sorted_by_date_and_other_lists_come_from_the_provider_filtered_to_visible()
    {
        var u = Guid.NewGuid();
        var a = Make.Item("A", 1);
        var b = Make.Item("B", 2, CatalogKind.Series);
        var hidden = Make.Item("Hidden", 3);
        _catalog.Items.AddRange(new[] { a, b, hidden });
        _catalog.Visible = new HashSet<Guid> { a.Id, b.Id };
        _ts.Store.Write(d => d.ComingSoon[u.ToString("N")] = new()
        {
            new ComingSoonEntry { TmdbId = 11, Title = "Late", PosterPath = "/p", ReleaseDate = "2026-12-24", Score = 9 },
            new ComingSoonEntry { TmdbId = 12, Title = "Early", PosterPath = "/p", ReleaseDate = "2026-10-05", Score = 1 },
            new ComingSoonEntry { TmdbId = 13, Title = "Old", PosterPath = "/p", ReleaseDate = "2026-09-01", Score = 5 },
        });
        var trending = new Fixed();
        trending.Watching.AddRange(new[] { a.Id, hidden.Id });
        trending.Movies.AddRange(new[] { hidden.Id, a.Id });
        trending.Shows.Add(b.Id);

        var r = new NewPopularService(_ts.Store, _catalog, trending).Build(u, tmdbConfigured: true, Now);

        Assert.Equal(new[] { "Early", "Late" }, r.ComingSoon.Select(c => c.Title).ToArray());
        Assert.Equal(new[] { a.Name }, r.EveryonesWatching.Select(c => c.Name).ToArray());
        var movie = Assert.Single(r.Top10Movies);
        Assert.Equal(1, movie.Rank);
        Assert.Equal(b.Name, Assert.Single(r.Top10Shows).Name);
    }

    [Fact]
    public void Without_a_tmdb_key_coming_soon_is_empty_and_a_broken_provider_does_not_break_the_page()
    {
        var u = Guid.NewGuid();
        var svc = new NewPopularService(_ts.Store, _catalog, new Throwing());
        var r = svc.Build(u, tmdbConfigured: false, Now);
        Assert.Empty(r.ComingSoon);
        Assert.Empty(r.EveryonesWatching);
    }

    private sealed class Throwing : ITrendingProvider
    {
        public IReadOnlyList<Guid> Trending(Guid userId, int take) => throw new InvalidOperationException("boom");

        public IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Signal_provider_counts_distinct_people_skips_kids_and_excluded_users_and_hides_invisible_titles()
    {
        var ann = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var kid = Guid.NewGuid();
        var viewer = Guid.NewGuid();
        var popular = Make.Item("Popular", 1, rating: 7);
        var kidsOnly = Make.Item("Kids Only", 2, rating: 9);
        var secret = Make.Item("Secret", 3, rating: 9);
        var show = Make.Item("A Show", 4, CatalogKind.Series, rating: 6);
        _catalog.Items.AddRange(new[] { popular, kidsOnly, secret, show });
        _catalog.Visible = new HashSet<Guid> { popular.Id, kidsOnly.Id, show.Id };
        _users.Caps[kid] = 5;
        _config.Current.ExcludedUserIds = new[] { bob.ToString("N") };
        _ts.Store.Write(d =>
        {
            void Play(Guid u, CatalogItem i) => d.Signals.Add(new PlaySignal { UserId = u, ItemId = i.Id, At = DateTime.UtcNow.AddDays(-1), Completion = 1 });
            Play(ann, popular);
            Play(kid, popular);
            Play(kid, kidsOnly);
            Play(kid, kidsOnly);
            Play(bob, secret);
            Play(ann, secret);
            Play(ann, show);
        });
        var p = new SignalTrendingProvider(_ts.Store, _catalog, _config, _users);

        var trending = p.Trending(viewer, 10);
        Assert.Equal(new[] { popular.Id, show.Id }, trending.ToArray()); // kids-only title absent, secret invisible
        Assert.Equal(new[] { popular.Id }, p.TopTen(viewer, CatalogKind.Movie).ToArray());
        Assert.Equal(new[] { show.Id }, p.TopTen(viewer, CatalogKind.Series).ToArray());

        // Not-for-me titles are not pushed back at the person who rejected them.
        _ts.Store.Write(d => d.Ratings[StoreData.UserItemKey(viewer, popular.Id)] = -1);
        Assert.DoesNotContain(popular.Id, p.Trending(viewer, 10));
    }
}

public class InteractionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly TempStore _ts = new();
    private readonly Guid _ann = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public void Dispose() => _ts.Dispose();

    private InteractionLog Log() => new(_ts.Store, NullLogger<InteractionLog>.Instance);

    private static StoredEvent E(Guid u, string type, string? row = null, string? query = null, DateTime? at = null)
        => new(u, type, row, null, query, at ?? Now);

    [Fact]
    public void Validator_accepts_known_types_and_rejects_the_rest()
    {
        var item = Guid.NewGuid();
        var ok = EventValidator.Validate(_ann, new ClientEvent("CardClicked", "TopPicks", item.ToString(), null, null), Now);
        Assert.NotNull(ok);
        Assert.Equal("cardClicked", ok!.Type);
        Assert.Equal("toppicks", ok.RowType);
        Assert.Equal(item.ToString("N"), ok.ItemId);
        Assert.Equal(_ann, ok.UserId);

        Assert.Null(EventValidator.Validate(_ann, new ClientEvent("deleteEverything", null, null, null, null), Now));
        Assert.Null(EventValidator.Validate(_ann, new ClientEvent("rowShown", "bad row!", null, null, null), Now));
        Assert.Null(EventValidator.Validate(_ann, new ClientEvent("rowShown", null, "not-a-guid", null, null), Now));
        Assert.Null(EventValidator.Validate(_ann, new ClientEvent("searchIssued", null, null, "   ", null), Now));
        Assert.Null(EventValidator.Validate(_ann, null, Now));
        Assert.Null(EventValidator.Validate(_ann, new ClientEvent(null, null, null, null, null), Now));
    }

    [Fact]
    public void Validator_cleans_queries_and_distrusts_the_client_clock()
    {
        var long_ = new string('x', 500);
        Assert.Equal(100, EventValidator.Validate(_ann, new ClientEvent("searchIssued", null, null, long_, null), Now)!.Query!.Length);
        Assert.Equal("a b", EventValidator.Validate(_ann, new ClientEvent("searchIssued", null, null, "a\u0000\n  b", null), Now)!.Query);

        JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Assert.Equal(Now, EventValidator.ParseTime(null, Now));
        Assert.Equal(Now, EventValidator.ParseTime(J("\"2001-01-01T00:00:00Z\""), Now));      // far in the past
        Assert.Equal(Now, EventValidator.ParseTime(J("\"2099-01-01T00:00:00Z\""), Now));      // far in the future
        Assert.Equal(Now, EventValidator.ParseTime(J("\"nonsense\""), Now));
        var recent = Now.AddMinutes(-3);
        Assert.Equal(recent, EventValidator.ParseTime(J($"\"{recent:O}\""), Now));
        Assert.Equal(recent, EventValidator.ParseTime(J(new DateTimeOffset(recent).ToUnixTimeMilliseconds().ToString()), Now));
    }

    [Fact]
    public void Rate_limiter_allows_a_budget_per_user_per_minute()
    {
        var l = new EventRateLimiter();
        Assert.True(l.TryTake(_ann, EventRateLimiter.MaxEventsPerMinute, Now));
        Assert.False(l.TryTake(_ann, 1, Now.AddSeconds(10)));
        Assert.True(l.TryTake(_bob, 1, Now.AddSeconds(10)), "another user has their own budget");
        Assert.True(l.TryTake(_ann, 1, Now.AddSeconds(61)), "a new minute starts a new budget");
    }

    [Fact]
    public void Log_appends_to_its_own_file_not_the_store_and_survives_garbage_lines()
    {
        var log = Log();
        log.Append(new[] { E(_ann, "rowShown", "toppicks"), E(_ann, "searchIssued", query: "alien") });
        Assert.True(File.Exists(log.FilePath));
        Assert.EndsWith("events.jsonl", log.FilePath);
        _ts.Store.Write(d => d.MyList.Add("x"));
        _ts.Store.Flush();
        Assert.DoesNotContain("toppicks", File.ReadAllText(Path.Combine(_ts.Store.DirectoryPath, "store.json")), StringComparison.Ordinal);
        File.AppendAllText(log.FilePath, "this is not json\n{\"u\":\"x\"}\n");
        var all = log.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("alien", all[1].Query);
    }

    [Fact]
    public void Log_rotates_when_large_prunes_old_lines_and_deletes_per_user()
    {
        var log = Log();
        File.WriteAllText(log.FilePath, new string(' ', (int)InteractionLog.RotateBytes + 1));
        var fresh = Log(); // a fresh instance reads the length from disk
        fresh.Append(new[] { E(_ann, "rowShown", "a") });
        Assert.True(File.Exists(Path.Combine(_ts.Store.DirectoryPath, "events.1.jsonl")), "the full file was rotated");
        Assert.True(new FileInfo(fresh.FilePath).Length < 1000);
        File.Delete(Path.Combine(_ts.Store.DirectoryPath, "events.1.jsonl"));

        fresh.Append(new[] { E(_bob, "rowShown", "b"), E(_bob, "rowShown", "old", at: Now.AddDays(-120)) });
        Assert.Equal(1, fresh.Prune(Now));
        Assert.Equal(2, fresh.ReadAll().Count);
        Assert.Equal(1, fresh.Delete(_bob));
        Assert.All(fresh.ReadAll(), e => Assert.Equal(_ann, e.UserId));
        fresh.Delete(null);
        Assert.Empty(fresh.ReadAll());
    }

    [Fact]
    public void Metrics_report_has_take_rates_never_clicked_rows_top_searches_and_per_day_counts_but_no_user_ids()
    {
        var log = Log();
        var evs = new List<StoredEvent>();
        for (var i = 0; i < 10; i++)
        {
            evs.Add(E(_ann, "rowShown", "toppicks"));
            evs.Add(E(_bob, "rowShown", "hidden"));
        }

        evs.Add(E(_ann, "cardClicked", "toppicks"));
        evs.Add(E(_ann, "cardClicked", "toppicks"));
        evs.Add(E(_ann, "playStarted", "toppicks"));
        evs.Add(E(_ann, "playStarted"));                       // not from a row
        evs.Add(E(_ann, "searchIssued", query: "Alien"));
        evs.Add(E(_bob, "searchIssued", query: "alien"));
        evs.Add(E(_bob, "searchIssued", query: "heat"));
        evs.Add(E(_bob, "rowShown", "old", at: Now.AddDays(-45))); // outside the 30 day window
        log.Append(evs);

        var report = new MetricsService(log, new FakeConfig()).Report(Now);

        var top = report.Rows.Single(r => r.RowType == "toppicks");
        Assert.Equal(10, top.Shown);
        Assert.Equal(2, top.Clicks);
        Assert.Equal(1, top.Plays);
        Assert.Equal(0.3, top.TakeRate, 3);
        Assert.Equal(new[] { "hidden" }, report.NeverClickedRows.ToArray());
        Assert.DoesNotContain(report.Rows, r => r.RowType == "old");
        Assert.Equal(1, report.PlaysFromRows);
        Assert.Equal(2, report.PlaysTotal);
        Assert.Equal("alien", report.TopSearches[0].Query);
        Assert.Equal(2, report.TopSearches[0].Count);
        Assert.Equal(2, report.TopSearches[0].Users);
        Assert.Equal(30, report.EventsPerDay.Count);
        Assert.Equal(report.TotalEvents, report.EventsPerDay.Sum(d => d.Events));
        Assert.Equal(2, report.ActiveUsers);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(_ann.ToString("N"), json, StringComparison.Ordinal);
        Assert.DoesNotContain(_bob.ToString("N"), json, StringComparison.Ordinal);
    }

    [Fact]
    public void Engagement_is_per_user_smoothed_and_empty_when_collection_is_off()
    {
        var log = Log();
        var evs = new List<StoredEvent>();
        for (var i = 0; i < 20; i++)
        {
            evs.Add(E(_ann, "rowShown", "because"));
            evs.Add(E(_ann, "rowShown", "toppicks"));
        }

        for (var i = 0; i < 10; i++)
        {
            evs.Add(E(_ann, "cardClicked", "because"));
        }

        evs.Add(E(_bob, "rowShown", "toppicks"));
        evs.Add(E(_bob, "cardClicked", "toppicks"));
        log.Append(evs);
        var cfg = new FakeConfig();
        IRowEngagementProvider p = new MetricsService(log, cfg);

        var mine = p.GetEngagement(_ann);
        Assert.True(mine["because"] > mine["toppicks"], "clicked rows score higher");
        Assert.InRange(mine["because"], 0, 1);
        Assert.True(p.GetEngagement(_bob)["toppicks"] < 0.5, "one lucky click on one impression must not look like 100%");
        Assert.Empty(p.GetEngagement(Guid.NewGuid()));

        cfg.Current.CollectInteractionMetrics = false;
        Assert.Empty(p.GetEngagement(_ann));
    }
}

public class OnboardingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly Guid _u = Guid.NewGuid();

    public void Dispose() => _ts.Dispose();

    private OnboardingService Svc() => new(_ts.Store, _catalog);

    private void Library()
    {
        var genres = new[] { "Drama", "Comedy", "Action", "Horror", "Sci-Fi", "Romance" };
        foreach (var g in genres)
        {
            for (var i = 0; i < 8; i++)
            {
                _catalog.Items.Add(Make.Item($"{g} {i}", 100 + _catalog.Items.Count, i % 3 == 0 ? CatalogKind.Series : CatalogKind.Movie, new[] { g }, rating: 9 - (i * 0.3f)));
            }
        }
    }

    [Fact]
    public void A_new_user_is_eligible_but_not_one_with_history_or_one_who_already_finished()
    {
        Library();
        var svc = Svc();
        Assert.True(svc.IsEligible(_u));

        _ts.Store.Write(d =>
        {
            for (var i = 0; i < OnboardingService.HistoryThreshold; i++)
            {
                d.Signals.Add(new PlaySignal { UserId = _u, ItemId = _catalog.Items[i].Id, At = DateTime.UtcNow, Completion = 1 });
            }
        });
        Assert.False(svc.IsEligible(_u));

        var other = Guid.NewGuid();
        Assert.True(svc.IsEligible(other));
        svc.Submit(other, null, null, skipped: true, Now);
        Assert.False(svc.IsEligible(other), "offered once");
    }

    [Fact]
    public void Suggestions_are_diverse_well_rated_visible_and_exclude_titles_the_user_already_has_signals_for()
    {
        Library();
        var hiddenTitle = _catalog.Items[0];
        var watched = _catalog.Items[1];
        var lowRated = Make.Item("Bad Movie", 999, genres: new[] { "Drama" }, rating: 3f);
        _catalog.Items.Add(lowRated);
        _catalog.Visible = _catalog.Items.Where(i => i.Id != hiddenTitle.Id).Select(i => i.Id).ToHashSet();
        _ts.Store.Write(d => d.Signals.Add(new PlaySignal { UserId = _u, ItemId = watched.Id, At = DateTime.UtcNow, Completion = 1 }));

        var s = Svc().Suggestions(_u);

        Assert.Equal(OnboardingService.SuggestionCount, s.Count);
        Assert.Equal(s.Count, s.Select(i => i.Id).Distinct().Count());
        Assert.DoesNotContain(s, i => i.Id == hiddenTitle.Id);
        Assert.DoesNotContain(s, i => i.Id == watched.Id);
        Assert.DoesNotContain(s, i => i.Id == lowRated.Id);
        Assert.All(s, i => Assert.True(i.Rating >= OnboardingService.MinRating));
        Assert.True(s.SelectMany(i => i.Genres).Distinct().Count() >= 5, "all genres are represented, not just the best-rated one");
        Assert.Contains(s, i => i.Kind == CatalogKind.Series);
        Assert.Contains(s, i => i.Kind == CatalogKind.Movie);
    }

    [Fact]
    public void Submit_seeds_plus_one_ratings_keeps_existing_ones_stores_genres_and_ignores_invisible_or_unknown_ids()
    {
        Library();
        var a = _catalog.Items[0];
        var b = _catalog.Items[1];
        var loved = _catalog.Items[2];
        var secret = _catalog.Items[3];
        _catalog.Visible = _catalog.Items.Where(i => i.Id != secret.Id).Select(i => i.Id).ToHashSet();
        _ts.Store.Write(d => d.Ratings[StoreData.UserItemKey(_u, loved.Id)] = 2);
        var svc = Svc();

        var outcome = svc.Submit(_u, new[] { a.Id.ToString("N"), b.Id.ToString(), loved.Id.ToString("N"), secret.Id.ToString("N"), Guid.NewGuid().ToString(), "junk" }, new[] { "drama", "Comedy", "Made Up" }, skipped: false, Now);

        Assert.Equal(2, outcome.Seeded);
        Assert.Equal(2, outcome.Genres);
        var ratings = _ts.Store.Read(d => d.Ratings.ToDictionary(kv => kv.Key, kv => kv.Value));
        Assert.Equal(1, ratings[StoreData.UserItemKey(_u, a.Id)]);
        Assert.Equal(1, ratings[StoreData.UserItemKey(_u, b.Id)]);
        Assert.Equal(2, ratings[StoreData.UserItemKey(_u, loved.Id)]);
        Assert.False(ratings.ContainsKey(StoreData.UserItemKey(_u, secret.Id)));
        Assert.Equal(new[] { "Drama", "Comedy" }, svc.GenrePreferences(_u).ToArray());
        Assert.False(svc.IsEligible(_u));
    }

    [Fact]
    public void Skipping_only_marks_it_done()
    {
        Library();
        var svc = Svc();
        svc.Submit(_u, new[] { _catalog.Items[0].Id.ToString("N") }, new[] { "Drama" }, skipped: true, Now);
        Assert.Empty(_ts.Store.Read(d => d.Ratings));
        Assert.Empty(svc.GenrePreferences(_u));
        Assert.True(_ts.Store.Read(d => d.Onboarding[_u.ToString("N")].Skipped));
    }

    [Fact]
    public void Hidden_titles_are_per_user_capped_and_reversible()
    {
        var h = new HiddenItemsService(_ts.Store);
        IHiddenItems api = h;
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        Assert.False(api.IsHidden(_u, item));
        Assert.True(h.Hide(_u, item));
        Assert.True(h.Hide(_u, item));
        Assert.True(api.IsHidden(_u, item));
        Assert.False(api.IsHidden(other, item));
        Assert.Equal(new[] { item }, api.HiddenFor(_u).ToArray());
        h.Unhide(_u, item);
        Assert.False(api.IsHidden(_u, item));

        for (var i = 0; i < HiddenItemsService.MaxHiddenPerUser; i++)
        {
            Assert.True(h.Hide(_u, Guid.NewGuid()));
        }

        Assert.False(h.Hide(_u, Guid.NewGuid()));
    }
}

public class SuggestTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly FakeCast _cast = new();
    private readonly Guid _u = Guid.NewGuid();

    private SuggestService Svc() => new(_catalog, _cast);

    [Theory]
    [InlineData("matrix", "matrix", true)]
    [InlineData("matirx", "matrix", true)]          // swapped letters
    [InlineData("godfather", "the godfather", true)]
    [InlineData("godfater", "the godfather", true)]
    [InlineData("alein", "alien", true)]
    [InlineData("blade", "blade runner", true)]
    [InlineData("zzzzz", "matrix", false)]
    [InlineData("star wars", "star trek", false)]
    public void Fuzzy_matching(string query, string name, bool expected)
        => Assert.Equal(expected, FuzzyText.Score(FuzzyText.Normalize(query), FuzzyText.Normalize(name)) > 0);

    [Fact]
    public void Normalize_removes_accents_and_punctuation()
    {
        Assert.Equal("amelie", FuzzyText.Normalize("Amélie"));
        Assert.Equal("spider man no way home", FuzzyText.Normalize("Spider-Man: No Way Home!"));
        Assert.Equal(string.Empty, FuzzyText.Normalize("  "));
        Assert.Equal(1, FuzzyText.Levenshtein("ab", "ba", 2));
        Assert.Equal(3, FuzzyText.Levenshtein("abcdef", "xyz", 2));
    }

    [Fact]
    public void Suggest_returns_visible_titles_people_and_genres_with_typo_tolerance()
    {
        var matrix = Make.Item("The Matrix", 1, genres: new[] { "Sci-Fi", "Action" }, year: 1999, rating: 8.7f);
        var secret = Make.Item("Matrix Secret", 2, genres: new[] { "Sci-Fi" }, rating: 9f);
        var alien = Make.Item("Alien", 3, genres: new[] { "Horror", "Sci-Fi" }, rating: 8.4f);
        _catalog.Items.AddRange(new[] { matrix, secret, alien });
        _catalog.Visible = new HashSet<Guid> { matrix.Id, alien.Id };
        _cast.Cast[matrix.Id] = new[] { "Keanu Reeves", "Carrie-Anne Moss" };
        _cast.Cast[secret.Id] = new[] { "Hidden Actor" };
        _cast.Cast[alien.Id] = new[] { "Sigourney Weaver" };

        var typo = Svc().Suggest(_u, "matirx");
        Assert.Equal(new[] { "The Matrix" }, typo.Titles.Select(t => t.Name).ToArray());
        Assert.Equal("Movie", typo.Titles[0].Type);
        Assert.Equal(1999, typo.Titles[0].Year);

        var people = Svc().Suggest(_u, "keanu");
        Assert.Equal("Keanu Reeves", Assert.Single(people.People).Name);
        Assert.Equal(1, people.People[0].Titles);
        Assert.Empty(Svc().Suggest(_u, "hidden actor").People);          // only on an invisible title

        var genres = Svc().Suggest(_u, "sci");
        Assert.Contains("Sci-Fi", genres.Genres);
        Assert.Empty(Svc().Suggest(_u, "x").Titles);                       // too short
        Assert.Empty(Svc().Suggest(_u, null).Titles);
    }

    [Fact]
    public void Extras_add_cast_matches_and_typo_titles_but_only_visible_ones()
    {
        var heat = Make.Item("Heat", 1, rating: 8.3f);
        var casino = Make.Item("Casino", 2, rating: 8.2f);
        var hiddenFilm = Make.Item("Secret Film", 3, rating: 9f);
        _catalog.Items.AddRange(new[] { heat, casino, hiddenFilm });
        _catalog.Visible = new HashSet<Guid> { heat.Id, casino.Id };
        _cast.Cast[heat.Id] = new[] { "Robert De Niro" };
        _cast.Cast[casino.Id] = new[] { "Robert De Niro" };
        _cast.Cast[hiddenFilm.Id] = new[] { "Robert De Niro" };

        var byPerson = Svc().Extras(_u, "robert de niro", includeTypoTitles: false);
        Assert.Equal(new[] { "Heat", "Casino" }, byPerson.Select(i => i.Name).ToArray());

        var typo = Svc().Extras(_u, "casion", includeTypoTitles: true);
        Assert.Contains(typo, i => i.Name == "Casino");
        Assert.DoesNotContain(typo, i => i.Name == "Secret Film");
        Assert.Empty(Svc().Extras(_u, "casion", includeTypoTitles: false));
    }

    [Fact]
    public void A_cast_index_that_throws_never_breaks_search()
    {
        _catalog.Items.Add(Make.Item("Heat", 1));
        var svc = new SuggestService(_catalog, new ThrowingCast());
        Assert.Single(svc.Suggest(_u, "heat").Titles);
    }

    private sealed class ThrowingCast : ICastIndex
    {
        public IReadOnlyList<string> CastOf(Guid itemId) => throw new InvalidOperationException("boom");
    }
}
