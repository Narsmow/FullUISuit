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
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

internal sealed class FixtureOllama : IOllamaClient
{
    public bool Enabled => true;

    public string EmbedModel => "nomic-embed-text";

    public Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<float[]>?>(texts.Select(_ => new float[] { 1, 0, 0 }).ToList());

    public Task<string?> ChatAsync(string system, string user, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<(bool ok, string message)> TestAsync(string? url, string? e, string? c, CancellationToken ct) => Task.FromResult((true, "Connected."));
}

internal sealed class FixtureNextUp : INextUpSource
{
    public Dictionary<Guid, List<NextUpEntry>> ByUser { get; } = new();

    public IReadOnlyList<Guid> NextUpSeries(Guid userId) => NextUpEntries(userId).Select(e => e.SeriesId).ToList();

    public IReadOnlyList<NextUpEntry> NextUpEntries(Guid userId) => ByUser.TryGetValue(userId, out var l) ? l : new List<NextUpEntry>();
}

/// <summary>
/// A fixed household and library used to produce the contract fixtures: fixed ids, dates and clock, so the JSON is the same on every run.
/// "Now" is 2026-06-01 12:00 UTC.
/// </summary>
internal sealed class FixtureWorld
{
    public static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    public static readonly Guid Viewer = new("11111111-2222-3333-4444-555555555555");
    public static readonly Guid NewUser = new("99999999-2222-3333-4444-555555555555");
    private static readonly Guid Alex = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bea = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Cleo = new("aaaaaaaa-0000-0000-0000-000000000003");

    public static Guid Id(int n) => new(n, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);

    private static readonly Guid DuneCollection = new("dc000000-0000-0000-0000-000000000001");

    private readonly PluginStore _store;

    public FixtureWorld(PluginStore store)
    {
        _store = store;
        Items = BuildLibrary();
        Catalog = new FakeCatalog { Items = Items };
        Config = new FakeConfig
        {
            Current = new PluginConfiguration { ServerName = "Home Cinema", TmdbApiKey = "fixture", RequestNotifications = true },
        };
        Cast = new FakeCast();
        foreach (var i in Items)
        {
            Cast.Cast[i.Id] = i.Cast.ToArray();
        }

        NextUp = new FixtureNextUp();
        NextUp.ByUser[Viewer] = new List<NextUpEntry> { new(Id(31), 2, 5) };
        SeedStore();
        Home = new HomeService(store, Catalog, NullLogger<HomeService>.Instance, Config, NextUp);
    }

    public List<CatalogItem> Items { get; }

    public FakeCatalog Catalog { get; }

    public FakeConfig Config { get; }

    public FakeCast Cast { get; }

    public FixtureNextUp NextUp { get; }

    public HomeService Home { get; }

    // ------------------------------------------------------------------ library

    private static string Tag(int n) => (0xA1B2C3D4E5F60000UL + (ulong)n * 0x1111UL).ToString("x16");

    private static CatalogItem Movie(int n, string name, int year, float rating, string rated, int runtime, string[] genres, string overview, int tmdb,
        string[]? cast = null, string[]? directors = null, string? trailer = null, int daysOld = 300, Guid? collection = null, string? collectionName = null)
        => new()
        {
            Id = Id(n),
            Kind = CatalogKind.Movie,
            Name = name,
            Year = year,
            Rating = rating,
            OfficialRating = rated,
            RuntimeMinutes = runtime,
            Genres = genres,
            Overview = overview,
            TmdbId = tmdb,
            TrailerKey = trailer,
            DateAdded = Now.AddDays(-daysOld),
            HasBackdrop = true,
            HasLogo = n % 3 == 0,
            PrimaryImageTag = Tag(n),
            Cast = cast ?? Array.Empty<string>(),
            Directors = directors ?? Array.Empty<string>(),
            CollectionId = collection,
            CollectionName = collectionName,
        };

    private static CatalogItem Show(int n, string name, int year, float rating, string rated, int runtime, string[] genres, string overview, int tmdb, int[] episodes,
        string[]? cast = null, string? trailer = null, int daysOld = 300, int[]? seasonAddedDaysAgo = null, int[]? recentEpisodeDaysAgo = null)
    {
        var seasons = new List<SeasonInfo>();
        for (var i = 0; i < episodes.Length; i++)
        {
            var added = Now.AddDays(-(seasonAddedDaysAgo is not null && i < seasonAddedDaysAgo.Length ? seasonAddedDaysAgo[i] : daysOld));
            seasons.Add(new SeasonInfo(i + 1, episodes[i], added, added));
        }

        var recent = (recentEpisodeDaysAgo ?? Array.Empty<int>()).Select(d => Now.AddDays(-d)).OrderByDescending(d => d).ToList();
        return new CatalogItem
        {
            Id = Id(n),
            Kind = CatalogKind.Series,
            Name = name,
            Year = year,
            Rating = rating,
            OfficialRating = rated,
            RuntimeMinutes = runtime,
            Genres = genres,
            Overview = overview,
            TmdbId = tmdb,
            TrailerKey = trailer,
            DateAdded = Now.AddDays(-daysOld),
            Seasons = seasons,
            RecentEpisodeDates = recent,
            LatestEpisodeAdded = recent.Count > 0 ? recent[0] : seasons.Max(s => s.LastAdded),
            HasBackdrop = true,
            HasLogo = n % 2 == 0,
            PrimaryImageTag = Tag(n),
            Cast = cast ?? Array.Empty<string>(),
        };
    }

    private static List<CatalogItem> BuildLibrary() => new()
    {
        Movie(1, "Arrival", 2016, 7.9f, "PG-13", 116, new[] { "Science Fiction", "Drama" }, "A linguist works with the military to communicate with alien visitors before the world slides into war.", 329865, new[] { "Amy Adams", "Jeremy Renner", "Forest Whitaker" }, new[] { "Denis Villeneuve" }, "tNQr0fKLvGE", 380),
        Movie(2, "Blade Runner 2049", 2017, 8.0f, "R", 164, new[] { "Science Fiction", "Thriller" }, "A young blade runner uncovers a long-buried secret that leads him to track down a missing legend.", 335984, new[] { "Ryan Gosling", "Harrison Ford", "Ana de Armas" }, new[] { "Denis Villeneuve" }, "gCcx85zbxz4", 360),
        Movie(3, "Ex Machina", 2014, 7.7f, "R", 108, new[] { "Science Fiction", "Thriller" }, "A programmer is invited to evaluate the human qualities of a breathtaking humanoid robot.", 264660, new[] { "Domhnall Gleeson", "Alicia Vikander", "Oscar Isaac" }, new[] { "Alex Garland" }, null, 340),
        Movie(4, "The Martian", 2015, 8.0f, "PG-13", 144, new[] { "Science Fiction", "Adventure" }, "An astronaut is stranded alone on Mars and must find a way to survive until rescue.", 286217, new[] { "Matt Damon", "Jessica Chastain" }, new[] { "Ridley Scott" }, "ej3ioOneTy8", 330),
        Movie(5, "Dune", 2021, 8.0f, "PG-13", 155, new[] { "Science Fiction", "Adventure" }, "A noble family becomes embroiled in a war for control over the galaxy's most valuable asset.", 438631, new[] { "Timothée Chalamet", "Zendaya", "Rebecca Ferguson" }, new[] { "Denis Villeneuve" }, "n9xhJrPXop4", 320, DuneCollection, "Dune Collection"),
        Movie(6, "Dune: Part Two", 2024, 8.3f, "PG-13", 166, new[] { "Science Fiction", "Adventure" }, "Paul Atreides unites with the Fremen while seeking revenge against those who destroyed his family.", 693134, new[] { "Timothée Chalamet", "Zendaya", "Austin Butler" }, new[] { "Denis Villeneuve" }, "Way9Dexny3w", 20, DuneCollection, "Dune Collection"),
        Movie(7, "Moon", 2009, 7.8f, "R", 97, new[] { "Science Fiction", "Drama" }, "Nearing the end of a lonely three-year stint on the moon, an astronaut begins to question what he knows.", 17431, new[] { "Sam Rockwell" }, new[] { "Duncan Jones" }, null, 310),
        Movie(8, "Interstellar", 2014, 8.4f, "PG-13", 169, new[] { "Science Fiction", "Adventure", "Drama" }, "A team of explorers travels through a wormhole in space in an attempt to ensure humanity's survival.", 157336, new[] { "Matthew McConaughey", "Anne Hathaway", "Jessica Chastain" }, new[] { "Christopher Nolan" }, "zSWdZVtXT7E", 300),
        Movie(9, "Whiplash", 2014, 8.4f, "R", 106, new[] { "Drama", "Music" }, "A promising young drummer enrolls at a cut-throat music conservatory where his dreams are tested.", 244786, new[] { "Miles Teller", "J.K. Simmons" }, new[] { "Damien Chazelle" }, null, 290),
        Movie(10, "Parasite", 2019, 8.5f, "R", 132, new[] { "Thriller", "Drama", "Comedy" }, "All unemployed, Ki-taek's family takes peculiar interest in the wealthy and glamorous Parks.", 496243, new[] { "Song Kang-ho", "Choi Woo-shik" }, new[] { "Bong Joon-ho" }, "5xH0HfJHsaY", 280),
        Movie(11, "Moonlight", 2016, 7.4f, "R", 111, new[] { "Drama" }, "A young man grapples with his identity and sexuality while experiencing the everyday struggles of childhood.", 376867, new[] { "Mahershala Ali", "Naomie Harris" }, new[] { "Barry Jenkins" }, null, 270),
        Movie(12, "Nomadland", 2020, 7.3f, "R", 108, new[] { "Drama" }, "After losing everything in the Great Recession, a woman embarks on a journey through the American West.", 581734, new[] { "Frances McDormand" }, new[] { "Chloé Zhao" }, null, 260),
        Movie(13, "The Father", 2020, 8.2f, "PG-13", 97, new[] { "Drama" }, "A man refuses all assistance from his daughter as he ages and begins to doubt his own mind.", 600354, new[] { "Anthony Hopkins", "Olivia Colman" }, new[] { "Florian Zeller" }, null, 250),
        Movie(14, "Superbad", 2007, 7.6f, "R", 113, new[] { "Comedy" }, "Two co-dependent high school seniors are forced to deal with separation anxiety after their plan goes awry.", 8363, new[] { "Jonah Hill", "Michael Cera" }, new[] { "Greg Mottola" }, null, 240),
        Movie(15, "Paddington 2", 2017, 7.8f, "PG", 103, new[] { "Comedy", "Family" }, "Paddington picks up a series of odd jobs to buy the perfect present for his Aunt Lucy's 100th birthday.", 346648, new[] { "Hugh Grant", "Ben Whishaw" }, new[] { "Paul King" }, null, 230),
        Movie(16, "The Grand Budapest Hotel", 2014, 8.1f, "R", 99, new[] { "Comedy", "Drama" }, "A writer encounters the owner of an aging high-class hotel who tells of his early years as a lobby boy.", 120467, new[] { "Ralph Fiennes", "Tony Revolori" }, new[] { "Wes Anderson" }, null, 220),
        Movie(17, "Knives Out", 2019, 7.9f, "PG-13", 130, new[] { "Mystery", "Comedy" }, "A detective investigates the death of a patriarch of an eccentric, combative family.", 546554, new[] { "Daniel Craig", "Ana de Armas" }, new[] { "Rian Johnson" }, null, 210),
        Movie(18, "Mad Max: Fury Road", 2015, 7.6f, "R", 120, new[] { "Action", "Adventure" }, "In a post-apocalyptic wasteland, a woman rebels against a tyrannical ruler in search of her homeland.", 76341, new[] { "Tom Hardy", "Charlize Theron" }, new[] { "George Miller" }, null, 200),
        Movie(19, "John Wick", 2014, 7.4f, "R", 101, new[] { "Action", "Thriller" }, "An ex-hitman comes out of retirement to track down the gangsters that took everything from him.", 245891, new[] { "Keanu Reeves" }, new[] { "Chad Stahelski" }, null, 190),
        Movie(20, "Spirited Away", 2001, 8.5f, "PG", 125, new[] { "Animation", "Family" }, "A young girl wanders into a world ruled by gods, witches and spirits, where humans are changed into beasts.", 129, Array.Empty<string>(), new[] { "Hayao Miyazaki" }, null, 180),
        Movie(21, "Spider-Man: Into the Spider-Verse", 2018, 8.4f, "PG", 117, new[] { "Animation", "Action" }, "Teen Miles Morales becomes the Spider-Man of his universe and joins others from across the multiverse.", 324857, Array.Empty<string>(), new[] { "Peter Ramsey" }, null, 170),
        Movie(22, "Se7en", 1995, 8.4f, "R", 127, new[] { "Thriller", "Crime" }, "Two detectives hunt a serial killer who uses the seven deadly sins as his motives.", 807, new[] { "Brad Pitt", "Morgan Freeman" }, new[] { "David Fincher" }, null, 160),
        Movie(23, "Gone Girl", 2014, 8.1f, "R", 149, new[] { "Thriller", "Drama" }, "With his wife's disappearance, Nick Dunne becomes the focus of an intense media circus.", 210577, new[] { "Ben Affleck", "Rosamund Pike" }, new[] { "David Fincher" }, null, 150),
        Movie(24, "Prisoners", 2013, 8.2f, "R", 153, new[] { "Thriller", "Crime" }, "When his daughter goes missing, a father takes matters into his own hands.", 146233, new[] { "Hugh Jackman", "Jake Gyllenhaal" }, new[] { "Denis Villeneuve" }, null, 140),
        Movie(25, "Poor Things", 2023, 7.8f, "R", 141, new[] { "Comedy", "Science Fiction" }, "The incredible tale of Bella Baxter, brought back to life by a brilliant, unorthodox scientist.", 792307, new[] { "Emma Stone", "Mark Ruffalo" }, new[] { "Yorgos Lanthimos" }, null, 4),
        Show(31, "Severance", 2022, 8.7f, "TV-MA", 50, new[] { "Science Fiction", "Drama", "Thriller" }, "Mark leads a team of office workers whose memories have been surgically divided between their work and personal lives.", 95396, new[] { 9, 10 },
            new[] { "Adam Scott", "Britt Lower", "Patricia Arquette" }, "xEQP4VVuyrY", 250, new[] { 250, 40 }, new[] { 3, 10, 17 }),
        Show(32, "Dark", 2017, 8.7f, "TV-MA", 55, new[] { "Science Fiction", "Drama", "Mystery" }, "A missing child sets four families on a frantic hunt for answers as they unearth a mind-bending mystery.", 70523, new[] { 10, 8, 8 }, new[] { "Louis Hofmann", "Lisa Vicari" }, null, 240),
        Show(33, "Succession", 2018, 8.8f, "TV-MA", 60, new[] { "Drama", "Comedy" }, "The Roy family is known for controlling the biggest media and entertainment company in the world.", 76331, new[] { 10, 8, 9, 10 }, new[] { "Brian Cox", "Jeremy Strong" }, null, 230),
        Show(34, "Ted Lasso", 2020, 8.8f, "TV-MA", 30, new[] { "Comedy", "Drama" }, "An American football coach is hired to manage a British soccer team despite having no experience.", 97546, new[] { 10, 12, 12 }, new[] { "Jason Sudeikis", "Hannah Waddingham" }, null, 220),
        Show(35, "Breaking Bad", 2008, 8.9f, "TV-MA", 47, new[] { "Drama", "Crime" }, "A chemistry teacher diagnosed with cancer turns to manufacturing drugs to secure his family's future.", 1396, new[] { 7, 13, 13, 13, 16 }, new[] { "Bryan Cranston", "Aaron Paul" }, null, 210),
        Show(36, "Fleabag", 2016, 8.6f, "TV-MA", 27, new[] { "Comedy", "Drama" }, "A dry-witted woman navigates life and love in London while grappling with a tragic loss.", 67070, new[] { 6, 6 }, new[] { "Phoebe Waller-Bridge" }, null, 200),
        Show(37, "The Bear", 2022, 8.6f, "TV-MA", 35, new[] { "Drama", "Comedy" }, "A young chef returns to Chicago to run his family's sandwich shop.", 136315, new[] { 8, 10, 10 }, new[] { "Jeremy Allen White", "Ayo Edebiri" }, null, 190, new[] { 190, 120, 5 }, new[] { 5, 6, 7 }),
        Show(38, "Andor", 2022, 8.4f, "TV-14", 45, new[] { "Science Fiction", "Drama" }, "Cassian Andor discovers the difference he can make against the Empire.", 83867, new[] { 12, 12 }, new[] { "Diego Luna", "Stellan Skarsgård" }, null, 180),
        Show(39, "Chernobyl", 2019, 9.1f, "TV-MA", 62, new[] { "Drama", "History" }, "The true story of one of the worst man-made catastrophes in history.", 87108, new[] { 5 }, new[] { "Jared Harris", "Stellan Skarsgård" }, null, 170),
    };

    // ------------------------------------------------------------------ store

    private static PlaySignal Play(Guid user, int item, double daysAgo, double completion = 1)
        => new() { UserId = user, ItemId = Id(item), At = Now.AddDays(-daysAgo), Completion = completion, Completed = completion >= 0.9 };

    private static PlaySignal Ep(Guid user, int show, int season, int episode, double daysAgo, double completion = 1)
        => new() { UserId = user, ItemId = Id(show), IsEpisode = true, At = Now.AddDays(-daysAgo), Completion = completion, Completed = completion >= 0.9, Season = season, Episode = episode };

    private static IEnumerable<PlaySignal> Episodes(Guid user, int show, int season, int count, double daysAgo)
        => Enumerable.Range(1, count).Select(e => Ep(user, show, season, e, daysAgo + (count - e) * 0.01));

    private void SeedStore()
    {
        _store.Write(d =>
        {
            var v = Viewer;
            d.Signals.AddRange(new[]
            {
                Play(v, 1, 40), Play(v, 2, 30), Play(v, 3, 25), Play(v, 9, 60), Play(v, 22, 90),
                Play(v, 8, 2, 0.42),
            });
            d.Signals.AddRange(Episodes(v, 31, 1, 9, 20));
            d.Signals.AddRange(Episodes(v, 31, 2, 4, 3));
            d.Signals.Add(Ep(v, 31, 2, 5, 0.5, 0.35));
            d.Signals.AddRange(Episodes(v, 37, 1, 8, 45));
            d.Signals.AddRange(Episodes(v, 37, 2, 10, 40));
            d.Signals.AddRange(Enumerable.Range(1, 7).Select(e => Ep(v, 35, 1, e, 100 - e)));
            d.Signals.AddRange(new[] { 2, 3, 4, 5 }.SelectMany(s => Enumerable.Range(1, new[] { 7, 13, 13, 13, 16 }[s - 1]).Select(e => Ep(v, 35, s, e, 90 - s))));
            d.Signals.Add(Ep(v, 32, 1, 1, 70, 0.2));

            d.Ratings[StoreData.UserItemKey(v, Id(1))] = 2;
            d.Ratings[StoreData.UserItemKey(v, Id(3))] = 1;
            d.Ratings[StoreData.UserItemKey(v, Id(14))] = -1;
            d.MyList.Add(StoreData.UserItemKey(v, Id(5)));
            d.MyList.Add(StoreData.UserItemKey(v, Id(10)));
            d.MyList.Add(StoreData.UserItemKey(v, Id(38)));

            // The rest of the household: what everyone has been watching this week.
            foreach (var (user, movies, shows) in new[]
                     {
                         (Alex, new[] { 10, 18, 20, 23, 6, 1, 2 }, new[] { 33, 34, 39 }),
                         (Bea, new[] { 10, 18, 19, 20, 21, 2, 4 }, new[] { 33, 39, 36 }),
                         (Cleo, new[] { 10, 23, 24, 18, 1, 8 }, new[] { 33, 34, 36 }),
                     })
            {
                var day = 0.3;
                foreach (var m in movies)
                {
                    d.Signals.Add(Play(user, m, day));
                    day += 0.4;
                }

                foreach (var s in shows)
                {
                    d.Signals.Add(Ep(user, s, 1, 1, day));
                    d.Signals.Add(Ep(user, s, 1, 2, day - 0.05));
                    day += 0.4;
                }
            }

            // Coming Soon (what the nightly TMDB task stored for the viewer).
            d.ComingSoon[Viewer.ToString("N")] = new List<ComingSoonEntry>
            {
                new() { TmdbId = 1001, MediaType = "movie", Title = "The Odyssey", Overview = "Christopher Nolan adapts Homer's epic about a king's ten-year voyage home.", PosterPath = "/odyssey.jpg", BackdropPath = "/odyssey-b.jpg", ReleaseDate = "2026-07-17", TrailerKey = "ODYSSEY0001", Score = 0.91, Upcoming = true },
                new() { TmdbId = 1002, MediaType = "movie", Title = "Supergirl", Overview = "Kara Zor-El takes on a cosmic road trip.", PosterPath = "/supergirl.jpg", BackdropPath = "/supergirl-b.jpg", ReleaseDate = "2026-06-26", Score = 0.84, Upcoming = true },
                new() { TmdbId = 1003, MediaType = "tv", Title = "Severance: Season 3", Overview = "The innies return to Lumon.", PosterPath = "/sev3.jpg", BackdropPath = "/sev3-b.jpg", ReleaseDate = "2026-09-04", Score = 0.80, Upcoming = true, SeasonNumber = 3 },
                new() { TmdbId = 1004, MediaType = "movie", Title = "Spider-Man: Brand New Day", Overview = "Peter Parker returns to New York.", PosterPath = "/spidey.jpg", BackdropPath = "/spidey-b.jpg", ReleaseDate = "2026-07-31", TrailerKey = "SPIDEY00001", Score = 0.72, Upcoming = true },
                new() { TmdbId = 1005, MediaType = "movie", Title = "Anora", Overview = "A young sex worker from Brooklyn gets her chance at a Cinderella story.", PosterPath = "/anora.jpg", BackdropPath = "/anora-b.jpg", ReleaseDate = "2024-10-18", Score = 0.66, Upcoming = false },
                new() { TmdbId = 1006, MediaType = "tv", Title = "Shōgun", Overview = "A shipwrecked English pilot becomes entangled in a Japanese power struggle.", PosterPath = "/shogun.jpg", BackdropPath = "/shogun-b.jpg", ReleaseDate = "2024-02-27", Score = 0.61, Upcoming = false },
            };
            d.Votes.Add(new VoteEntry { UserId = Viewer, TmdbId = 1002, MediaType = "movie", Vote = 1, Title = "Supergirl", PosterPath = "/supergirl.jpg", BackdropPath = "/supergirl-b.jpg", ReleaseDate = "2026-06-26", Overview = "Kara Zor-El takes on a cosmic road trip.", At = Now.AddDays(-2) });
            d.TrailerKeys["movie:1002"] = "SUPERGIRL01";

            d.Notifications.AddRange(new[]
            {
                new NotificationEntry { Id = new Guid("00000000-0000-0000-0000-0000000000a1"), UserId = Viewer, Text = "Dune: Part Two is now on Home Cinema", ItemId = Id(6), At = Now.AddDays(-1), Read = false },
                new NotificationEntry { Id = new Guid("00000000-0000-0000-0000-0000000000a2"), UserId = Viewer, Text = "Poor Things is now on Home Cinema", ItemId = Id(25), At = Now.AddDays(-4), Read = true },
                new NotificationEntry { Id = new Guid("00000000-0000-0000-0000-0000000000a3"), UserId = Alex, Text = "Someone else's notification", At = Now.AddDays(-1) },
            });

            // Vectors for AI search (3 dimensions are enough for a fixture).
            d.RowTitles["Science Fiction"] = "Mind-Bending Sci-Fi";
            d.RowTitleStamps["Science Fiction"] = Now.AddDays(-2);
        });

        var vectors = new Dictionary<int, float[]>
        {
            [1] = new float[] { 0.95f, 0.30f, 0.05f },   // Arrival
            [2] = new float[] { 0.90f, 0.40f, 0.10f },   // Blade Runner 2049
            [8] = new float[] { 0.99f, 0.10f, 0.05f },   // Interstellar
            [7] = new float[] { 0.80f, 0.55f, 0.10f },   // Moon
            [31] = new float[] { 0.75f, 0.60f, 0.20f },  // Severance
            [32] = new float[] { 0.85f, 0.35f, 0.30f },  // Dark
            [10] = new float[] { 0.10f, 0.20f, 0.95f },  // Parasite (not similar)
            [18] = new float[] { 0.05f, 0.10f, 0.99f },  // Mad Max
        };
        _store.WriteEmbeddings(e =>
        {
            foreach (var (n, vec) in vectors)
            {
                e[Id(n).ToString("N")] = new EmbeddingEntry { Vector = vec, Model = "nomic-embed-text", Hash = "fixture" };
            }
        });

        new ReminderService(_store, Catalog, Config, NullLogger<ReminderService>.Instance).Set(Viewer, 1001, "movie", true, Now.AddDays(-1));
        var runs = new TaskRunLog(_store);
        runs.Record("FullUIRebuildRecs", "Rebuild FullUI recommendations", Now.AddHours(-9).AddMinutes(-2), Now.AddHours(-9), TaskOutcome.Success, "Finished normally.");
        runs.Record("FullUIDiscoverUpcoming", "Discover upcoming titles", Now.AddHours(-9), Now.AddHours(-8).AddMinutes(-58), TaskOutcome.Success, "Finished normally.");
        runs.Record("FullUIBuildEmbeddings", "Build AI index", Now.AddDays(-1), Now.AddDays(-1).AddMinutes(4), TaskOutcome.Problem, "3 of 38 titles could not be embedded.");
        runs.Record("FullUIDaily", "Send reminders and tidy up", Now.AddHours(-6), Now.AddHours(-6).AddSeconds(5), TaskOutcome.Success, "1 reminder sent.");
    }

    // ------------------------------------------------------------------ controllers

    public DiscoveryController Discovery(bool semantic = false, ICatalog? catalog = null)
    {
        var cat = catalog ?? Catalog;
        IOllamaClient ollama = semantic ? new FixtureOllama() : new FakeOllama();
        return new DiscoveryController(_store, cat, new VoteService(_store), new NlSearch(_store, cat, ollama), Config, new FakeTmdbClient(),
            NullLogger<DiscoveryController>.Instance, new SuggestService(cat, Cast)).As(Viewer);
    }

    public NewPopularController NewPopularCtl()
        => new NewPopularController(
            new NewPopularService(_store, Catalog, new EngineTrendingProvider(Home, new SignalTrendingProvider(_store, Catalog, Config, new FakeUsers()), NullLogger<EngineTrendingProvider>.Instance)),
            new ReminderService(_store, Catalog, Config, NullLogger<ReminderService>.Instance),
            Config,
            NullLogger<NewPopularController>.Instance).As(Viewer);

    public OnboardingController OnboardingCtl(Guid user)
        => new OnboardingController(new OnboardingService(_store, Catalog), new HiddenItemsService(_store), Catalog, _store, new FakeHome(), NullLogger<OnboardingController>.Instance).As(user);

    public SearchSuggestController SuggestCtl()
        => new SearchSuggestController(new SuggestService(Catalog, Cast), NullLogger<SearchSuggestController>.Instance).As(Viewer);

    private InteractionLog? _log;

    public MetricsController MetricsCtl(Guid user, bool seedMore = false)
    {
        _log ??= new InteractionLog(_store, NullLogger<InteractionLog>.Instance);
        if (seedMore)
        {
            SeedEvents(_log);
        }

        return new MetricsController(_log, new MetricsService(_log, Config), new EventRateLimiter(), Config, NullLogger<MetricsController>.Instance).As(user);
    }

    private static void SeedEvents(InteractionLog log)
    {
        var events = new List<StoredEvent>();
        foreach (var (user, offset) in new[] { (Viewer, 0), (Alex, 1), (Bea, 2) })
        {
            for (var day = 1; day <= 5; day++)
            {
                var at = Now.AddDays(-day).Date.AddHours(19 + offset);
                events.Add(new StoredEvent(user, "rowShown", "toppicks", null, null, at));
                events.Add(new StoredEvent(user, "rowShown", "because", null, null, at));
                events.Add(new StoredEvent(user, "rowShown", "genre", null, null, at));
                events.Add(new StoredEvent(user, "cardExpanded", "toppicks", Id(6).ToString("N"), null, at.AddSeconds(5)));
                if (day % 2 == 1)
                {
                    events.Add(new StoredEvent(user, "cardClicked", "toppicks", Id(6).ToString("N"), null, at.AddSeconds(9)));
                    events.Add(new StoredEvent(user, "playStarted", "toppicks", Id(6).ToString("N"), null, at.AddSeconds(12)));
                }

                if (day % 3 == 0)
                {
                    events.Add(new StoredEvent(user, "cardClicked", "because", Id(2).ToString("N"), null, at.AddSeconds(20)));
                }
            }
        }

        events.Add(new StoredEvent(Viewer, "searchIssued", null, null, "blade runner", Now.AddDays(-1)));
        events.Add(new StoredEvent(Alex, "searchIssued", null, null, "blade runner", Now.AddDays(-2)));
        events.Add(new StoredEvent(Bea, "searchIssued", null, null, "dune", Now.AddDays(-2)));
        log.Append(events);
    }

    public AdminOpsController AdminOpsCtl()
    {
        _log ??= new InteractionLog(_store, NullLogger<InteractionLog>.Instance);
        var health = new HealthService(_store, Catalog, new TaskRunLog(_store), Config, new FakeTmdbClient(), new FakeOllama { Enabled = false }, _log);
        return new AdminOpsController(health, null!, NullLogger<AdminOpsController>.Instance).As(Viewer);
    }

    // ------------------------------------------------------------------ title details

    public ItemDetailsController DetailsCtl()
        => new ItemDetailsController(
            new ItemDetailsService(Home, new FixtureTitleSource(), _store, Config, NullLogger<ItemDetailsService>.Instance),
            NullLogger<ItemDetailsController>.Instance).As(Viewer);
}

/// <summary>What Jellyfin would return for the two titles of the details fixtures (Interstellar, Severance), as the household viewer sees them.</summary>
internal sealed class FixtureTitleSource : ITitleDetailsSource
{
    private static Guid Id(int n) => FixtureWorld.Id(n);

    public TitleData? Load(Guid userId, Guid itemId)
    {
        if (itemId == Id(8))
        {
            return new TitleData(
                "Mankind was born on Earth. It was never meant to die here.",
                Array.Empty<SeasonData>(),
                new[]
                {
                    new PersonData("Matthew McConaughey", "Cooper", "Actor", Id(901), true),
                    new PersonData("Anne Hathaway", "Brand", "Actor", Id(902), true),
                    new PersonData("Jessica Chastain", "Murph", "Actor", Id(903), true),
                    new PersonData("Christopher Nolan", null, "Director", Id(904), true),
                    new PersonData("Jonathan Nolan", null, "Writer", Id(905), false),
                },
                new[] { new TrailerData("zSWdZVtXT7E", "Official Trailer") });
        }

        if (itemId == Id(31))
        {
            var s1 = new[] { "Good News About Hell", "Half Loop", "In Perpetuity", "The You You Are", "The Grim Barbarity of Optics and Design", "Hide and Seek", "Defiant Jazz", "What's for Dinner?", "The We We Are" };
            var s2 = new[] { "Hello, Ms. Cobel", "Goodbye, Mrs. Selvig", "Who Is Alive?", "Woes Hollow", "Trojan's Horse", "Attila", "Chikhai Bardo", "Sweet Vitriol", "The After Hours", "Cold Harbor" };
            var seasons = new List<SeasonData>
            {
                new(Id(311), 1, "Season 1", s1.Select((n, i) => new EpisodeData(Id(3100 + i + 1), 1, i + 1, n, i == 0 ? "Mark S. is promoted after a colleague leaves Lumon." : null, 45 + ((i * 3) % 11), null, true, true, new DateTime(2022, 2, 18).AddDays(i < 3 ? 0 : 7 * (i - 2)))).ToList()),
                new(Id(312), 2, "Season 2", s2.Select((n, i) => new EpisodeData(Id(3200 + i + 1), 2, i + 1, n, i == 4 ? "Mark confronts what the severance chip took from him." : null, 44 + ((i * 5) % 12), i == 4 ? 0.35 : null, i < 4, i != 9, new DateTime(2025, 1, 17).AddDays(7 * i))).ToList()),
                new(Id(310), 0, "Specials", new[] { new EpisodeData(Id(3001), 0, 1, "Making of Severance", null, 22, null, false, true, new DateTime(2022, 3, 1)) }),
            };
            return new TitleData(
                null,
                seasons,
                new[]
                {
                    new PersonData("Adam Scott", "Mark Scout", "Actor", Id(906), true),
                    new PersonData("Britt Lower", "Helly R.", "Actor", Id(907), true),
                    new PersonData("Patricia Arquette", "Harmony Cobel", "Actor", Id(908), true),
                    new PersonData("Ben Stiller", null, "Director", Id(909), false),
                },
                new[] { new TrailerData("xEQP4VVuyrY", "Official Trailer"), new TrailerData("AAAAAAAAAAA", null) });
        }

        return null;
    }
}
