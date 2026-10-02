using System.Text.Json;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Configuration;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Metrics;
using Jellyfin.Plugin.FullUI.Ops;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class StoreMigrationTests : IDisposable
{
    private readonly DiskStore _disk = new();

    public void Dispose() => _disk.Dispose();

    private void WriteLegacy(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_disk.StoreFile)!);
        File.WriteAllText(_disk.StoreFile, json);
    }

    private const string Legacy = """
        {"Signals":[{"UserId":"aaaaaaaa-0000-0000-0000-000000000001","ItemId":"bbbbbbbb-0000-0000-0000-000000000001","IsEpisode":false,"At":"2026-09-01T10:00:00Z","Completion":1,"Completed":true}],
         "Ratings":{"aaaaaaaa000000000000000000000001|bbbbbbbb000000000000000000000001":2},
         "MyList":["aaaaaaaa000000000000000000000001|bbbbbbbb000000000000000000000001"],
         "Votes":[{"UserId":"aaaaaaaa-0000-0000-0000-000000000001","TmdbId":5,"MediaType":"movie","Vote":1,"Title":"T","At":"2026-09-01T10:00:00Z"}],
         "Statuses":{"movie:5":{"Status":"Requested","Note":"","UpdatedAt":"2026-09-01T10:00:00Z"}},
         "Notifications":[],"ComingSoon":{},"TrailerKeys":{"movie:5":"abc"},"BackfilledUsers":["aaaaaaaa000000000000000000000001"],"RowTitles":{"Drama":"Quiet Dramas"}}
        """;

    [Fact]
    public void A_file_without_a_version_is_version_1_and_is_upgraded_without_losing_anything()
    {
        WriteLegacy(Legacy);
        using (var s = _disk.Open(TimeSpan.FromHours(1)))
        {
            Assert.Equal(StoreMigrations.CurrentVersion, s.Read(d => d.Version));
            Assert.Equal(2, s.Read(d => d.Ratings.Values.Single()));
            Assert.Single(s.Read(d => d.MyList.ToList()));
            Assert.Equal("T", s.Read(d => d.Votes.Single().Title));
            Assert.Equal("abc", s.Read(d => d.TrailerKeys["movie:5"]));
            Assert.Equal("Quiet Dramas", s.Read(d => d.RowTitles["Drama"]));
            Assert.True(s.Read(d => d.Signals.Single().Completed));
            Assert.Empty(s.Read(d => d.Reminders.ToList()));        // new collections exist and are empty
            Assert.Empty(s.Read(d => d.HiddenContinue.ToList()));
            Assert.Empty(s.Read(d => d.Onboarding.ToList()));
        }

        Assert.True(File.Exists(_disk.StoreFile + ".v1.bak"), "a backup of the old file is kept");
        Assert.Contains("\"Version\":2", File.ReadAllText(_disk.StoreFile));
        Assert.Contains("\"Votes\"", File.ReadAllText(_disk.StoreFile + ".v1.bak"));
        Assert.DoesNotContain("\"Version\"", File.ReadAllText(_disk.StoreFile + ".v1.bak"));

        using var again = _disk.Open();
        Assert.Equal(2, again.Read(d => d.Version));
        Assert.Equal(2, again.Read(d => d.Ratings.Values.Single()));
    }

    [Fact]
    public void Migrate_reports_what_it_did()
    {
        var d = new StoreData();
        var r = StoreMigrations.Migrate(d);
        Assert.Equal(1, r.From);
        Assert.Equal(StoreMigrations.CurrentVersion, r.To);
        Assert.True(r.Changed);
        Assert.NotEmpty(r.Applied);
        var second = StoreMigrations.Migrate(d);
        Assert.False(second.Changed);
        Assert.Empty(second.Applied);
    }

    [Fact]
    public void A_file_from_a_newer_version_is_backed_up_and_still_readable()
    {
        WriteLegacy("""{"Version":99,"Ratings":{"a|b":1},"SomethingNew":{"x":1}}""");
        using var s = _disk.Open(TimeSpan.FromHours(1));
        Assert.Equal(99, s.Read(d => d.Version));
        Assert.Equal(1, s.Read(d => d.Ratings["a|b"]));
        Assert.True(File.Exists(_disk.StoreFile + ".v99.bak"));
        Assert.Contains("SomethingNew", File.ReadAllText(_disk.StoreFile + ".v99.bak"));
    }

    [Fact]
    public void Explicit_nulls_in_a_hand_edited_file_do_not_crash_the_plugin()
    {
        WriteLegacy("""{"Version":1,"Reminders":null,"Ratings":null,"HiddenContinue":null,"Votes":[]}""");
        using var s = _disk.Open(TimeSpan.FromHours(1));
        s.Write(d =>
        {
            d.Ratings["k"] = 1;
            d.Reminders.Add(new ReminderEntry { Title = "x" });
        });
        Assert.Equal(1, s.Read(d => d.Ratings.Count));
    }

    [Fact]
    public void A_brand_new_store_is_current_and_writes_nothing_until_something_changes()
    {
        using var s = _disk.Open(TimeSpan.FromHours(1));
        Assert.Equal(StoreMigrations.CurrentVersion, s.Read(d => d.Version));
        s.Flush();
        Assert.False(File.Exists(_disk.StoreFile));
    }

    [Fact]
    public void The_new_collections_survive_a_restart()
    {
        var u = Guid.NewGuid();
        using (var s = _disk.Open())
        {
            s.Write(d =>
            {
                d.Reminders.Add(new ReminderEntry { UserId = u, TmdbId = 3, Title = "R", ReleaseDate = "2027-01-01" });
                d.Onboarding["x"] = new OnboardingState { Completed = true, Genres = new() { "Drama" } };
                d.HiddenContinue.Add("a|b");
                d.TaskRuns.Add(new TaskRunRecord { Key = "k", Name = "n", Outcome = "Success" });
                d.RowTitleStamps["Drama"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            });
        }

        using var re = _disk.Open();
        Assert.Equal("R", re.Read(d => d.Reminders.Single().Title));
        Assert.Equal("Drama", re.Read(d => d.Onboarding["x"].Genres.Single()));
        Assert.Contains("a|b", re.Read(d => d.HiddenContinue.ToList()));
        Assert.Single(re.Read(d => d.TaskRuns.ToList()));
        Assert.Equal(2026, re.Read(d => d.RowTitleStamps["Drama"].Year));
    }
}

public class ErrorLogTests
{
    [Fact]
    public void Keeps_only_the_last_50_and_never_stores_secrets_urls_or_paths()
    {
        ErrorLog.Clear();
        for (var i = 0; i < 70; i++)
        {
            ErrorLog.Add("problem number " + i);
        }

        var recent = ErrorLog.Recent();
        Assert.Equal(ErrorLog.Capacity, recent.Count);
        Assert.Equal("problem number 69", recent[0].Message);       // newest first
        Assert.DoesNotContain(recent, e => e.Message == "problem number 5");

        ErrorLog.Clear();
        ErrorLog.Add("TMDB failed at https://api.themoviedb.org/3/movie?api_key=abcdef0123456789abcdef0123456789 for /media/movies/Alien/file.mkv and C:\\Media\\x.mkv, token=SUPERSECRETVALUE");
        var text = ErrorLog.Recent().Single().Message;
        Assert.DoesNotContain("themoviedb.org", text);
        Assert.DoesNotContain("abcdef0123456789", text);
        Assert.DoesNotContain("/media/movies", text);
        Assert.DoesNotContain("C:\\Media", text);
        Assert.DoesNotContain("SUPERSECRETVALUE", text);
        ErrorLog.Clear();
    }

    [Fact]
    public void Repeats_are_collapsed()
    {
        ErrorLog.Clear();
        ErrorLog.Add("same");
        ErrorLog.Add("same");
        ErrorLog.Add("other");
        ErrorLog.Add("same");
        Assert.Equal(3, ErrorLog.Recent().Count);
        ErrorLog.Clear();
    }

    [Fact]
    public async Task A_failing_endpoint_leaves_a_friendly_message_in_the_log()
    {
        ErrorLog.Clear();
        using var ts = new TempStore();
        var c = new DiscoveryController(ts.Store, new ThrowingCatalog(), new VoteService(ts.Store), new NlSearch(ts.Store, new FakeCatalog(), new FakeOllama()), new FakeConfig(), new FakeTmdbClient(), NullLogger<DiscoveryController>.Instance).As();
        await Http.Render(c.ComingSoon());
        Assert.Contains(ErrorLog.Recent(), e => e.Message.Contains("Coming Soon", StringComparison.Ordinal) && !e.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        ErrorLog.Clear();
    }
}

public class TaskRunTests : IDisposable
{
    private readonly TempStore _ts = new();

    public void Dispose() => _ts.Dispose();

    [Fact]
    public void Runs_are_kept_per_task_bounded_and_the_latest_wins()
    {
        var log = new TaskRunLog(_ts.Store);
        var t0 = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 30; i++)
        {
            log.Record("FullUIA", "A", t0.AddDays(i), t0.AddDays(i).AddMinutes(1), TaskOutcome.Success, "ok " + i);
        }

        log.Record("FullUIB", "B", t0, t0.AddMinutes(1), TaskOutcome.Failed, "went wrong");
        Assert.Equal(TaskRunLog.KeepPerKey, _ts.Store.Read(d => d.TaskRuns.Count(r => r.Key == "FullUIA")));
        var latest = log.Latest();
        Assert.Equal(2, latest.Count);
        Assert.Equal("ok 29", latest.Single(r => r.Key == "FullUIA").Message);
        Assert.Equal(TaskOutcome.Failed, latest.Single(r => r.Key == "FullUIB").Outcome);
    }

    [Fact]
    public void The_recorder_stores_finished_fullui_tasks_attaches_notes_and_ignores_other_tasks()
    {
        ErrorLog.Clear();
        var runs = new TaskRunLog(_ts.Store);
        var rec = new TaskRunRecorderService(Stub.Make<ITaskManager>(), runs, NullLogger<TaskRunRecorderService>.Instance);
        var worker = Stub.Make<IScheduledTaskWorker>();
        TaskResult R(string key, TaskCompletionStatus st) => new() { Key = key, Name = "Name " + key, Status = st, StartTimeUtc = DateTime.UtcNow.AddMinutes(-1), EndTimeUtc = DateTime.UtcNow };

        rec.OnCompleted(null, new TaskCompletionEventArgs(worker, R("FullUIOk", TaskCompletionStatus.Completed)));
        runs.Note("FullUIProb", "3 of 5 users failed", problem: true);
        rec.OnCompleted(null, new TaskCompletionEventArgs(worker, R("FullUIProb", TaskCompletionStatus.Completed)));
        rec.OnCompleted(null, new TaskCompletionEventArgs(worker, R("FullUIFail", TaskCompletionStatus.Failed)));
        rec.OnCompleted(null, new TaskCompletionEventArgs(worker, R("FullUICancel", TaskCompletionStatus.Cancelled)));
        rec.OnCompleted(null, new TaskCompletionEventArgs(worker, R("RefreshLibrary", TaskCompletionStatus.Completed)));

        var by = runs.Latest().ToDictionary(r => r.Key);
        Assert.Equal(TaskOutcome.Success, by["FullUIOk"].Outcome);
        Assert.Equal(TaskOutcome.Problem, by["FullUIProb"].Outcome);
        Assert.Equal("3 of 5 users failed", by["FullUIProb"].Message);
        Assert.Equal(TaskOutcome.Failed, by["FullUIFail"].Outcome);
        Assert.Equal(TaskOutcome.Cancelled, by["FullUICancel"].Outcome);
        Assert.False(by.ContainsKey("RefreshLibrary"));
        Assert.NotEmpty(ErrorLog.Recent()); // failed and problem runs show up in the recent-problems list
        ErrorLog.Clear();
    }

    [Fact]
    public void Task_rows_are_coloured_in_plain_english()
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        TaskRunRecord Run(string outcome, double hoursAgo) => new() { Key = "k", Name = "n", Outcome = outcome, StartUtc = now.AddHours(-hoursAgo), EndUtc = now.AddHours(-hoursAgo) };

        Assert.Equal("grey", HealthService.Row("k", "n", true, null, now).Color);
        Assert.Equal("green", HealthService.Row("k", "n", true, Run(TaskOutcome.Success, 5), now).Color);
        Assert.Equal("amber", HealthService.Row("k", "n", true, Run(TaskOutcome.Success, 24 * 5), now).Color);   // daily task silent for days
        Assert.Equal("green", HealthService.Row("k", "n", false, Run(TaskOutcome.Success, 24 * 5), now).Color);
        Assert.Equal("amber", HealthService.Row("k", "n", true, Run(TaskOutcome.Problem, 1), now).Color);
        Assert.Equal("red", HealthService.Row("k", "n", true, Run(TaskOutcome.Failed, 1), now).Color);
        Assert.Equal("Has not run yet", HealthService.Row("k", "n", true, null, now).Status);
    }
}

public class OpsControllerTests : IDisposable
{
    private readonly TempStore _ts = new();
    private readonly FakeCatalog _catalog = new();
    private readonly FakeConfig _config = new() { Current = new() { TmdbApiKey = "SECRET-TMDB-KEY-0123456789abcdef" } };
    private readonly FakeUsers _users = new();
    private readonly FakeHome _home = new();
    private readonly Guid _bob = Guid.NewGuid();

    public OpsControllerTests()
    {
        _users.Names[Http.User] = "ann";
        _users.Names[_bob] = "bob";
    }

    public void Dispose() => _ts.Dispose();

    private InteractionLog Log() => new(_ts.Store, NullLogger<InteractionLog>.Instance);

    private AdminOpsController AdminOps()
    {
        var log = Log();
        var health = new HealthService(_ts.Store, _catalog, new TaskRunLog(_ts.Store), _config, new FakeTmdbClient(), new FakeOllama(), log);
        return new AdminOpsController(health, new DataPortability(_ts.Store, _catalog, _users, log, _home), NullLogger<AdminOpsController>.Instance).As();
    }

    private (CatalogItem A, CatalogItem B) Seed()
    {
        var a = Make.Item("Alpha", 1);
        var b = Make.Item("Beta", 2);
        _catalog.Items.AddRange(new[] { a, b });
        _ts.Store.Write(d =>
        {
            d.Ratings[StoreData.UserItemKey(Http.User, a.Id)] = 2;
            d.Ratings[StoreData.UserItemKey(_bob, b.Id)] = -1;
            d.MyList.Add(StoreData.UserItemKey(Http.User, b.Id));
            d.Votes.Add(new VoteEntry { UserId = Http.User, TmdbId = 9, MediaType = "movie", Vote = 1, Title = "Wanted", At = DateTime.UtcNow });
            d.Statuses["movie:9"] = new RequestStatusEntry { Status = "Getting it", Note = "soon", UpdatedAt = DateTime.UtcNow };
            d.Notifications.Add(new NotificationEntry { UserId = Http.User, Text = "hello", At = DateTime.UtcNow });
            d.Reminders.Add(new ReminderEntry { UserId = Http.User, TmdbId = 9, Title = "Wanted", ReleaseDate = "2027-01-01", CreatedAt = DateTime.UtcNow });
            d.HiddenContinue.Add(StoreData.UserItemKey(Http.User, a.Id));
            d.Onboarding[Http.User.ToString("N")] = new OnboardingState { Completed = true, Genres = new() { "Drama" } };
        });
        return (a, b);
    }

    private static ExportDocument Roundtrip(string json)
        => JsonSerializer.Deserialize<ExportDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Health_is_plain_camelCase_json_and_never_contains_secrets()
    {
        Seed();
        ErrorLog.Clear();
        ErrorLog.Add("TMDB said no for api_key=SECRET-TMDB-KEY-0123456789abcdef");
        var (status, body, _) = await Http.Render(AdminOps().Health());
        Assert.Equal(200, status);
        Http.AssertCamelCase(body);
        Assert.DoesNotContain("SECRET-TMDB-KEY", body);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("10.11.6 and later", root.GetProperty("supportedJellyfin").GetString());
        Assert.Equal(2, root.GetProperty("catalogItems").GetInt32());
        Assert.True(root.GetProperty("tasks").GetArrayLength() >= 6);
        Assert.Contains(root.GetProperty("tasks").EnumerateArray(), t => t.GetProperty("key").GetString() == "FullUIRebuildRecs" && t.GetProperty("color").GetString() == "grey");
        Assert.Equal(3, root.GetProperty("files").GetArrayLength());
        Assert.True(root.GetProperty("counts").GetProperty("ratings").GetInt32() >= 2);
        Assert.NotEmpty(root.GetProperty("recentErrors").EnumerateArray().ToList());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("summary").GetString()));
        ErrorLog.Clear();
    }

    [Fact]
    public async Task Health_survives_a_library_that_cannot_be_read()
    {
        var log = Log();
        var health = new HealthService(_ts.Store, new ThrowingCatalog(), new TaskRunLog(_ts.Store), _config, new FakeTmdbClient(), new FakeOllama(), log);
        var c = new AdminOpsController(health, new DataPortability(_ts.Store, _catalog, _users, log, _home), NullLogger<AdminOpsController>.Instance).As();
        var (status, body, _) = await Http.Render(c.Health());
        Assert.Equal(200, status);
        Assert.DoesNotContain("secret library failure", body);
    }

    [Fact]
    public async Task Export_is_a_json_download_with_everything_and_can_be_limited_to_one_user()
    {
        var (a, _) = Seed();
        var all = AdminOps().Export(null);
        var file = Assert.IsType<FileContentResult>(all);
        Assert.Equal("application/json", file.ContentType);
        Assert.StartsWith("fullui-export-", file.FileDownloadName);
        var doc = Roundtrip(System.Text.Encoding.UTF8.GetString(file.FileContents));
        Assert.Equal("fullui-export", doc.Format);
        Assert.Equal(2, doc.Users!.Count);
        var ann = doc.Users[Http.User.ToString("N")];
        Assert.Equal("ann", ann.Name);
        Assert.Equal(2, ann.Ratings![a.Id.ToString("N")]);
        Assert.Single(ann.Votes!);
        Assert.Single(ann.Reminders!);
        Assert.Single(ann.Notifications!);
        Assert.True(ann.Onboarding!.Completed);
        Assert.Equal("Getting it", doc.Statuses!["movie:9"].Status);

        var one = Assert.IsType<FileContentResult>(AdminOps().Export(Http.User.ToString("N")));
        var oneDoc = Roundtrip(System.Text.Encoding.UTF8.GetString(one.FileContents));
        Assert.Single(oneDoc.Users!);
        Assert.Empty(oneDoc.Statuses!);          // request statuses belong to the server, not to a user
        var (status, _, _) = await Http.Render(AdminOps().Export("not a user"));
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task Import_defaults_to_a_dry_run_and_only_applies_when_asked()
    {
        var (a, b) = Seed();
        var json = System.Text.Encoding.UTF8.GetString(((FileContentResult)AdminOps().Export(null)).FileContents);

        // A second, empty server with the same users and library.
        using var other = new TempStore();
        var log2 = new InteractionLog(other.Store, NullLogger<InteractionLog>.Instance);
        var home2 = new FakeHome();
        var data2 = new DataPortability(other.Store, _catalog, _users, log2, home2);

        var dry = data2.Import(Roundtrip(json), dryRun: true);
        Assert.True(dry.Valid);
        Assert.True(dry.DryRun);
        Assert.Equal(2, dry.Users);
        Assert.Equal(2, dry.Ratings);
        Assert.Equal(1, dry.Votes);
        Assert.Equal(1, dry.Requests);
        Assert.Empty(other.Store.Read(d => d.Ratings));
        Assert.Empty(home2.Invalidated);

        var real = data2.Import(Roundtrip(json), dryRun: false);
        Assert.False(real.DryRun);
        Assert.Equal(2, other.Store.Read(d => d.Ratings[StoreData.UserItemKey(Http.User, a.Id)]));
        Assert.Equal(-1, other.Store.Read(d => d.Ratings[StoreData.UserItemKey(_bob, b.Id)]));
        Assert.Contains(StoreData.UserItemKey(Http.User, b.Id), other.Store.Read(d => d.MyList.ToList()));
        Assert.Equal("Wanted", other.Store.Read(d => d.Votes.Single().Title));
        Assert.Equal("Getting it", other.Store.Read(d => d.Statuses["movie:9"].Status));
        Assert.Single(other.Store.Read(d => d.Reminders.ToList()));
        Assert.Single(other.Store.Read(d => d.Notifications.ToList()));
        Assert.NotEmpty(home2.Invalidated);

        // Importing the same file again changes nothing (no duplicate votes / notifications / reminders).
        data2.Import(Roundtrip(json), dryRun: false);
        Assert.Single(other.Store.Read(d => d.Votes.ToList()));
        Assert.Single(other.Store.Read(d => d.Notifications.ToList()));
        Assert.Single(other.Store.Read(d => d.Reminders.ToList()));
    }

    [Fact]
    public void Import_is_strict_unknown_users_titles_and_bad_values_are_skipped_and_reported()
    {
        var (a, _) = Seed();
        var data = new DataPortability(_ts.Store, _catalog, _users, Log(), _home);
        var ghost = Guid.NewGuid().ToString("N");
        var doc = new ExportDocument
        {
            Format = "fullui-export",
            Version = 1,
            Users = new()
            {
                [ghost] = new ExportUser { Ratings = new() { [a.Id.ToString("N")] = 1 } },
                [Http.User.ToString("N")] = new ExportUser
                {
                    Ratings = new() { [a.Id.ToString("N")] = 1, [Guid.NewGuid().ToString("N")] = 1, ["junk"] = 1, [_catalog.Items[1].Id.ToString("N")] = 7 },
                    Votes = new() { new ExportVote { TmdbId = -4, MediaType = "movie", Vote = 1 }, new ExportVote { TmdbId = 4, MediaType = "podcast", Vote = 1 }, new ExportVote { TmdbId = 4, MediaType = "tv", Vote = 5 } },
                    Notifications = new() { new ExportNotification { Id = Guid.NewGuid(), Text = new string('x', 5000) } },
                },
            },
            Statuses = new() { ["movie:1"] = new ExportStatus { Status = "Hacked" } },
        };

        var r = data.Import(doc, dryRun: true);

        Assert.True(r.Valid);
        Assert.Equal(1, r.Users);
        Assert.Equal(1, r.Ratings);                // only the valid one
        Assert.Equal(0, r.Votes);
        Assert.Equal(0, r.Notifications);
        Assert.Equal(0, r.Requests);
        Assert.Equal(3, r.Skipped.Count);
        Assert.Contains(r.Skipped, s => s.Contains("do not exist", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Contains("not in this library", StringComparison.Ordinal));
        Assert.Contains(r.Skipped, s => s.Contains("not valid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Import_refuses_the_wrong_format_and_absurd_sizes_and_writes_nothing()
    {
        Seed();
        var before = _ts.Store.Read(d => d.Ratings.Count);
        var data = new DataPortability(_ts.Store, _catalog, _users, Log(), _home);

        Assert.False(data.Import(new ExportDocument { Format = "something-else", Version = 1 }, dryRun: false).Valid);
        Assert.False(data.Import(new ExportDocument { Format = "fullui-export", Version = 42 }, dryRun: false).Valid);
        Assert.False(data.Import(null, dryRun: false).Valid);

        var many = new Dictionary<string, ExportUser>();
        for (var i = 0; i < DataPortability.MaxUsers + 1; i++)
        {
            many[Guid.NewGuid().ToString("N")] = new ExportUser();
        }

        Assert.False(data.Import(new ExportDocument { Format = "fullui-export", Version = 1, Users = many }, dryRun: false).Valid);
        Assert.Equal(before, _ts.Store.Read(d => d.Ratings.Count));

        var (status, body, _) = await Http.Render(AdminOps().Import(new ExportDocument { Format = "nope" }));
        Assert.Equal(400, status);
        Http.AssertCamelCase(body);
        var (nullStatus, _, _) = await Http.Render(AdminOps().Import(null));
        Assert.Equal(400, nullStatus);
    }

    [Fact]
    public async Task Purge_needs_a_confirmation_token_and_deletes_only_the_chosen_user()
    {
        var (a, b) = Seed();
        var ops = AdminOps();
        var log = Log();
        log.Append(new[] { new StoredEvent(Http.User, "rowShown", "x", null, null, DateTime.UtcNow), new StoredEvent(_bob, "rowShown", "x", null, null, DateTime.UtcNow) });

        var (s1, first, _) = await Http.Render(ops.Purge(new PurgeRequest(Http.User.ToString("N"), null)));
        Assert.Equal(200, s1);
        using var d1 = JsonDocument.Parse(first);
        Assert.True(d1.RootElement.GetProperty("confirmRequired").GetBoolean());
        var token = d1.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(2, _ts.Store.Read(d => d.Ratings.Count));       // nothing deleted yet

        // A token for one target does not work for another, and a wrong token deletes nothing.
        var wrong = await Http.Render(ops.Purge(new PurgeRequest("all", token)));
        Assert.Contains("\"confirmRequired\":true", wrong.Body);
        Assert.Equal(2, _ts.Store.Read(d => d.Ratings.Count));
        var wrong2 = await Http.Render(AdminOps().Purge(new PurgeRequest(Http.User.ToString("N"), "deadbeef")));
        Assert.Contains("\"confirmRequired\":true", wrong2.Body);
        Assert.Equal(2, _ts.Store.Read(d => d.Ratings.Count));

        // A token that did not match is not spent: the right target can still use it.
        var ok = await Http.Render(ops.Purge(new PurgeRequest(Http.User.ToString("N"), token)));
        Assert.Contains("\"confirmRequired\":false", ok.Body);
        Assert.Equal(new[] { StoreData.UserItemKey(_bob, b.Id) }, _ts.Store.Read(d => d.Ratings.Keys.ToArray()));
        Assert.Empty(_ts.Store.Read(d => d.MyList.ToList()));
        Assert.Empty(_ts.Store.Read(d => d.Votes.ToList()));
        Assert.Empty(_ts.Store.Read(d => d.Reminders.ToList()));
        Assert.Empty(_ts.Store.Read(d => d.Notifications.ToList()));
        Assert.Empty(_ts.Store.Read(d => d.Onboarding.ToList()));
        Assert.Empty(_ts.Store.Read(d => d.HiddenContinue.ToList()));
        Assert.Equal(1, _ts.Store.Read(d => d.Statuses.Count));       // server-wide request statuses stay
        Assert.All(log.ReadAll(), e => Assert.Equal(_bob, e.UserId)); // usage events of that user are gone too
        Assert.Contains(Http.User, _home.Invalidated);

        // Token is single use.
        var again = await Http.Render(ops.Purge(new PurgeRequest(Http.User.ToString("N"), token)));
        Assert.Contains("\"confirmRequired\":true", again.Body);
        Assert.NotNull(a);
    }

    [Fact]
    public async Task Purge_all_clears_everything_including_the_ai_index_and_usage_log()
    {
        Seed();
        _ts.Store.WriteEmbeddings(e => e["x"] = new EmbeddingEntry { Vector = new float[] { 1 } });
        var ops = AdminOps();
        var first = await Http.Render(ops.Purge(new PurgeRequest("ALL", null)));
        using var doc = JsonDocument.Parse(first.Body);
        var token = doc.RootElement.GetProperty("token").GetString()!;
        await Http.Render(ops.Purge(new PurgeRequest("all", token)));
        Assert.Empty(_ts.Store.Read(d => d.Ratings));
        Assert.Empty(_ts.Store.Read(d => d.Statuses));
        Assert.Empty(_ts.Store.Read(d => d.Votes));
        Assert.Equal(0, _ts.Store.ReadEmbeddings(e => e.Count));
    }

    [Fact]
    public async Task Purge_rejects_a_missing_or_unreadable_target()
    {
        var ops = AdminOps();
        Assert.Equal(400, (await Http.Render(ops.Purge(null))).Status);
        Assert.Equal(400, (await Http.Render(ops.Purge(new PurgeRequest(null, null)))).Status);
        Assert.Equal(400, (await Http.Render(ops.Purge(new PurgeRequest("someone", null)))).Status);
    }
}

public class DeletedUserCleanupTests
{
    [Fact]
    public void Users_removed_from_jellyfin_lose_their_reminders_hidden_titles_and_onboarding_too()
    {
        using var ts = new TempStore();
        var gone = Guid.NewGuid();
        var here = Guid.NewGuid();
        var users = new FakeUsers();
        users.Names[here] = "here";
        ts.Store.Write(d =>
        {
            d.Reminders.Add(new ReminderEntry { UserId = gone, TmdbId = 1, Title = "x" });
            d.Reminders.Add(new ReminderEntry { UserId = here, TmdbId = 1, Title = "x" });
            d.HiddenContinue.Add(StoreData.UserItemKey(gone, Guid.NewGuid()));
            d.HiddenContinue.Add(StoreData.UserItemKey(here, Guid.NewGuid()));
            d.Onboarding[gone.ToString("N")] = new OnboardingState();
            d.Onboarding[here.ToString("N")] = new OnboardingState();
        });

        Assert.Equal(1, new RequestService(ts.Store, users, new FakeConfig()).PurgeDeletedUsers());

        Assert.Equal(here, Assert.Single(ts.Store.Read(d => d.Reminders.ToList())).UserId);
        Assert.Single(ts.Store.Read(d => d.HiddenContinue.ToList()));
        Assert.Equal(new[] { here.ToString("N") }, ts.Store.Read(d => d.Onboarding.Keys.ToArray()));
    }
}
