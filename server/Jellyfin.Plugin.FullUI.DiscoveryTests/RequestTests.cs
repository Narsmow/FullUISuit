using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class RequestTests
{
    private static VoteRequest V(int id, int vote, string type = "movie", string? title = "Title") => new(id, type, vote, title, "/p.jpg", null, "2026-01-01", "o");

    [Fact]
    public void Vote_upserts_one_row_per_user_title_and_clears_on_zero()
    {
        using var ts = new TempStore();
        var votes = new VoteService(ts.Store);
        var u = Guid.NewGuid();
        Assert.Equal(VoteResult.Ok, votes.Cast(u, V(1, 1)));
        Assert.Equal(VoteResult.Ok, votes.Cast(u, V(1, -1)));
        Assert.Single(ts.Store.Read(d => d.Votes.ToList()));
        Assert.Equal(-1, votes.MyVotes(u)["movie:1"]);
        votes.Cast(u, V(1, 1, "tv"));
        Assert.Equal(2, ts.Store.Read(d => d.Votes.Count));
        votes.Cast(u, V(1, 0));
        Assert.DoesNotContain("movie:1", votes.MyVotes(u).Keys);
    }

    [Fact]
    public void Vote_validates_input()
    {
        using var ts = new TempStore();
        var votes = new VoteService(ts.Store);
        var u = Guid.NewGuid();
        Assert.Equal(VoteResult.Invalid, votes.Cast(u, V(0, 1)));
        Assert.Equal(VoteResult.Invalid, votes.Cast(u, V(1, 2)));
        Assert.Equal(VoteResult.Invalid, votes.Cast(u, V(1, 1, "game")));
        Assert.Equal(VoteResult.Invalid, votes.Cast(Guid.Empty, V(1, 1)));
        Assert.Empty(ts.Store.Read(d => d.Votes.ToList()));
    }

    [Fact]
    public void Vote_sanitises_paths_and_creates_requested_status_once()
    {
        using var ts = new TempStore();
        var votes = new VoteService(ts.Store);
        var u = Guid.NewGuid();
        votes.Cast(u, new VoteRequest(5, "movie", 1, "T", "javascript:alert(1)", "/ok.jpg", "not a date", null));
        var v = ts.Store.Read(d => d.Votes.Single());
        Assert.Null(v.PosterPath);
        Assert.Equal("/ok.jpg", v.BackdropPath);
        Assert.Null(v.ReleaseDate);
        ts.Store.Write(d => d.Statuses["movie:5"] = new RequestStatusEntry { Status = "Getting it" });
        votes.Cast(Guid.NewGuid(), V(5, 1));
        Assert.Equal("Getting it", ts.Store.Read(d => d.Statuses["movie:5"].Status));
    }

    [Fact]
    public void Votes_are_private_per_user()
    {
        using var ts = new TempStore();
        var votes = new VoteService(ts.Store);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        votes.Cast(a, V(1, 1));
        votes.Cast(b, V(2, -1));
        Assert.Equal(new[] { "movie:1" }, votes.MyVotes(a).Keys.ToArray());
        Assert.Equal(new[] { "movie:2" }, votes.MyVotes(b).Keys.ToArray());
    }

    private static (TempStore ts, RequestService svc, VoteService votes, FakeUsers users) Setup()
    {
        var ts = new TempStore();
        var users = new FakeUsers();
        return (ts, new RequestService(ts.Store, users, new FakeConfig()), new VoteService(ts.Store), users);
    }

    [Fact]
    public void Aggregate_counts_wants_only_sorts_and_lists_voters()
    {
        var (ts, svc, votes, users) = Setup();
        using var keep = ts;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        users.Names[a] = "Ann";
        users.Names[b] = "Bob";
        users.Names[c] = "Cy";
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        votes.Cast(a, V(1, 1, title: "Popular"), t0);
        votes.Cast(b, V(1, 1, title: "Popular"), t0.AddDays(1));
        votes.Cast(c, V(1, -1, title: "Popular"), t0.AddDays(2));
        votes.Cast(c, V(2, 1, "tv", "Newest"), t0.AddDays(5));
        var byVotes = svc.Aggregate("votes");
        Assert.Equal(new[] { "Popular", "Newest" }, byVotes.Select(r => r.Title).ToArray());
        Assert.Equal(2, byVotes[0].WantCount);
        Assert.Equal(new[] { "Ann", "Bob" }, byVotes[0].Voters.ToArray());
        Assert.Equal("https://www.themoviedb.org/tv/2", byVotes[1].TmdbUrl);
        Assert.Equal(new[] { "Newest", "Popular" }, svc.Aggregate("recent").Select(r => r.Title).ToArray());
    }

    [Fact]
    public void SetStatus_validates_and_persists()
    {
        var (ts, svc, votes, _) = Setup();
        using var keep = ts;
        votes.Cast(Guid.NewGuid(), V(1, 1));
        Assert.False(svc.SetStatus(1, "movie", "Bogus", null));
        Assert.False(svc.SetStatus(1, "game", "Added", null));
        Assert.True(svc.SetStatus(1, "movie", "Getting it", "ordering"));
        var row = svc.Aggregate("votes").Single();
        Assert.Equal("Getting it", row.Status);
        Assert.Equal("ordering", row.Note);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-2", "'-2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    public void Csv_cell_escaping(string input, string expected) => Assert.Equal(expected, RequestService.CsvCell(input));

    [Fact]
    public void Csv_contains_header_and_escaped_rows()
    {
        var (ts, svc, votes, users) = Setup();
        using var keep = ts;
        var a = Guid.NewGuid();
        users.Names[a] = "=evil()";
        votes.Cast(a, V(1, 1, title: "=HYPERLINK(\"x\")"));
        var csv = RequestService.ToCsv(svc.Aggregate("votes"));
        Assert.StartsWith("Title,Type,TmdbId,Votes,Status,Note,Voters,TmdbUrl,LastVote", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", csv);
        Assert.Contains("'=evil()", csv);
    }

    [Fact]
    public void Sync_marks_added_notifies_voters_and_is_idempotent()
    {
        var (ts, svc, votes, _) = Setup();
        using var keep = ts;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        votes.Cast(a, V(10, 1));
        votes.Cast(b, V(10, 1));
        votes.Cast(b, V(11, 1, "tv"));
        var lib = new[] { Make.Item("Ten", 10) };
        Assert.Equal(1, svc.Sync(lib));
        Assert.Equal("Added", ts.Store.Read(d => d.Statuses["movie:10"].Status));
        Assert.Equal("Requested", ts.Store.Read(d => d.Statuses["tv:11"].Status));
        var notes = ts.Store.Read(d => d.Notifications.ToList());
        Assert.Equal(2, notes.Count);
        Assert.All(notes, n => Assert.Equal("Ten is now available", n.Text));
        Assert.Equal(0, svc.Sync(lib));
        Assert.Equal(2, ts.Store.Read(d => d.Notifications.Count));
    }

    [Fact]
    public void Sync_matches_media_type_and_respects_notification_setting()
    {
        using var ts = new TempStore();
        var cfg = new FakeConfig();
        cfg.Current.RequestNotifications = false;
        var svc = new RequestService(ts.Store, new FakeUsers(), cfg);
        var votes = new VoteService(ts.Store);
        votes.Cast(Guid.NewGuid(), V(10, 1, "tv"));

        // a movie with the same numeric id must not satisfy a tv request
        Assert.Equal(0, svc.Sync(new[] { Make.Item("Movie10", 10) }));
        Assert.Equal(1, svc.Sync(new[] { Make.Item("Show10", 10, CatalogKind.Series) }));
        Assert.Empty(ts.Store.Read(d => d.Notifications.ToList()));
    }
}
