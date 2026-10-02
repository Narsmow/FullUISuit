using System;
using System.Linq;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Metrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>Impression / interaction events from the web client, and the admin's aggregate view of them.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class MetricsController : ControllerBase
{
    private readonly InteractionLog _events;
    private readonly MetricsService _metrics;
    private readonly EventRateLimiter _limiter;
    private readonly IConfigSource _config;
    private readonly ILogger<MetricsController> _log;

    public MetricsController(InteractionLog events, MetricsService metrics, EventRateLimiter limiter, IConfigSource config, ILogger<MetricsController> log)
    {
        _events = events;
        _metrics = metrics;
        _limiter = limiter;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Batch of up to 200 events. Whose events they are comes from the sign-in token only. When the admin has switched
    /// collection off, the batch is accepted and discarded (200, accepted 0) so the page does not need to know.
    /// </summary>
    [HttpPost("Events")]
    public IActionResult PostEvents([FromBody] EventsRequest? request)
        => SafeApi.Run(_log, "recording usage", () =>
        {
            if (!UserClaim.TryGet(User, out var userId))
            {
                return Unauthorized();
            }

            var batch = request?.Events;
            if (batch is null || batch.Length == 0)
            {
                return SafeApi.Json(new EventsResult(0, 0));
            }

            if (batch.Length > EventValidator.MaxBatch)
            {
                return SafeApi.Error(StatusCodes.Status400BadRequest, $"Send at most {EventValidator.MaxBatch} events at a time.");
            }

            if (!_config.Current.CollectInteractionMetrics)
            {
                return SafeApi.Json(new EventsResult(0, batch.Length));
            }

            var now = DateTime.UtcNow;
            if (!_limiter.TryTake(userId, batch.Length, now))
            {
                Response.Headers["Retry-After"] = "60";
                return SafeApi.Error(StatusCodes.Status429TooManyRequests, "Too many events were sent. Try again in a minute.");
            }

            var good = batch.Select(e => EventValidator.Validate(userId, e, now)).OfType<StoredEvent>().ToList();
            _events.Append(good);
            return SafeApi.Json(new EventsResult(good.Count, batch.Length - good.Count));
        });

    /// <summary>Admin: aggregates only (take rate per row, plays, never-clicked rows, top searches, events per day).</summary>
    [HttpGet("Admin/Metrics")]
    [Authorize(Policy = "RequiresElevation")]
    public IActionResult Metrics([FromQuery] int? days)
        => SafeApi.Run(_log, "loading the usage statistics", () => SafeApi.Json(_metrics.Report(DateTime.UtcNow, days ?? 30)));
}
