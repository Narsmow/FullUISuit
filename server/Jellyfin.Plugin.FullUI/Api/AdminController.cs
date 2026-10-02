using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

[ApiController]
[Route("FullUI")]
[Authorize(Policy = "RequiresElevation")]
public class AdminController : ControllerBase
{
    private static int _rebuilding;

    private readonly RequestService _requests;
    private readonly IOllamaClient _ollama;
    private readonly ICatalog _catalog;
    private readonly ComingSoonService _comingSoon;
    private readonly EmbeddingIndexer _indexer;
    private readonly RowTitleGenerator _titles;
    private readonly ILogger<AdminController> _log;

    public AdminController(
        RequestService requests,
        IOllamaClient ollama,
        ICatalog catalog,
        ComingSoonService comingSoon,
        EmbeddingIndexer indexer,
        RowTitleGenerator titles,
        ILogger<AdminController> log)
    {
        _requests = requests;
        _ollama = ollama;
        _catalog = catalog;
        _comingSoon = comingSoon;
        _indexer = indexer;
        _titles = titles;
        _log = log;
    }

    public sealed record StatusRequest(int TmdbId, string MediaType, string Status, string? Note);

    public sealed record OllamaTestRequest(string? Url, string? EmbedModel, string? ChatModel);

    [HttpGet("Admin/Requests")]
    public IActionResult GetRequests([FromQuery] string? sort)
        => SafeApi.Run(_log, "loading requests", () => SafeApi.Json(_requests.Aggregate(sort)));

    [HttpPost("Admin/Requests/Status")]
    public IActionResult SetStatus([FromBody] StatusRequest? request)
        => SafeApi.Run(_log, "saving the request status", () =>
            request is not null && _requests.SetStatus(request.TmdbId, request.MediaType, request.Status, request.Note)
                ? NoContent()
                : SafeApi.Error(StatusCodes.Status400BadRequest, "That status was not valid. Use Requested, Getting it or Added."));

    /// <summary>CSV export. Starts with a UTF-8 byte order mark so Excel shows non-ASCII titles correctly.</summary>
    [HttpGet("Admin/Requests.csv")]
    public IActionResult GetCsv([FromQuery] string? sort)
        => SafeApi.Run(_log, "exporting the CSV", () =>
        {
            var body = Encoding.UTF8.GetBytes(RequestService.ToCsv(_requests.Aggregate(sort)));
            var preamble = Encoding.UTF8.GetPreamble();
            var bytes = new byte[preamble.Length + body.Length];
            preamble.CopyTo(bytes, 0);
            body.CopyTo(bytes, preamble.Length);
            return File(bytes, "text/csv; charset=utf-8", "fullui-requests.csv");
        });

    [HttpPost("Admin/Rebuild")]
    public IActionResult Rebuild()
        => SafeApi.Run(_log, "starting the rebuild", RebuildCore);

    /// <summary>Whether the FullUI web reskin is hooked into the Jellyfin web page (shown on the settings page).</summary>
    [HttpGet("Admin/Injection")]
    public IActionResult Injection()
        => SafeApi.Run(_log, "checking the web interface hook", () => SafeApi.Json(WebInjection.Status.Describe()));

    private IActionResult RebuildCore()
    {
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) != 0)
        {
            return Accepted();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _catalog.InvalidateNow();
                _requests.Sync(_catalog.All);
                await _comingSoon.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
                await _indexer.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
                await _titles.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "FullUI: manual rebuild failed");
            }
            finally
            {
                Interlocked.Exchange(ref _rebuilding, 0);
            }
        });
        return Accepted();
    }

    [HttpPost("Ollama/Test")]
    public Task<IActionResult> TestOllama([FromBody] OllamaTestRequest? request, CancellationToken ct)
        => SafeApi.RunAsync(_log, "testing Ollama", async () =>
        {
            var (ok, message) = await _ollama.TestAsync(request?.Url, request?.EmbedModel, request?.ChatModel, ct).ConfigureAwait(false);
            return SafeApi.Json(new FullUIController.TestResult(ok, message));
        });
}
