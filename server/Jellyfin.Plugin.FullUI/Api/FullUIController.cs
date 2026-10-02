using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FullUI.Api;

[ApiController]
[Route("FullUI")]
public class FullUIController : ControllerBase
{
    private readonly IHttpClientFactory _http;

    public FullUIController(IHttpClientFactory http)
    {
        _http = http;
    }

    public record TmdbTestRequest(string? ApiKey);

    public record TestResult(bool Ok, string Message);

    /// <summary>
    /// Admin-only: checks a TMDB v3 key or v4 read token. Falls back to the saved key when none is posted.
    /// </summary>
    [HttpPost("Tmdb/Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<TestResult>> TestTmdb([FromBody] TmdbTestRequest request, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(request.ApiKey)
            ? Plugin.Instance?.Configuration.TmdbApiKey
            : request.ApiKey;
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return new TestResult(false, "No TMDB API key entered.");
        }

        var isBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = "https://api.themoviedb.org/3/configuration" + (isBearer ? string.Empty : "?api_key=" + Uri.EscapeDataString(key));
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        if (isBearer)
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        try
        {
            using var client = _http.CreateClient("FullUI");
            using var resp = await client.SendAsync(msg, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? new TestResult(true, "Connected to TMDB.")
                : new TestResult(false, $"TMDB rejected the key (HTTP {(int)resp.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new TestResult(false, "Could not reach TMDB: " + ex.Message);
        }
    }

    /// <summary>Lightweight status for the web client; never exposes secrets.</summary>
    [HttpGet("Status")]
    [Authorize]
    public ActionResult<object> Status()
    {
        var cfg = Plugin.Instance?.Configuration;
        return new
        {
            serverName = cfg?.ServerName ?? "FullUI",
            accentColor = cfg?.AccentColor ?? "#e50914",
            tmdbConfigured = !string.IsNullOrWhiteSpace(cfg?.TmdbApiKey),
            ollamaEnabled = cfg?.OllamaEnabled ?? false,
        };
    }

    [HttpGet("web/{file}")]
    [AllowAnonymous]
    public ActionResult WebAsset(string file)
    {
        var (name, type) = file switch
        {
            "fullui.js" => ("fullui.js", "application/javascript"),
            "fullui.css" => ("fullui.css", "text/css"),
            _ => (string.Empty, string.Empty),
        };
        if (name.Length == 0)
        {
            return NotFound();
        }

        var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}");
        return stream is null ? NotFound() : File(stream, type);
    }
}
