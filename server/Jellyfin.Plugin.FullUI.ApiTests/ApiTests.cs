using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

internal sealed class FakeOllama : IOllamaClient
{
    public bool Enabled { get; set; }

    public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => Task.FromResult<IReadOnlyList<float[]>?>(null);

    public Task<string?> ChatAsync(string system, string user, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "Connected."));
}

internal sealed class FakeTmdbClient : ITmdbClient
{
    public bool Configured { get; set; } = true;

    public Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

    public Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string m, int id, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

    public Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbTitle>>(Array.Empty<TmdbTitle>());

    public Task<string?> TrailerKeyAsync(string m, int id, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<TmdbGenre>> GenresAsync(string m, CancellationToken ct) => Task.FromResult<IReadOnlyList<TmdbGenre>>(Array.Empty<TmdbGenre>());
}

internal sealed class ThrowingCatalog : ICatalog
{
    public IReadOnlyList<CatalogItem> All => throw new InvalidOperationException("secret library failure at C:\\media");

    public IReadOnlySet<Guid> VisibleTo(Guid userId) => throw new InvalidOperationException("secret library failure");

    public void Invalidate()
    {
    }

    public event EventHandler? Changed { add { } remove { } }
}

/// <summary>
/// B-02: Jellyfin's MVC formatter is PascalCase but the contract (and the web/Fire TV clients) is camelCase.
/// Every action's body is rendered through a real MVC pipeline configured like Jellyfin's and checked for lowercase keys.
/// </summary>
public class JsonCasingTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new();
    private readonly FakeUsers _users = new();

    public void Dispose() => _ts.Dispose();

    private DiscoveryController Discovery(bool tmdbConfigured = true)
        => new DiscoveryController(
            _ts.Store,
            _catalog,
            new VoteService(_ts.Store),
            new NlSearch(_ts.Store, _catalog, new FakeOllama()),
            _config,
            new FakeTmdbClient { Configured = tmdbConfigured },
            NullLogger<DiscoveryController>.Instance).As();

    private AdminController Admin()
        => new AdminController(
            new RequestService(_ts.Store, _users, _config),
            new FakeOllama(),
            _catalog,
            null!,
            null!,
            null!,
            NullLogger<AdminController>.Instance).As();

    private HomeController Home()
        => new HomeController(
            new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, _config),
            _ts.Store,
            _catalog,
            Stub.Make<ILibraryManager>(),
            Stub.Make<IUserManager>(),
            Stub.Make<IUserDataManager>(),
            NullLogger<HomeController>.Instance).As();

    [Fact]
    public async Task Harness_WouldHaveCaught_PascalCaseBodies()
    {
        // Same payload through MVC's own formatter (what the old code did) is PascalCase: this is the bug the other tests guard.
        var (_, body, _) = await Http.Render(new ObjectResult(new SearchResponse("keyword", Array.Empty<ItemCard>())));
        Assert.Contains("\"Mode\"", body);
        var (_, fixedBody, _) = await Http.Render(SafeApi.Json(new SearchResponse("keyword", Array.Empty<ItemCard>())));
        Assert.Contains("\"mode\"", fixedBody);
        Assert.DoesNotContain("\"Mode\"", fixedBody);
    }

    [Fact]
    public async Task Search_UsesLowercaseKeys_AndFullCards()
    {
        var movie = Make.Item("Blade Runner", 1, genres: new[] { "Sci-Fi" });
        _catalog.Items.Add(movie);
        var (status, body, type) = await Http.Render(await Discovery().Search("blade", default));

        Assert.Equal(200, status);
        Assert.Contains("application/json", type);
        Http.AssertCamelCase(body);
        Assert.Contains("\"mode\":\"keyword\"", body);
        Assert.Contains("\"items\":[{", body);
        Assert.Contains("\"inMyList\":false", body);
        Assert.Contains("\"badges\":[", body);   // search cards now carry badges like Home cards
    }

    [Fact]
    public async Task Search_Empty_StillHasModeAndItems()
    {
        var (_, body, _) = await Http.Render(await Discovery().Search("  ", default));
        Assert.Equal("{\"mode\":\"keyword\",\"items\":[]}", body);
    }

    [Fact]
    public async Task Notifications_UseLowercaseKeys()
    {
        _ts.Store.Write(d => d.Notifications.Add(new NotificationEntry { UserId = Http.User, Text = "X is now available", At = DateTime.UtcNow }));
        var (_, body, _) = await Http.Render(Discovery().Notifications());
        Http.AssertCamelCase(body);
        Assert.Contains("\"items\":[{", body);
        Assert.Contains("\"text\":\"X is now available\"", body);
    }

    [Fact]
    public async Task ComingSoon_UsesLowercaseKeys_WithAndWithoutTmdb()
    {
        _ts.Store.Write(d => d.ComingSoon[Http.User.ToString("N")] = new() { new ComingSoonEntry { TmdbId = 9, MediaType = "movie", Title = "Soon", Score = 1, ReleaseDate = Dates.Soon } });

        var (_, off, _) = await Http.Render(Discovery(tmdbConfigured: false).ComingSoon());
        Assert.Equal("{\"cards\":[]}", off);

        var (_, on, _) = await Http.Render(Discovery().ComingSoon());
        Http.AssertCamelCase(on);
        Assert.Contains("\"tmdbId\":9", on);
        Assert.Contains("\"myVote\":0", on);
    }

    [Fact]
    public async Task Admin_Requests_UseLowercaseKeys()
    {
        _ts.Store.Write(d => d.Votes.Add(new VoteEntry { UserId = Http.User, TmdbId = 5, MediaType = "movie", Vote = 1, Title = "Wanted", At = DateTime.UtcNow }));
        var (_, body, _) = await Http.Render(Admin().GetRequests("votes"));
        Http.AssertCamelCase(body);
        Assert.Contains("\"wantCount\":1", body);
        Assert.Contains("\"tmdbUrl\"", body);
    }

    [Fact]
    public async Task TestEndpoints_ReturnOkAndMessage_InLowercase()
    {
        var (_, ollama, _) = await Http.Render(await Admin().TestOllama(new AdminController.OllamaTestRequest(null, null, null), default));
        Assert.Equal("{\"ok\":true,\"message\":\"Connected.\"}", ollama);

        var tmdb = new FullUIController(Stub.Make<System.Net.Http.IHttpClientFactory>()).As();
        var (_, noKey, _) = await Http.Render(await tmdb.TestTmdb(new FullUIController.TmdbTestRequest(null), default));
        Assert.Equal("{\"ok\":false,\"message\":\"No TMDB API key entered.\"}", noKey);
    }

    [Fact]
    public async Task Status_UsesLowercaseKeys_AndNeverHasTheKey()
    {
        var (_, body, _) = await Http.Render(new FullUIController(Stub.Make<System.Net.Http.IHttpClientFactory>()).As().Status());
        Http.AssertCamelCase(body);
        Assert.Contains("\"tmdbConfigured\"", body);
        Assert.DoesNotContain("TmdbApiKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"webInjected\"", body);
    }

    [Fact]
    public async Task Home_AndMyServer_UseLowercaseKeys()
    {
        var (_, home, _) = await Http.Render(Home().GetHome());
        Http.AssertCamelCase(home);
        Assert.Contains("\"serverName\"", home);
        Assert.Contains("\"rows\":[]", home);

        var (_, mine, _) = await Http.Render(Home().MyServer());
        Assert.Equal("{\"continueWatching\":[],\"myList\":[],\"wanted\":[]}", mine);
    }

    [Fact]
    public async Task Errors_AreLowercase_AndFriendly()
    {
        var (status, body, _) = await Http.Render(Discovery().Vote(new VoteRequest(0, "movie", 1, null, null, null, null, null)));
        Assert.Equal(400, status);
        Http.AssertCamelCase(body);
        Assert.Contains("\"message\"", body);

        var (pStatus, problem, pType) = await Http.Render(Home().Rate(new RateRequest(Guid.NewGuid(), 7)));
        Assert.Equal(400, pStatus);
        Assert.Contains("problem+json", pType);
        Http.AssertCamelCase(problem);
    }
}

public class SafeApiTests
{
    [Fact]
    public async Task Fail_LogsDetails_ButNeverReturnsThem()
    {
        var result = SafeApi.Run(NullLogger.Instance, "loading things", () => throw new InvalidOperationException("secret path C:\\keys\\tmdb.txt"));
        var (status, body, _) = await Http.Render(result);

        Assert.Equal(500, status);
        Assert.Contains("loading things", body);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("InvalidOperation", body);
        Http.AssertCamelCase(body);
    }

    [Fact]
    public async Task RunAsync_MapsCancellationTo499_AndOtherFailuresTo500()
    {
        var cancelled = await SafeApi.RunAsync(NullLogger.Instance, "x", () => throw new OperationCanceledException());
        Assert.Equal(499, ((StatusCodeResult)cancelled).StatusCode);

        var failed = await SafeApi.RunAsync(NullLogger.Instance, "x", () => throw new IOException("disk"));
        Assert.Equal(500, (await Http.Render(failed)).Status);
    }

    [Fact]
    public async Task Run_PassesSuccessThrough()
    {
        var ok = SafeApi.Run(NullLogger.Instance, "x", () => SafeApi.Json(new { fooBar = 1 }));
        var (status, body, _) = await Http.Render(ok);
        Assert.Equal(200, status);
        Assert.Equal("{\"fooBar\":1}", body);
    }

    [Fact]
    public async Task Json_KeepsRequestedStatus_AndDoesNotRenameKeysWithPascalFormatter()
    {
        var (status, body, _) = await Http.Render(SafeApi.Json(new SearchResponse("semantic", Array.Empty<ItemCard>()), 202));
        Assert.Equal(202, status);
        Assert.Equal("{\"mode\":\"semantic\",\"items\":[]}", body);
    }

    [Theory]
    [InlineData("#e50914", "#e50914")]
    [InlineData("#FFF", "#fff")]
    [InlineData("e50914", "#e50914")]
    [InlineData("#12345678", "#12345678")]
    [InlineData("red", "#e5383b")]
    [InlineData("#e5091", "#e5383b")]
    [InlineData("javascript:alert(1)", "#e5383b")]
    [InlineData("#e50914; background:url(x)", "#e5383b")]
    [InlineData("", "#e5383b")]
    [InlineData(null, "#e5383b")]
    public void AccentColor_IsValidatedServerSide(string? input, string expected)
        => Assert.Equal(expected, Branding.NormalizeAccent(input));
}

/// <summary>Titles a user may not see (library access, parental rating) must behave as if they do not exist.</summary>
public class InvisibleItemTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly CatalogItem _visible = Make.Item("Visible", 1);
    private readonly CatalogItem _hidden = Make.Item("Hidden", 2);
    private readonly FakeCatalog _catalog = new();

    public InvisibleItemTests()
    {
        _catalog.Items.AddRange(new[] { _visible, _hidden });
        _catalog.Visible = new HashSet<Guid> { _visible.Id };
    }

    public void Dispose() => _ts.Dispose();

    private HomeController Controller(ICatalog? catalog = null)
    {
        var cat = catalog ?? _catalog;
        return new HomeController(
            new HomeService(_ts.Store, cat, NullLogger<HomeService>.Instance, new FakeConfig()),
            _ts.Store,
            cat,
            Stub.Make<ILibraryManager>(),
            Stub.Make<IUserManager>(),
            Stub.Make<IUserDataManager>(),
            NullLogger<HomeController>.Instance).As();
    }

    [Fact]
    public void Rate_OnAnInvisibleTitle_Is404_AndStoresNothing()
    {
        var result = Controller().Rate(new RateRequest(_hidden.Id, 1));

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_ts.Store.Read(d => d.Ratings.ToList()));
    }

    [Fact]
    public void MyList_OnAnInvisibleTitle_Is404_AndStoresNothing()
    {
        var result = Controller().MyList(new MyListRequest(_hidden.Id, true));

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_ts.Store.Read(d => d.MyList.ToList()));
    }

    [Fact]
    public void Item_OnAnInvisibleTitle_Is404()
        => Assert.IsType<NotFoundResult>(Controller().GetItem(_hidden.Id));

    [Fact]
    public void Item_OnAnUnknownTitle_Is404()
        => Assert.IsType<NotFoundResult>(Controller().GetItem(Guid.NewGuid()));

    [Fact]
    public async Task VisibleTitle_CanBeRatedListed_AndLoaded()
    {
        var c = Controller();
        Assert.IsType<NoContentResult>(c.Rate(new RateRequest(_visible.Id, 1)));
        Assert.IsType<NoContentResult>(c.MyList(new MyListRequest(_visible.Id, true)));
        Assert.Equal(1, _ts.Store.Read(d => d.Ratings[StoreData.UserItemKey(Http.User, _visible.Id)]));
        Assert.Single(_ts.Store.Read(d => d.MyList.ToList()));

        var (status, body, _) = await Http.Render(c.GetItem(_visible.Id));
        Assert.Equal(200, status);
        Assert.Contains("\"myRating\":1", body);
        Assert.Contains("\"inMyList\":true", body);
    }

    [Fact]
    public void WithoutAUserClaim_EveryPerUserEndpoint_Is401()
    {
        var c = Controller().As(Guid.Empty);
        Assert.IsType<UnauthorizedResult>(c.GetHome());
        Assert.IsType<UnauthorizedResult>(c.Rate(new RateRequest(_visible.Id, 1)));
        Assert.IsType<UnauthorizedResult>(c.MyList(new MyListRequest(_visible.Id, true)));
        Assert.IsType<UnauthorizedResult>(c.GetItem(_visible.Id));
        Assert.IsType<UnauthorizedResult>(c.MyServer());
    }

    [Fact]
    public async Task Home_WhenTheLibraryFails_Is500_NotAnEmpty200_AndLeaksNothing()
    {
        // B-16: a broken library must be distinguishable from an empty one.
        var result = Controller(new ThrowingCatalog()).GetHome();
        var (status, body, _) = await Http.Render(result);

        Assert.Equal(500, status);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("C:\\", body);
        Assert.Contains("Please try again", body);
    }

    [Fact]
    public async Task MyServerAndItem_WhenTheLibraryFails_Are500()
    {
        Assert.Equal(500, (await Http.Render(Controller(new ThrowingCatalog()).MyServer())).Status);
        Assert.Equal(500, (await Http.Render(Controller(new ThrowingCatalog()).GetItem(Guid.NewGuid()))).Status);
    }
}

public class ComingSoonRowTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly Guid _u = Guid.NewGuid();

    public ComingSoonRowTests()
    {
        // Enough unseen titles for "Top Picks" (order 1) and "Recently Added" (order 10) so the Coming Soon row (order 5) sits between them.
        for (var i = 0; i < 8; i++)
        {
            _catalog.Items.Add(Make.Item("Title" + i, 100 + i, genres: new[] { "Drama" }));
        }
    }

    public void Dispose() => _ts.Dispose();

    private void SeedEntries(params int[] tmdbIds)
        => _ts.Store.Write(d => d.ComingSoon[_u.ToString("N")] = tmdbIds
            .Select(id => new ComingSoonEntry { TmdbId = id, MediaType = "movie", Title = "Soon" + id, PosterPath = "/p.jpg", Score = id, ReleaseDate = Dates.Soon })
            .ToList());

    private HomeResponse Home(string? tmdbKey = "k")
        => new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, new FakeConfig { Current = new PluginConfiguration { TmdbApiKey = tmdbKey ?? string.Empty } })
            .GetHome(_u);

    [Fact]
    public void ComingSoonRow_IsInsertedBetweenTrendingAndMyList_InContractOrder()
    {
        SeedEntries(1, 2, 3, 4);
        var ids = Home().Rows.Select(r => r.Id).ToList();

        Assert.Contains("comingsoon", ids);
        Assert.True(ids.IndexOf("toppicks") < ids.IndexOf("comingsoon"));
        Assert.True(ids.IndexOf("comingsoon") < ids.IndexOf("recent"));
        var row = Home().Rows.Single(r => r.Id == "comingsoon");
        Assert.Equal("comingsoon", row.Type);
        Assert.Equal(4, row.ComingSoon!.Count);
        Assert.Equal(4, row.ComingSoon[0].TmdbId);   // best score first
        Assert.Empty(row.Items);
    }

    [Fact]
    public void ComingSoonRow_NeedsAtLeastThreeCards_AndATmdbKey()
    {
        SeedEntries(1, 2);
        Assert.DoesNotContain(Home().Rows, r => r.Id == "comingsoon");

        SeedEntries(1, 2, 3);
        Assert.DoesNotContain(Home(tmdbKey: null).Rows, r => r.Id == "comingsoon");
        Assert.Contains(Home().Rows, r => r.Id == "comingsoon");
    }

    [Fact]
    public void ComingSoonRow_DropsTitlesAlreadyInTheLibrary_AndHidesNotForMe()
    {
        // B-22: the Home row and GET /ComingSoon use one rule.
        SeedEntries(100, 2, 3, 4, 5); // tmdb 100 is "Title0" in the library
        _ts.Store.Write(d => d.Votes.Add(new VoteEntry { UserId = _u, TmdbId = 5, MediaType = "movie", Vote = -1, At = DateTime.UtcNow }));

        var cards = Home().Rows.Single(r => r.Id == "comingsoon").ComingSoon!;
        Assert.Equal(new[] { 4, 3, 2 }, cards.Select(c => c.TmdbId).ToArray());
    }

    [Fact]
    public async Task EndpointAndHomeRow_ShowTheSameCards()
    {
        SeedEntries(100, 2, 3, 4, 5);
        _ts.Store.Write(d => d.Votes.Add(new VoteEntry { UserId = _u, TmdbId = 5, MediaType = "movie", Vote = -1, At = DateTime.UtcNow }));
        var home = Home().Rows.Single(r => r.Id == "comingsoon").ComingSoon!.Select(c => c.TmdbId).ToArray();

        var controller = new DiscoveryController(
            _ts.Store, _catalog, new VoteService(_ts.Store), new NlSearch(_ts.Store, _catalog, new FakeOllama()),
            new FakeConfig(), new FakeTmdbClient(), NullLogger<DiscoveryController>.Instance).As(_u);
        var (_, body, _) = await Http.Render(controller.ComingSoon());
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var endpoint = doc.RootElement.GetProperty("cards").EnumerateArray().Select(e => e.GetProperty("tmdbId").GetInt32()).ToArray();

        Assert.Equal(home, endpoint);
    }

    [Fact]
    public void RowIds_AreUnique_AndGenreRowsDoNotCollide()
    {
        // B-36 (server): "Sci-Fi" and "Sci Fi" must not both become "genre-sci-fi".
        var used = new HashSet<string>();
        var a = Recs.RecEngine.UniqueGenreId("Sci-Fi", used);
        var b = Recs.RecEngine.UniqueGenreId("Sci Fi", used);
        var c = Recs.RecEngine.UniqueGenreId("SCI-FI", used);
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.Equal("genre-sci-fi", a);
    }
}
