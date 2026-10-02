using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>Result of a trailer lookup. <see cref="Failed"/> separates "TMDB could not be asked" from "TMDB has no trailer".</summary>
public readonly record struct TrailerLookup(string? Key, bool Failed);

public interface ITmdbClient
{
    bool Configured { get; }

    Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string mediaType, int id, CancellationToken ct);

    Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string mediaType, int id, CancellationToken ct);

    /// <summary>Titles whose release date (movie) or first air date (show) is today or later. Never "currently airing".</summary>
    Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string mediaType, CancellationToken ct);

    /// <summary>
    /// The age rating TMDB lists for the title in the configured region (falling back to the US), or null when TMDB has none
    /// or could not be asked. Used to apply parental limits to titles that are not in the library.
    /// </summary>
    Task<TmdbCertification?> CertificationAsync(string mediaType, int id, CancellationToken ct) => Task.FromResult<TmdbCertification?>(null);

    /// <summary>For a show: its next episode when that is episode 1 of a later season (a genuinely new season), else null.</summary>
    Task<TmdbNewSeason?> NextSeasonAsync(int tvId, CancellationToken ct) => Task.FromResult<TmdbNewSeason?>(null);

    Task<string?> TrailerKeyAsync(string mediaType, int id, CancellationToken ct);

    /// <summary>
    /// Like <see cref="TrailerKeyAsync"/> but says whether a null key means "no trailer exists" (remember it) or
    /// "the lookup failed" (do not remember it, try again next run).
    /// </summary>
    async Task<TrailerLookup> LookupTrailerAsync(string mediaType, int id, CancellationToken ct)
    {
        try
        {
            return new TrailerLookup(await TrailerKeyAsync(mediaType, id, ct).ConfigureAwait(false), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TrailerLookup(null, true);
        }
    }

    Task<IReadOnlyList<TmdbGenre>> GenresAsync(string mediaType, CancellationToken ct);
}

/// <summary>
/// TMDB v3 client. Accepts a v3 api key (query param) or a v4 read token (bearer, starts with "eyJ").
/// The key is never logged or returned. Requests are serialised through a small semaphore with a short delay,
/// 429 responses honour Retry-After, and successful responses are cached in memory.
/// </summary>
public sealed class TmdbClient : ITmdbClient
{
    private const string Base = "https://api.themoviedb.org/3";
    private readonly IHttpClientFactory _http;
    private readonly IConfigSource _config;
    private readonly ILogger<TmdbClient> _log;
    private readonly SemaphoreSlim _gate = new(2, 2);
    private const int MaxCacheEntries = 400;
    private const int FailureBudget = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureBackOff = TimeSpan.FromMinutes(2);

    // Raw response text, not JsonDocument: a document must be disposed, and a cached one lives (and is shared) forever.
    private readonly ConcurrentDictionary<string, (DateTime at, string text)> _cache = new();
    private int _consecutiveFailures;
    private long _blockedUntilTicks;
    private readonly TimeSpan _delay;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(12);
    private readonly Func<TimeSpan, CancellationToken, Task> _sleep;

    public TmdbClient(IHttpClientFactory http, IConfigSource config, ILogger<TmdbClient> log)
        : this(http, config, log, TimeSpan.FromMilliseconds(120), static (t, ct) => Task.Delay(t, ct))
    {
    }

    public TmdbClient(IHttpClientFactory http, IConfigSource config, ILogger<TmdbClient> log, TimeSpan delay, Func<TimeSpan, CancellationToken, Task> sleep)
    {
        _http = http;
        _config = config;
        _log = log;
        _delay = delay;
        _sleep = sleep;
    }

    public bool Configured => !string.IsNullOrWhiteSpace(_config.Current.TmdbApiKey);

    /// <summary>True when the last call was refused with 401/403 (bad or revoked key).</summary>
    public bool KeyRejected { get; private set; }

    public static bool IsBearer(string key) => key.StartsWith("eyJ", StringComparison.Ordinal);

    public async Task<IReadOnlyList<TmdbTitle>> RecommendationsAsync(string mediaType, int id, CancellationToken ct)
    {
        using var doc = await GetAsync($"/{Seg(mediaType)}/{id}/recommendations", null, ct).ConfigureAwait(false);
        return ParseTitles(doc, mediaType);
    }

    public async Task<IReadOnlyList<TmdbTitle>> SimilarAsync(string mediaType, int id, CancellationToken ct)
    {
        using var doc = await GetAsync($"/{Seg(mediaType)}/{id}/similar", null, ct).ConfigureAwait(false);
        return ParseTitles(doc, mediaType);
    }

    public async Task<IReadOnlyList<TmdbTitle>> UpcomingAsync(string mediaType, CancellationToken ct)
    {
        // "Upcoming" means a release date of today or later. /tv/on_the_air (shows currently airing) and
        // /movie/upcoming (can include dates in the past) are deliberately not used.
        var today = Today().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var tv = mediaType == "tv";
        var path = tv ? "/discover/tv" : "/discover/movie";
        var filter = tv ? "first_air_date.gte=" + today : "primary_release_date.gte=" + today + "&with_release_type=" + Uri.EscapeDataString("2|3");
        var all = new List<TmdbTitle>();
        for (var page = 1; page <= 3; page++)
        {
            using var doc = await GetAsync(path, "sort_by=popularity.desc&" + filter + "&page=" + page, ct).ConfigureAwait(false);
            var items = ParseTitles(doc, mediaType);
            all.AddRange(items);
            if (items.Count == 0)
            {
                break;
            }
        }

        return all;
    }

    /// <summary>Today's date (UTC). Replaceable so tests do not depend on the clock.</summary>
    internal Func<DateTime> Today { get; set; } = static () => DateTime.UtcNow.Date;

    public async Task<TmdbCertification?> CertificationAsync(string mediaType, int id, CancellationToken ct)
    {
        var region = string.IsNullOrWhiteSpace(_config.Current.TmdbRegion) ? "US" : _config.Current.TmdbRegion.Trim().ToUpperInvariant();
        var tv = mediaType == "tv";
        using var doc = await GetAsync(tv ? $"/tv/{id}/content_ratings" : $"/movie/{id}/release_dates", null, ct).ConfigureAwait(false);
        return tv ? ParseTvCertification(doc, region) : ParseMovieCertification(doc, region);
    }

    /// <summary>The certification for <paramref name="region"/> (else the US) from a /movie/{id}/release_dates answer; theatrical releases first.</summary>
    public static TmdbCertification? ParseMovieCertification(JsonDocument? doc, string region)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var r) || r.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var country in new[] { region, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in r.EnumerateArray())
            {
                if (!string.Equals(Str(c, "iso_3166_1"), country, StringComparison.OrdinalIgnoreCase)
                    || !c.TryGetProperty("release_dates", out var rd) || rd.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var best = rd.EnumerateArray()
                    .Select(e => (Cert: Str(e, "certification")?.Trim(), Type: e.TryGetProperty("type", out var t) && t.TryGetInt32(out var ti) ? ti : 0))
                    .Where(x => !string.IsNullOrEmpty(x.Cert))
                    .OrderBy(x => x.Type == 3 ? 0 : x.Type == 2 ? 1 : 2)
                    .Select(x => x.Cert)
                    .FirstOrDefault();
                if (best is not null)
                {
                    return new TmdbCertification(country.ToUpperInvariant(), best);
                }
            }
        }

        return null;
    }

    /// <summary>The rating for <paramref name="region"/> (else the US) from a /tv/{id}/content_ratings answer.</summary>
    public static TmdbCertification? ParseTvCertification(JsonDocument? doc, string region)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var r) || r.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var country in new[] { region, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in r.EnumerateArray())
            {
                if (string.Equals(Str(c, "iso_3166_1"), country, StringComparison.OrdinalIgnoreCase) && Str(c, "rating")?.Trim() is { Length: > 0 } rating)
                {
                    return new TmdbCertification(country.ToUpperInvariant(), rating);
                }
            }
        }

        return null;
    }

    public async Task<TmdbNewSeason?> NextSeasonAsync(int tvId, CancellationToken ct)
    {
        using var doc = await GetAsync($"/tv/{tvId}", null, ct).ConfigureAwait(false);
        return ParseNewSeason(doc, Today());
    }

    /// <summary>A /tv/{id} answer whose next episode is episode 1 of season 2 or later, airing today or later.</summary>
    public static TmdbNewSeason? ParseNewSeason(JsonDocument? doc, DateTime today)
    {
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("next_episode_to_air", out var ne) || ne.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var air = Str(ne, "air_date");
        if (!ne.TryGetProperty("season_number", out var sn) || !sn.TryGetInt32(out var season) || season < 2
            || !ne.TryGetProperty("episode_number", out var en) || !en.TryGetInt32(out var episode) || episode != 1
            || air is null || !DateTime.TryParse(air, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var airDate)
            || airDate.Date < today.Date)
        {
            return null;
        }

        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
        {
            return null;
        }

        var name = Str(root, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var genres = new List<int>();
        if (root.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var ge in g.EnumerateArray())
            {
                if (ge.TryGetProperty("id", out var gid) && gid.TryGetInt32(out var gi))
                {
                    genres.Add(gi);
                }
            }
        }

        var va = root.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var show = new TmdbTitle(id, "tv", name!, Str(root, "overview"), Str(root, "poster_path"), Str(root, "backdrop_path"), air, va, genres);
        return new TmdbNewSeason(show, season, airDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<string?> TrailerKeyAsync(string mediaType, int id, CancellationToken ct)
        => (await LookupTrailerAsync(mediaType, id, ct).ConfigureAwait(false)).Key;

    public async Task<TrailerLookup> LookupTrailerAsync(string mediaType, int id, CancellationToken ct)
    {
        var lang = (_config.Current.TmdbLanguage ?? "en-US").Split('-')[0];
        var (doc, failed) = await GetCoreAsync($"/{Seg(mediaType)}/{id}/videos", "include_video_language=" + Uri.EscapeDataString(lang + ",en,null"), ct).ConfigureAwait(false);
        using (doc)
        {
            return new TrailerLookup(PickTrailer(ParseVideos(doc), lang), failed);
        }
    }

    public async Task<IReadOnlyList<TmdbGenre>> GenresAsync(string mediaType, CancellationToken ct)
    {
        using var doc = await GetAsync($"/genre/{Seg(mediaType)}/list", null, ct).ConfigureAwait(false);
        var list = new List<TmdbGenre>();
        if (doc is not null && doc.RootElement.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in g.EnumerateArray())
            {
                if (e.TryGetProperty("id", out var i) && i.TryGetInt32(out var id) && e.TryGetProperty("name", out var n))
                {
                    list.Add(new TmdbGenre(id, n.GetString() ?? string.Empty));
                }
            }
        }

        return list;
    }

    /// <summary>Best YouTube clip: Trailer before Teaser, official first, then preferred language.</summary>
    public static string? PickTrailer(IEnumerable<TmdbVideo> videos, string? language = null)
    {
        return videos
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(v.Key)
                && (string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase) || string.Equals(v.Type, "Teaser", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
            .ThenByDescending(v => v.Official ? 1 : 0)
            .ThenByDescending(v => language is not null && string.Equals(v.Language, language, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(v => v.Key)
            .FirstOrDefault();
    }

    public static IReadOnlyList<TmdbVideo> ParseVideos(JsonDocument? doc)
    {
        var list = new List<TmdbVideo>();
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var r) || r.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var e in r.EnumerateArray())
        {
            list.Add(new TmdbVideo(
                Str(e, "site") ?? string.Empty,
                Str(e, "type") ?? string.Empty,
                Str(e, "key") ?? string.Empty,
                e.TryGetProperty("official", out var o) && o.ValueKind == JsonValueKind.True,
                Str(e, "iso_639_1")));
        }

        return list;
    }

    public static IReadOnlyList<TmdbTitle> ParseTitles(JsonDocument? doc, string mediaType)
    {
        var list = new List<TmdbTitle>();
        if (doc is null || !doc.RootElement.TryGetProperty("results", out var r) || r.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var e in r.EnumerateArray())
        {
            if (!e.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id))
            {
                continue;
            }

            var title = Str(e, mediaType == "tv" ? "name" : "title") ?? Str(e, "title") ?? Str(e, "name");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var genres = new List<int>();
            if (e.TryGetProperty("genre_ids", out var gi) && gi.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in gi.EnumerateArray())
                {
                    if (g.TryGetInt32(out var gid))
                    {
                        genres.Add(gid);
                    }
                }
            }

            var va = e.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            list.Add(new TmdbTitle(
                id,
                mediaType,
                title,
                Str(e, "overview"),
                Str(e, "poster_path"),
                Str(e, "backdrop_path"),
                Str(e, mediaType == "tv" ? "first_air_date" : "release_date"),
                va,
                genres));
        }

        return list;
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static string Seg(string mediaType) => mediaType == "tv" ? "tv" : "movie";

    private async Task<JsonDocument?> GetAsync(string path, string? extraQuery, CancellationToken ct)
        => (await GetCoreAsync(path, extraQuery, ct).ConfigureAwait(false)).Doc;

    /// <summary>Doc is null on any failure; Failed is true when TMDB could not be asked (as opposed to "not found"/no key).</summary>
    private async Task<(JsonDocument? Doc, bool Failed)> GetCoreAsync(string path, string? extraQuery, CancellationToken ct)
    {
        var cfg = _config.Current;
        var key = cfg.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return (null, false);
        }

        var lang = string.IsNullOrWhiteSpace(cfg.TmdbLanguage) ? "en-US" : cfg.TmdbLanguage;
        var region = string.IsNullOrWhiteSpace(cfg.TmdbRegion) ? "US" : cfg.TmdbRegion;
        // include_adult=false on every call: adult titles must never reach a family server's lists.
        var query = "language=" + Uri.EscapeDataString(lang) + "&region=" + Uri.EscapeDataString(region) + "&include_adult=false"
            + (string.IsNullOrEmpty(extraQuery) ? string.Empty : "&" + extraQuery);

        // Cache key deliberately excludes the credential.
        var cacheKey = path + "?" + query;
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.at < _ttl)
        {
            return (JsonDocument.Parse(hit.text), false);
        }

        // Several failures in a row (TMDB down, no internet): stop asking for a while instead of waiting out every timeout.
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _blockedUntilTicks))
        {
            return (null, true);
        }

        var bearer = IsBearer(key);
        var url = Base + path + "?" + query + (bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(key));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, url);
                if (bearer)
                {
                    msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }

                HttpResponseMessage resp;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                try
                {
                    using var client = _http.CreateClient("FullUI");
                    resp = await client.SendAsync(msg, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
                {
                    _log.LogWarning("FullUI: TMDB request to {Path} failed ({Type})", path, ex.GetType().Name);
                    return (null, RecordFailure());
                }

                using (resp)
                {
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < 3)
                    {
                        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                        if (wait > TimeSpan.FromSeconds(15))
                        {
                            wait = TimeSpan.FromSeconds(15);
                        }

                        await _sleep(wait, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        KeyRejected = true;
                        _log.LogWarning("FullUI: TMDB rejected the API key (HTTP {Status}). Copy it again from themoviedb.org/settings/api", (int)resp.StatusCode);
                        return (null, true);
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        _log.LogDebug("FullUI: TMDB {Path} not found", path);
                        return (null, false); // the title does not exist on TMDB: a definitive "nothing there", not an outage
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        _log.LogWarning("FullUI: TMDB {Path} returned HTTP {Status}", path, (int)resp.StatusCode);
                        return (null, RecordFailure());
                    }

                    var text = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    var doc = JsonDocument.Parse(text);
                    KeyRejected = false;
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    Remember(cacheKey, text);
                    await _sleep(_delay, ct).ConfigureAwait(false);
                    return (doc, false);
                }
            }

            return (null, RecordFailure());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Malformed JSON, dropped connection, anything else: the feature is simply unavailable this time.
            _log.LogWarning("FullUI: TMDB response for {Path} could not be used ({Type})", path, ex.GetType().Name);
            return (null, RecordFailure());
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= FailureBudget)
        {
            Interlocked.Exchange(ref _blockedUntilTicks, (DateTime.UtcNow + FailureBackOff).Ticks);
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _log.LogWarning("FullUI: TMDB is not answering; pausing TMDB requests for {Minutes} minutes", (int)FailureBackOff.TotalMinutes);
        }

        return true;
    }

    private void Remember(string cacheKey, string text)
    {
        _cache[cacheKey] = (DateTime.UtcNow, text);
        if (_cache.Count <= MaxCacheEntries)
        {
            return;
        }

        foreach (var k in _cache.OrderBy(kv => kv.Value.at).Take(_cache.Count - (MaxCacheEntries * 3 / 4)).Select(kv => kv.Key).ToList())
        {
            _cache.TryRemove(k, out _);
        }
    }
}
