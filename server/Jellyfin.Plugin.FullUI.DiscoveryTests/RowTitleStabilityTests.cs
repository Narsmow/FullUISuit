using System.Net;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.DiscoveryTests;

public class RowTitleStabilityTests
{
    private sealed class CountingOllama : IOllamaClient
    {
        public bool Enabled => true;

        public int Chats { get; private set; }

        public string? Reply { get; set; } = "Twisty Sci-Fi Mysteries";

        public List<string> Prompts { get; } = new();

        public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) => Task.FromResult<IReadOnlyList<float[]>?>(null);

        public Task<string?> ChatAsync(string system, string user, CancellationToken ct)
        {
            Chats++;
            Prompts.Add(user);
            return Task.FromResult(Reply);
        }

        public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "ok"));
    }

    private static FakeCatalog Library() => new()
    {
        Items =
        {
            Make.Item("Alpha", 1, genres: new[] { "Sci-Fi" }, rating: 5f),
            Make.Item("Bravo", 2, genres: new[] { "Sci-Fi" }, rating: 9f),
            Make.Item("Charlie", 3, genres: new[] { "Sci-Fi" }, rating: 7f),
        },
    };

    [Fact]
    public async Task Titles_are_kept_for_a_week_and_only_then_asked_for_again()
    {
        using var ts = new TempStore();
        var ollama = new CountingOllama();
        var clock = new DateTime(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
        var gen = new RowTitleGenerator(ts.Store, Library(), ollama) { Now = () => clock };

        Assert.Equal(1, await gen.RunAsync(default));
        Assert.Equal(1, ollama.Chats);

        // The nightly run for the next six days changes nothing and does not even ask the model.
        for (var day = 1; day <= 6; day++)
        {
            clock = clock.AddDays(1);
            ollama.Reply = "A Completely Different Title " + day;
            Assert.Equal(0, await gen.RunAsync(default));
        }

        Assert.Equal(1, ollama.Chats);
        Assert.Equal("Twisty Sci-Fi Mysteries", ts.Store.Read(d => d.RowTitles["Sci-Fi"]));

        clock = clock.AddDays(2); // 8 days after the first
        ollama.Reply = "Fresh Sci-Fi Picks";
        Assert.Equal(1, await gen.RunAsync(default));
        Assert.Equal("Fresh Sci-Fi Picks", ts.Store.Read(d => d.RowTitles["Sci-Fi"]));
    }

    [Fact]
    public async Task A_bad_answer_keeps_the_old_title_and_is_retried_next_run()
    {
        using var ts = new TempStore();
        ts.Store.Write(d =>
        {
            d.RowTitles["Sci-Fi"] = "Old Good Title";
            d.RowTitleStamps["Sci-Fi"] = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        var ollama = new CountingOllama { Reply = "ok" }; // fails validation (too short)
        var gen = new RowTitleGenerator(ts.Store, Library(), ollama) { Now = () => new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        Assert.Equal(0, await gen.RunAsync(default));
        Assert.Equal("Old Good Title", ts.Store.Read(d => d.RowTitles["Sci-Fi"]));
        ollama.Reply = "Mind-Bending Sci-Fi Journeys";
        Assert.Equal(1, await gen.RunAsync(default));
    }

    [Fact]
    public void Samples_are_the_most_watched_then_the_best_rated_titles_of_the_genre()
    {
        var cat = Library();
        cat.Items.Add(Make.Item("Delta", 4, genres: new[] { "Drama" }, rating: 10f));
        var charlie = cat.Items.Single(i => i.Name == "Charlie");
        var watch = new Dictionary<Guid, int> { [charlie.Id] = 3 };

        var sample = RowTitleGenerator.SampleTitles(cat.Items, watch, "sci-fi");

        Assert.Equal(new[] { "Charlie", "Bravo", "Alpha" }, sample.ToArray());   // watched first, then by rating; other genres excluded
    }

    [Fact]
    public async Task The_prompt_uses_representative_titles_not_just_the_first_five_in_the_library()
    {
        using var ts = new TempStore();
        var cat = new FakeCatalog();
        for (var i = 0; i < 10; i++)
        {
            cat.Items.Add(Make.Item($"Filler {i}", i + 1, genres: new[] { "Sci-Fi" }, rating: 4f));
        }

        cat.Items.Add(Make.Item("Masterpiece", 99, genres: new[] { "Sci-Fi" }, rating: 9.5f));
        var ollama = new CountingOllama();
        await new RowTitleGenerator(ts.Store, cat, ollama).RunAsync(default);
        Assert.Contains("Masterpiece", ollama.Prompts.Single());
    }

    [Fact]
    public async Task Chat_requests_use_a_low_temperature()
    {
        string? body = null;
        var h = new FakeHandler
        {
            Respond = r =>
            {
                body = r.Content!.ReadAsStringAsync().Result;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"Quiet Dramas For Two\"}}") };
            },
        };
        var cfg = new FakeConfig { Current = new() { OllamaEnabled = true, OllamaUrl = "http://localhost:11434" } };
        var reply = await new OllamaClient(new FakeFactory(h), cfg, NullLogger<OllamaClient>.Instance).ChatAsync("s", "u", default);
        Assert.Equal("Quiet Dramas For Two", reply);
        Assert.Contains("\"temperature\":0.2", body);
        Assert.DoesNotContain("0.8", body);
    }
}
