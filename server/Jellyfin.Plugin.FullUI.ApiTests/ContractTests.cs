using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class ContractTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();

    public void Dispose() => _ts.Dispose();

    private DiscoveryController Discovery()
        => new DiscoveryController(
            _ts.Store, _catalog, new VoteService(_ts.Store), new NlSearch(_ts.Store, _catalog, new FakeOllama()),
            new FakeConfig(), new FakeTmdbClient(), NullLogger<DiscoveryController>.Instance).As();

    private void SeedNotifications(out Guid first, out Guid second)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _ts.Store.Write(d =>
        {
            d.Notifications.Add(new NotificationEntry { Id = a, UserId = Http.User, Text = "a", At = DateTime.UtcNow });
            d.Notifications.Add(new NotificationEntry { Id = b, UserId = Http.User, Text = "b", At = DateTime.UtcNow });
            d.Notifications.Add(new NotificationEntry { UserId = Guid.NewGuid(), Text = "someone else", At = DateTime.UtcNow });
        });
        first = a;
        second = b;
    }

    private int Unread(string? text = null)
        => _ts.Store.Read(d => d.Notifications.Count(n => !n.Read && (text is null || n.Text == text)));

    [Fact]
    public void MarkRead_WithNullBody_NullIds_OrEmptyIds_MarksAllOfMyNotifications()
    {
        foreach (var request in new MarkReadRequest?[] { null, new MarkReadRequest(null), new MarkReadRequest(Array.Empty<Guid>()) })
        {
            SeedNotifications(out _, out _);
            Assert.IsType<NoContentResult>(Discovery().MarkRead(request));
            Assert.Equal(0, _ts.Store.Read(d => d.Notifications.Count(n => n.UserId == Http.User && !n.Read)));
            Assert.True(Unread("someone else") >= 1, "another user's notifications must stay unread");
            _ts.Store.Write(d => d.Notifications.Clear());
        }
    }

    [Fact]
    public void MarkRead_WithIds_MarksOnlyThose()
    {
        SeedNotifications(out var a, out _);

        Discovery().MarkRead(new MarkReadRequest(new[] { a }));

        Assert.Equal(0, Unread("a"));
        Assert.Equal(1, Unread("b"));
    }

    [Fact]
    public async Task ItemCards_CarryThePrimaryImageTag()
    {
        var item = new CatalogItem { Id = Guid.NewGuid(), Name = "Tagged", TmdbId = 1, PrimaryImageTag = "abc123def4567890" };
        var untagged = Make.Item("Untagged", 2);
        _catalog.Items.AddRange(new[] { item, untagged });

        var (_, body, _) = await Http.Render(await Discovery().Search("tagged", default));
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var cards = doc.RootElement.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal("abc123def4567890", cards.Single(c => c.GetProperty("name").GetString() == "Tagged").GetProperty("imageTag").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cards.Single(c => c.GetProperty("name").GetString() == "Untagged").GetProperty("imageTag").ValueKind);
    }

    [Fact]
    public void HomeCards_AlsoCarryTheImageTag()
    {
        var item = new CatalogItem { Id = Guid.NewGuid(), Name = "Tagged", PrimaryImageTag = "tag1" };
        var card = CardMapper.ToCard(item, Array.Empty<string>(), null, null, 0, false, new StoreData());
        Assert.Equal("tag1", card.ImageTag);
    }

    [Fact]
    public async Task Status_ReportsTrailerSwitch_AndAValidAccentOnly()
    {
        var status = new FullUIController(Stub.Make<System.Net.Http.IHttpClientFactory>()).As();
        var (_, body, _) = await Http.Render(status.Status());
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        Assert.True(doc.RootElement.GetProperty("trailersEnabled").GetBoolean());   // default on
        Assert.Matches("^#[0-9a-f]{3,8}$", doc.RootElement.GetProperty("accentColor").GetString()!);
        Assert.True(new PluginConfiguration().TrailersEnabled);
    }

    [Fact]
    public void Home_NeverSendsAnInvalidAccentColour()
    {
        var home = new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, new FakeConfig { Current = new PluginConfiguration { AccentColor = "red; background:url(x)", ServerName = "  " } });

        var response = home.GetHome(Http.User);

        Assert.Equal("#e50914", response.AccentColor);
        Assert.Equal("FullUI", response.ServerName);
    }

    [Fact]
    public void TheTopTenWindowSetting_ReachesTheRowTitle()
    {
        var movies = Enumerable.Range(0, 4).Select(i => Make.Item("Movie" + i, i + 1)).ToList();
        _catalog.Items.AddRange(movies);
        var other = Guid.NewGuid();
        _ts.Store.Write(d =>
        {
            foreach (var m in movies)
            {
                foreach (var u in new[] { Http.User, other })
                {
                    d.Signals.Add(new PlaySignal { UserId = u, ItemId = m.Id, At = DateTime.UtcNow.AddDays(-2), Completion = 1, Completed = true });
                }
            }
        });
        string Title(int days) => new HomeService(_ts.Store, _catalog, NullLogger<HomeService>.Instance, new FakeConfig { Current = new PluginConfiguration { TopTenWindowDays = days, ServerName = "Srv" } })
            .GetHome(Guid.NewGuid()).Rows.Single(r => r.Id == "top10-movies").Title;

        Assert.Equal("Top 10 Movies on Srv This Week", Title(7));
        Assert.Equal("Top 10 Movies on Srv in the Last 30 Days", Title(30));
    }

    [Fact]
    public async Task AdminInjectionStatus_IsReadableByTheSettingsPage()
    {
        var admin = new AdminController(new RequestService(_ts.Store, new FakeUsers(), new FakeConfig()), new FakeOllama(), _catalog, null!, null!, null!, NullLogger<AdminController>.Instance);

        var (status, body, _) = await Http.Render(admin.Injection());

        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        Assert.Contains("\"registered\"", body);
        Assert.Contains("\"message\"", body);
    }
}
