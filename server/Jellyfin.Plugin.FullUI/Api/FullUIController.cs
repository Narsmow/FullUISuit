using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Configuration;
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
    public async Task<IActionResult> TestTmdb([FromBody] TmdbTestRequest? request, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(request?.ApiKey)
            ? Plugin.Instance?.Configuration.TmdbApiKey
            : request!.ApiKey;
        key = key?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return SafeApi.Json(new TestResult(false, "No TMDB API key entered."));
        }

        var isBearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var url = "https://api.themoviedb.org/3/configuration" + (isBearer ? string.Empty : "?api_key=" + Uri.EscapeDataString(key));
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        if (isBearer)
        {
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = _http.CreateClient("FullUI");
            using var resp = await client.SendAsync(msg, cts.Token).ConfigureAwait(false);
            return SafeApi.Json(resp.IsSuccessStatusCode
                ? new TestResult(true, "Connected to TMDB.")
                : new TestResult(false, $"TMDB rejected the key (HTTP {(int)resp.StatusCode})."));
        }
        catch (Exception ex) when ((ex is HttpRequestException || ex is OperationCanceledException) && !ct.IsCancellationRequested)
        {
            // Never echo the exception text: it can contain the request URL, which carries a v3 key.
            return SafeApi.Json(new TestResult(false, "Could not reach TMDB. Check the server's internet connection and try again."));
        }
    }

    /// <summary>Lightweight status for signed-in clients; never exposes secrets.</summary>
    [HttpGet("Status")]
    [Authorize]
    public IActionResult Status()
    {
        var cfg = Plugin.Instance?.Configuration;
        return SafeApi.Json(new
        {
            serverName = string.IsNullOrWhiteSpace(cfg?.ServerName) ? "FullUI" : cfg!.ServerName,
            accentColor = Branding.NormalizeAccent(cfg?.AccentColor),
            tmdbConfigured = !string.IsNullOrWhiteSpace(cfg?.TmdbApiKey),
            ollamaEnabled = cfg?.OllamaEnabled ?? false,
            trailersEnabled = cfg?.TrailersEnabled ?? true,
            webInjected = WebInjection.Status.Registered,
        });
    }

    /// <summary>
    /// Serves the embedded bundle. It is revalidated on every load (ETag = the build's module id, so an upgraded
    /// plugin is picked up immediately and an unchanged one answers 304 without re-sending the bundle).
    /// </summary>
    [HttpGet("web/{file}")]
    [AllowAnonymous]
    public IActionResult WebAsset(string file)
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

        var asm = typeof(Plugin).Assembly;
        var stream = asm.GetManifestResourceStream($"{typeof(Plugin).Namespace}.Web.{name}");
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers[Microsoft.Net.Http.Headers.HeaderNames.CacheControl] = "no-cache";
        var etag = new Microsoft.Net.Http.Headers.EntityTagHeaderValue("\"" + asm.ManifestModule.ModuleVersionId.ToString("N") + "-" + name + "\"");
        return File(stream, type, lastModified: null, entityTag: etag);
    }
}
