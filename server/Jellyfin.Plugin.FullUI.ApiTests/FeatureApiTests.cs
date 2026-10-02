using System.Text.Json;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class NewPopularApiTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new();
    private readonly FakeUsers _users = new();

    public void Dispose() => _ts.Dispose();

    private NewPopularController Controller(Guid? user = null)
        => new NewPopularController(
            new NewPopularService(_ts.Store, _catalog, new SignalTrendingProvider(_ts.Store, _catalog, _config, _users)),
            new ReminderService(_ts.Store, _catalog, _config, NullLogger<ReminderService>.Instance),
            _config,
            NullLogger<NewPopularController>.Instance).As(user);

    private void SoonList(string date = "")
        => _ts.Store.Write(d => d.ComingSoon[Http.User.ToString("N")] = new()
        {
            new ComingSoonEntry { TmdbId = 1, MediaType = "movie", Title = "Later", PosterPath = "/p", ReleaseDate = Dates.Soon, Score = 5 },
            new ComingSoonEntry { TmdbId = 2, MediaType = "tv", Title = "Sooner", PosterPath = "/p", ReleaseDate = DateTime.UtcNow.AddDays(3).ToString("yyyy-MM-dd"), Score = 1 },
            new ComingSoonEntry { TmdbId = 3, MediaType = "movie", Title = "Released", PosterPath = "/p", ReleaseDate = Dates.Past, Score = 9 },
        });

    [Fact]
    public async Task NewPopular_has_the_four_lists_in_camelCase_with_coming_soon_by_date()
    {
        SoonList();
        var hit = Make.Item("Hit", 50, rating: 8);
        _catalog.Items.Add(hit);
        _ts.Store.Write(d => d.Signals.Add(new PlaySignal { UserId = Guid.NewGuid(), ItemId = hit.Id, At = DateTime.UtcNow, Completion = 1 }));

        var (status, body, _) = await Http.Render(Controller().NewPopular());

        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(new[] { "Sooner", "Later" }, root.GetProperty("comingSoon").EnumerateArray().Select(c => c.GetProperty("title").GetString()).ToArray());
        Assert.All(root.GetProperty("comingSoon").EnumerateArray(), c => Assert.True(c.GetProperty("upcoming").GetBoolean()));
        Assert.Equal("Hit", root.GetProperty("everyonesWatching")[0].GetProperty("name").GetString());
        Assert.Equal(1, root.GetProperty("top10Movies")[0].GetProperty("rank").GetInt32());
        Assert.Equal(0, root.GetProperty("top10Shows").GetArrayLength());
    }

    [Fact]
    public async Task Remind_sets_lists_and_clears_only_my_reminders_and_marks_the_card_reminded()
    {
        SoonList();
        var c = Controller();
        Assert.IsType<NoContentResult>(c.Remind(new RemindRequest(1, "movie", true)));

        var (_, list, _) = await Http.Render(c.Reminders());
        Http.AssertCamelCase(list);
        Assert.Contains("\"title\":\"Later\"", list);

        var (_, page, _) = await Http.Render(c.NewPopular());
        using (var doc = JsonDocument.Parse(page))
        {
            var cards = doc.RootElement.GetProperty("comingSoon").EnumerateArray().ToList();
            Assert.True(cards.Single(x => x.GetProperty("title").GetString() == "Later").GetProperty("reminded").GetBoolean());
            Assert.False(cards.Single(x => x.GetProperty("title").GetString() == "Sooner").GetProperty("reminded").GetBoolean());
        }

        var someoneElse = Controller(Guid.NewGuid());
        var (_, theirs, _) = await Http.Render(someoneElse.Reminders());
        Assert.Contains("\"items\":[]", theirs);

        Assert.IsType<NoContentResult>(c.Remind(new RemindRequest(1, "movie", false)));
        var (_, after, _) = await Http.Render(c.Reminders());
        Assert.Contains("\"items\":[]", after);
    }

    [Fact]
    public async Task Remind_rejects_unknown_titles_and_missing_bodies_with_a_friendly_message()
    {
        SoonList();
        var (s1, b1, _) = await Http.Render(Controller().Remind(new RemindRequest(404, "movie", true)));
        Assert.Equal(400, s1);
        Http.AssertCamelCase(b1);
        Assert.Contains("message", b1);
        Assert.Equal(400, (await Http.Render(Controller().Remind(null))).Status);
    }

    [Fact]
    public void Everything_needs_a_signed_in_user()
    {
        var c = Controller(Guid.Empty);
        Assert.IsType<UnauthorizedResult>(c.NewPopular());
        Assert.IsType<UnauthorizedResult>(c.Remind(new RemindRequest(1, "movie", true)));
        Assert.IsType<UnauthorizedResult>(c.Reminders());
    }

    [Fact]
    public async Task Coming_soon_endpoint_lists_released_recommendations_separately()
    {
        SoonList();
        var c = new DiscoveryController(_ts.Store, _catalog, new VoteService(_ts.Store), new NlSearch(_ts.Store, _catalog, new FakeOllama()), _config, new FakeTmdbClient(), NullLogger<DiscoveryController>.Instance).As();
        var (_, body, _) = await Http.Render(c.ComingSoon());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(2, doc.RootElement.GetProperty("cards").GetArrayLength());
        var rec = doc.RootElement.GetProperty("recommended").EnumerateArray().ToList();
        Assert.Equal("Released", Assert.Single(rec).GetProperty("title").GetString());
        Assert.False(rec[0].GetProperty("upcoming").GetBoolean());

        _config.Current.ShowRecommendedNotInLibrary = false;
        var (_, off, _) = await Http.Render(c.ComingSoon());
        Assert.DoesNotContain("recommended", off);
    }
}

public class EventsApiTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeConfig _config = new();
    private readonly EventRateLimiter _limiter = new();

    public void Dispose() => _ts.Dispose();

    private (MetricsController C, InteractionLog Log) Make_(Guid? user = null)
    {
        var log = new InteractionLog(_ts.Store, NullLogger<InteractionLog>.Instance);
        return (new MetricsController(log, new MetricsService(log, _config), _limiter, _config, NullLogger<MetricsController>.Instance).As(user), log);
    }

    private static ClientEvent Ev(string type, string? row = null, string? query = null) => new(type, row, null, query, null);

    [Fact]
    public async Task Events_are_stored_under_the_token_user_only_and_invalid_ones_are_counted()
    {
        var (c, log) = Make_();
        var (status, body, _) = await Http.Render(c.PostEvents(new EventsRequest(new[] { Ev("rowShown", "toppicks"), Ev("searchIssued", null, "alien"), Ev("bogus"), Ev("rowShown", "bad row") })));
        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        Assert.Contains("\"accepted\":2", body);
        Assert.Contains("\"rejected\":2", body);
        var stored = log.ReadAll();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.Equal(Http.User, e.UserId));
    }

    [Fact]
    public async Task A_batch_over_200_is_refused_and_empty_batches_are_fine()
    {
        var (c, log) = Make_();
        var big = Enumerable.Range(0, 201).Select(_ => Ev("rowShown", "a")).ToArray();
        Assert.Equal(400, (await Http.Render(c.PostEvents(new EventsRequest(big)))).Status);
        Assert.Empty(log.ReadAll());
        var exactly = Enumerable.Range(0, 200).Select(_ => Ev("rowShown", "a")).ToArray();
        Assert.Equal(200, (await Http.Render(c.PostEvents(new EventsRequest(exactly)))).Status);
        Assert.Equal(200, (await Http.Render(c.PostEvents(new EventsRequest(null)))).Status);
        Assert.Equal(200, (await Http.Render(c.PostEvents(null))).Status);
    }

    [Fact]
    public async Task A_flood_from_one_user_gets_429_but_others_are_unaffected()
    {
        var (c, _) = Make_();
        var batch = new EventsRequest(Enumerable.Range(0, 200).Select(_ => Ev("rowShown", "a")).ToArray());
        for (var i = 0; i < EventRateLimiter.MaxEventsPerMinute / 200; i++)
        {
            Assert.Equal(200, (await Http.Render(c.PostEvents(batch))).Status);
        }

        var (status, body, _) = await Http.Render(c.PostEvents(batch));
        Assert.Equal(429, status);
        Assert.Contains("message", body);

        var (other, _) = Make_(Guid.NewGuid());
        Assert.Equal(200, (await Http.Render(other.PostEvents(batch))).Status);
    }

    [Fact]
    public async Task When_collection_is_off_nothing_is_written()
    {
        _config.Current.CollectInteractionMetrics = false;
        var (c, log) = Make_();
        var (status, body, _) = await Http.Render(c.PostEvents(new EventsRequest(new[] { Ev("rowShown", "a") })));
        Assert.Equal(200, status);
        Assert.Contains("\"accepted\":0", body);
        Assert.Empty(log.ReadAll());
        Assert.False(File.Exists(log.FilePath));
    }

    [Fact]
    public async Task Admin_metrics_is_camelCase_and_aggregate_only()
    {
        var (c, _) = Make_();
        await Http.Render(c.PostEvents(new EventsRequest(new[] { Ev("rowShown", "toppicks"), Ev("cardClicked", "toppicks"), Ev("searchIssued", null, "heat") })));
        var (status, body, _) = await Http.Render(c.Metrics(null));
        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        Assert.DoesNotContain(Http.User.ToString("N"), body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("toppicks", doc.RootElement.GetProperty("rows")[0].GetProperty("rowType").GetString());
        Assert.Equal(1.0, doc.RootElement.GetProperty("rows")[0].GetProperty("takeRate").GetDouble());
        Assert.Equal("heat", doc.RootElement.GetProperty("topSearches")[0].GetProperty("query").GetString());
    }

    [Fact]
    public void Unsigned_callers_are_refused()
    {
        var (c, _) = Make_(Guid.Empty);
        Assert.IsType<UnauthorizedResult>(c.PostEvents(new EventsRequest(new[] { Ev("rowShown", "a") })));
    }
}

public class OnboardingApiTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeHome _home = new();

    public void Dispose() => _ts.Dispose();

    private OnboardingController Controller(Guid? user = null)
        => new OnboardingController(new OnboardingService(_ts.Store, _catalog), new HiddenItemsService(_ts.Store), _catalog, _ts.Store, _home, NullLogger<OnboardingController>.Instance).As(user);

    private void Library()
    {
        foreach (var g in new[] { "Drama", "Comedy", "Action", "Horror" })
        {
            for (var i = 0; i < 8; i++)
            {
                _catalog.Items.Add(Make.Item($"{g} {i}", 100 + _catalog.Items.Count, genres: new[] { g }, rating: 8f));
            }
        }
    }

    [Fact]
    public async Task Get_offers_a_new_user_cards_and_genres_and_nothing_to_users_with_history()
    {
        Library();
        var (_, body, _) = await Http.Render(Controller().Get());
        Http.AssertCamelCase(body);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("eligible").GetBoolean());
        Assert.Equal(OnboardingService.SuggestionCount, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(4, doc.RootElement.GetProperty("genres").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("items")[0].TryGetProperty("id", out _));

        _ts.Store.Write(d =>
        {
            foreach (var i in _catalog.Items.Take(OnboardingService.HistoryThreshold))
            {
                d.Ratings[StoreData.UserItemKey(Http.User, i.Id)] = 1;
            }
        });
        var (_, hist, _) = await Http.Render(Controller().Get());
        using var h = JsonDocument.Parse(hist);
        Assert.False(h.RootElement.GetProperty("eligible").GetBoolean());
        Assert.Equal(0, h.RootElement.GetProperty("items").GetArrayLength());

        // Re-opening on purpose (menu entry) still works.
        var (_, forced, _) = await Http.Render(Controller().Get(force: true));
        using var f = JsonDocument.Parse(forced);
        Assert.True(f.RootElement.GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Post_seeds_ratings_marks_done_and_refreshes_the_users_home()
    {
        Library();
        var pick = _catalog.Items[3];
        var (status, body, _) = await Http.Render(Controller().Post(new OnboardingSubmitRequest(new[] { pick.Id.ToString("N") }, new[] { "Drama" }, false)));
        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        Assert.Equal(1, _ts.Store.Read(d => d.Ratings[StoreData.UserItemKey(Http.User, pick.Id)]));
        Assert.Contains(Http.User, _home.Invalidated);

        var (_, again, _) = await Http.Render(Controller().Get());
        Assert.Contains("\"eligible\":false", again);
        Assert.Equal(400, (await Http.Render(Controller().Post(null))).Status);
    }

    [Fact]
    public async Task Hide_and_unhide_are_per_user_validated_and_refresh_the_home()
    {
        Library();
        var item = _catalog.Items[0];
        var hidden = new HiddenItemsService(_ts.Store);

        Assert.IsType<NoContentResult>(Controller().Hide(new ItemIdRequest(item.Id.ToString("N"))));
        Assert.True(hidden.IsHidden(Http.User, item.Id));
        Assert.False(hidden.IsHidden(Guid.NewGuid(), item.Id));
        Assert.Contains(Http.User, _home.Invalidated);

        Assert.IsType<NoContentResult>(Controller().Unhide(new ItemIdRequest(item.Id.ToString("N"))));
        Assert.False(hidden.IsHidden(Http.User, item.Id));

        Assert.Equal(400, (await Http.Render(Controller().Hide(new ItemIdRequest("junk")))).Status);
        Assert.Equal(400, (await Http.Render(Controller().Hide(null))).Status);
        _catalog.Visible = new HashSet<Guid>();
        var (s, b, _) = await Http.Render(Controller().Hide(new ItemIdRequest(item.Id.ToString("N"))));
        Assert.Equal(404, s);
        Assert.Contains("message", b);
    }

    [Fact]
    public async Task Suggest_endpoint_is_camelCase_and_visible_only()
    {
        var matrix = Make.Item("The Matrix", 1, genres: new[] { "Sci-Fi" }, year: 1999);
        var secret = Make.Item("Matrix Secret", 2);
        _catalog.Items.AddRange(new[] { matrix, secret });
        _catalog.Visible = new HashSet<Guid> { matrix.Id };
        var c = new SearchSuggestController(new SuggestService(_catalog, new NullCastIndex()), NullLogger<SearchSuggestController>.Instance).As();
        var (_, body, _) = await Http.Render(c.Suggest("matirx"));
        Http.AssertCamelCase(body);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("The Matrix", Assert.Single(doc.RootElement.GetProperty("titles").EnumerateArray().ToList()).GetProperty("name").GetString());
        Assert.Equal(200, (await Http.Render(c.Suggest(null))).Status);
        Assert.IsType<UnauthorizedResult>(new SearchSuggestController(new SuggestService(_catalog, new NullCastIndex()), NullLogger<SearchSuggestController>.Instance).As(Guid.Empty).Suggest("x"));
    }

    [Fact]
    public async Task Keyword_search_finds_titles_through_typos_and_cast()
    {
        var heat = Make.Item("Heat", 1, rating: 8f);
        var casino = Make.Item("Casino", 2, rating: 8f);
        _catalog.Items.AddRange(new[] { heat, casino });
        var cast = new FakeCast();
        cast.Cast[heat.Id] = new[] { "Al Pacino" };
        var config = new FakeConfig();
        var c = new DiscoveryController(_ts.Store, _catalog, new VoteService(_ts.Store), new NlSearch(_ts.Store, _catalog, new FakeOllama()), config, new FakeTmdbClient(), NullLogger<DiscoveryController>.Instance, new SuggestService(_catalog, cast)).As();

        var (_, typo, _) = await Http.Render(await c.Search("casion", default));
        Assert.Contains("\"name\":\"Casino\"", typo);

        var (_, person, _) = await Http.Render(await c.Search("al pacino", default));
        Assert.Contains("\"name\":\"Heat\"", person);
    }
}

public class StatusAndBrandingTests
{
    /// <summary>Plugin sets a static Instance that other tests expect to be null (no configuration); put it back.</summary>
    private static Plugin NewPlugin()
    {
        var p = new Plugin(Stub.Make<MediaBrowser.Common.Configuration.IApplicationPaths>(), Stub.Make<MediaBrowser.Model.Serialization.IXmlSerializer>());
        typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, null);
        return p;
    }

    [Fact]
    public async Task Status_exposes_the_player_assist_metrics_and_tmdb_attribution_fields()
    {
        var (_, body, _) = await Http.Render(new FullUIController(Stub.Make<System.Net.Http.IHttpClientFactory>()).As().Status());
        Http.AssertCamelCase(body);
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("playerAssistEnabled").GetBoolean());
        Assert.True(r.GetProperty("collectMetrics").GetBoolean());
        Assert.True(r.GetProperty("tmdbAttribution").GetBoolean());
        Assert.Equal("This product uses the TMDB API but is not endorsed or certified by TMDB.", r.GetProperty("tmdbAttributionText").GetString());
        // earlier fields are untouched
        Assert.True(r.TryGetProperty("serverName", out _));
        Assert.True(r.TryGetProperty("webInjected", out _));
    }

    [Fact]
    public void The_plugin_no_longer_calls_itself_netflix_and_the_default_accent_is_not_netflix_red()
    {
        Assert.DoesNotContain("netflix", NewPlugin().Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("#e5383b", new Configuration.PluginConfiguration().AccentColor);
        Assert.Equal("#e5383b", Configuration.Branding.DefaultAccent);
    }

    [Fact]
    public void Every_embedded_admin_page_carries_the_tmdb_logo_and_sentence()
    {
        var asm = typeof(Plugin).Assembly;
        foreach (var page in new[] { "settings", "requests", "health" })
        {
            using var s = asm.GetManifestResourceStream($"Jellyfin.Plugin.FullUI.Configuration.{page}.html");
            Assert.NotNull(s);
            var html = new StreamReader(s!).ReadToEnd();
            Assert.Contains("This product uses the TMDB API but is not endorsed or certified by TMDB.", html);
            Assert.Contains("aria-label=\"TMDB\"", html);
            Assert.DoesNotContain("netflix", html, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotNull(asm.GetManifestResourceStream("Jellyfin.Plugin.FullUI.Web.tmdb-logo.svg"));
    }

    [Fact]
    public void The_health_page_is_registered_in_the_server_menu()
    {
        var plugin = NewPlugin();
        var health = plugin.GetPages().Single(p => p.Name == "FullUIHealth");
        Assert.Equal("FullUI Health", health.DisplayName);
        Assert.True(health.EnableInMainMenu);
        Assert.Equal("server", health.MenuSection);
        Assert.NotNull(typeof(Plugin).Assembly.GetManifestResourceStream(health.EmbeddedResourcePath));
    }
}
