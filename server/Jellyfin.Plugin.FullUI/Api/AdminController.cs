using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Ai;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.AspNetCore.Authorization;
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
    public ActionResult<IReadOnlyList<RequestRow>> GetRequests([FromQuery] string? sort)
        => SafeApi.Run<IReadOnlyList<RequestRow>>(this, _log, "loading requests", () => Ok(_requests.Aggregate(sort)));

    [HttpPost("Admin/Requests/Status")]
    public ActionResult SetStatus([FromBody] StatusRequest? request)
        => SafeApi.Run(this, _log, "saving the request status", () =>
            request is not null && _requests.SetStatus(request.TmdbId, request.MediaType, request.Status, request.Note)
                ? NoContent()
                : BadRequest(new ApiError("That status was not valid. Use Requested, Getting it or Added.")));

    [HttpGet("Admin/Requests.csv")]
    public ActionResult GetCsv([FromQuery] string? sort)
        => SafeApi.Run(this, _log, "exporting the CSV", () =>
            File(Encoding.UTF8.GetBytes(RequestService.ToCsv(_requests.Aggregate(sort))), "text/csv; charset=utf-8", "fullui-requests.csv"));

    [HttpPost("Admin/Rebuild")]
    public ActionResult Rebuild()
        => SafeApi.Run(this, _log, "starting the rebuild", RebuildCore);

    private ActionResult RebuildCore()
    {
        if (Interlocked.CompareExchange(ref _rebuilding, 1, 0) != 0)
        {
            return Accepted();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                _catalog.Invalidate();
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
    public Task<ActionResult<FullUIController.TestResult>> TestOllama([FromBody] OllamaTestRequest? request, CancellationToken ct)
        => SafeApi.RunAsync(this, _log, "testing Ollama", async () =>
        {
            var (ok, message) = await _ollama.TestAsync(request?.Url, request?.EmbedModel, request?.ChatModel, ct).ConfigureAwait(false);
            return (ActionResult<FullUIController.TestResult>)new FullUIController.TestResult(ok, message);
        });
}
