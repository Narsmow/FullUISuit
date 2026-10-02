using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;
using Jellyfin.Plugin.FullUI.Ops;
using Jellyfin.Plugin.FullUI.Recs;
using Jellyfin.Plugin.FullUI.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>
/// Contract fixtures: <c>docs/contract-fixtures/*.json</c> are real responses produced by the real controllers and services over a
/// fixed household, serialized exactly like production (<see cref="SafeApi.Json"/>). With <c>FULLUI_UPDATE_FIXTURES=1</c> the test
/// rewrites the files; otherwise it FAILS when a committed file differs from what the code produces now, so a client mock can never
/// drift from the server. Clock, ids and dates are fixed; the output is deterministic.
/// </summary>
public class ContractFixtureTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FixtureWorld _w;

    public ContractFixtureTests()
    {
        _w = new FixtureWorld(_ts.Store);
    }

    public void Dispose() => _ts.Dispose();

    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Renders an action result through MVC like production does and returns "status + indented JSON body".</summary>
    private static async Task<string> Body(IActionResult result, Action<JsonNode>? normalize = null)
    {
        var (status, body, _) = await Http.Render(result);
        Assert.True(body.Length > 0, "fixture source must have a JSON body (status " + status + ")");
        return Format(body, normalize);
    }

    private static string Format(string json, Action<JsonNode>? normalize = null)
    {
        var node = JsonNode.Parse(json)!;
        normalize?.Invoke(node);
        return node.ToJsonString(Pretty).Replace("\r\n", "\n") + "\n";
    }

    private static string Format(object value) => Format(JsonSerializer.Serialize(value, Pretty));

    public static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "api-contract.md")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "docs", "contract-fixtures");
    }

    private HomeController HomeCtl(ICatalog? catalog = null, Guid? user = null)
        => new HomeController(catalog is null ? _w.Home : new HomeService(_ts.Store, catalog, NullLogger<HomeService>.Instance, _w.Config), _ts.Store, catalog ?? _w.Catalog, Stub.Make<ILibraryManager>(), Stub.Make<IUserManager>(), Stub.Make<IUserDataManager>(), NullLogger<HomeController>.Instance).As(user ?? FixtureWorld.Viewer);

    private async Task<SortedDictionary<string, string>> Generate()
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var viewer = FixtureWorld.Viewer;
        using var _ = Clock.Fix(FixtureWorld.Now);
        ErrorLog.Clear();
        WebInjection.Status.Record(true, "ok");

        // ---- Status
        files["status.json"] = await Body(new FullUIController(Stub.Make<System.Net.Http.IHttpClientFactory>(), _w.Config).As(viewer).Status());

        // ---- Home and the things cut from it
        var home = HomeCtl();
        files["home.json"] = await Body(home.GetHome());
        files["my-server.json"] = await Body(home.MyServer());
        files["item.json"] = await Body(home.GetItem(FixtureWorld.Id(8)));      // Interstellar: resumable movie

        // ---- Coming Soon, New & Popular, Reminders
        var discovery = _w.Discovery();
        files["coming-soon.json"] = await Body(discovery.ComingSoon());
        files["notifications.json"] = await Body(discovery.Notifications());
        var np = _w.NewPopularCtl();
        files["new-popular.json"] = await Body(np.NewPopular());
        files["reminders.json"] = await Body(np.Reminders());

        // ---- Onboarding (a brand-new user is eligible; the household viewer is not)
        files["onboarding.json"] = await Body(_w.OnboardingCtl(FixtureWorld.NewUser).Get(false));
        files["onboarding-not-eligible.json"] = await Body(_w.OnboardingCtl(viewer).Get(false));

        // ---- Search: keyword, semantic, typeahead
        files["search-keyword.json"] = await Body(await discovery.Search("space", default));
        files["search-semantic.json"] = await Body(await _w.Discovery(semantic: true).Search("slow thoughtful science fiction about time", default));
        files["search-suggest.json"] = await Body(_w.SuggestCtl().Suggest("blde runer"));
        files["search-suggest-people.json"] = await Body(_w.SuggestCtl().Suggest("ryan gos"));

        // ---- Events: the request the web client sends, and the response
        var request = new
        {
            events = new object[]
            {
                new { type = "rowShown", rowType = "toppicks", at = "2026-06-01T11:58:00Z" },
                new { type = "cardExpanded", rowType = "toppicks", itemId = FixtureWorld.Id(6).ToString("N"), at = "2026-06-01T11:58:05Z" },
                new { type = "cardClicked", rowType = "toppicks", itemId = FixtureWorld.Id(6).ToString("N"), at = "2026-06-01T11:58:09Z" },
                new { type = "playStarted", rowType = "toppicks", itemId = FixtureWorld.Id(6).ToString("N"), at = "2026-06-01T11:58:12Z" },
                new { type = "trailerViewed", itemId = FixtureWorld.Id(6).ToString("N"), at = 1780315093000L },
                new { type = "searchIssued", query = "space", at = "2026-06-01T11:59:00Z" },
                new { type = "searchClicked", query = "space", itemId = FixtureWorld.Id(6).ToString("N"), at = "2026-06-01T11:59:04Z" },
            },
        };
        var requestJson = JsonSerializer.Serialize(request, Pretty);
        files["events-request.json"] = Format(requestJson);
        var parsed = JsonSerializer.Deserialize<EventsRequest>(requestJson, SafeApi.CamelCase);
        var metrics = _w.MetricsCtl(viewer);
        files["events-response.json"] = await Body(metrics.PostEvents(parsed));

        // ---- Admin
        files["admin-metrics.json"] = await Body(_w.MetricsCtl(viewer, seedMore: true).Metrics(30));
        files["admin-health.json"] = await Body(_w.AdminOpsCtl().Health(), NormalizeHealth);

        // ---- Title details (series and movie)
        files["item-details-series.json"] = await Body(_w.DetailsCtl().Details(FixtureWorld.Id(31)));
        files["item-details-movie.json"] = await Body(_w.DetailsCtl().Details(FixtureWorld.Id(8)));

        // ---- Error shapes
        // RFC 7807 problem, 4xx (an out-of-range rating) and 5xx (the library cannot be read).
        files["error-problem-4xx.json"] = await Body(HomeCtl().Rate(new RateRequest(FixtureWorld.Id(8), 9)));
        files["error-problem-5xx.json"] = await Body(HomeCtl(new ThrowingCatalog()).GetHome());
        // { message } shape, 4xx (too many events in one call) and 5xx (a search that failed inside).
        var tooMany = new EventsRequest(Enumerable.Range(0, EventValidator.MaxBatch + 1).Select(_ => new ClientEvent("rowShown", "toppicks", null, null, null)).ToArray());
        files["error-message-4xx.json"] = await Body(_w.MetricsCtl(viewer).PostEvents(tooMany));
        files["error-message-5xx.json"] = await Body(await _w.Discovery(catalog: new ThrowingCatalog()).Search("space", default));
        return files;
    }

    /// <summary>The health page reports the plugin's own version and real file sizes; pin them so a version bump or a slow disk flush cannot change the fixture.</summary>
    private static void NormalizeHealth(JsonNode node)
    {
        node["pluginVersion"] = "1.0.0";
        var sizes = new Dictionary<string, long> { ["store.json"] = 48213, ["embeddings.json"] = 3120, ["events.jsonl"] = 912 };
        foreach (var f in node["files"]!.AsArray())
        {
            f!["bytes"] = sizes[f["name"]!.GetValue<string>()];
        }
    }

    [Fact]
    public async Task Committed_fixtures_match_what_the_server_code_produces()
    {
        var generated = await Generate();
        var dir = FixtureDir();
        if (Environment.GetEnvironmentVariable("FULLUI_UPDATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(dir);
            foreach (var stale in Directory.GetFiles(dir, "*.json").Where(f => !generated.ContainsKey(Path.GetFileName(f))))
            {
                File.Delete(stale);
            }

            foreach (var (name, text) in generated)
            {
                File.WriteAllText(Path.Combine(dir, name), text, new UTF8Encoding(false));
            }

            return;
        }

        var problems = new List<string>();
        foreach (var (name, text) in generated)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path))
            {
                problems.Add($"{name}: missing");
            }
            else if (File.ReadAllText(path).Replace("\r\n", "\n") != text)
            {
                problems.Add($"{name}: differs from what the code produces");
            }
        }

        problems.AddRange(Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).Where(n => !generated.ContainsKey(n!)).Select(n => $"{n}: no longer produced")
            : Array.Empty<string>());
        Assert.True(problems.Count == 0, "Contract fixtures are out of date (" + string.Join("; ", problems) + "). Re-run with FULLUI_UPDATE_FIXTURES=1, review the diff and commit it together with the web changes it implies.");
    }

    [Fact]
    public async Task Fixtures_are_deterministic_and_camelCase()
    {
        var a = await Generate();
        var b = await GenerateFresh();
        Assert.Equal(a.Keys, b.Keys);
        foreach (var k in a.Keys)
        {
            Assert.Equal(a[k], b[k]);
            Http.AssertCamelCase(a[k]);
        }
    }

    private async Task<SortedDictionary<string, string>> GenerateFresh()
    {
        using var other = new TempStore();
        var t = new ContractFixtureTests(other);
        return await t.Generate();
    }

    private ContractFixtureTests(TempStore ts)
    {
        _ts = ts;
        _w = new FixtureWorld(ts.Store);
    }
}
