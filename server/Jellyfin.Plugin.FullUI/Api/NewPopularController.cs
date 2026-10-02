using System;
using System.Linq;
using Jellyfin.Plugin.FullUI.Discovery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

public sealed record RemindRequest(int TmdbId, string MediaType, bool On);

public sealed record ReminderDto(int TmdbId, string MediaType, string Title, string? PosterPath, string? ReleaseDate, int? SeasonNumber, bool Notified);

public sealed record RemindersResponse(System.Collections.Generic.IReadOnlyList<ReminderDto> Items);

/// <summary>The New &amp; Popular page and "Remind me". Everything is per signed-in user.</summary>
[ApiController]
[Route("FullUI")]
[Authorize]
public class NewPopularController : ControllerBase
{
    private readonly NewPopularService _page;
    private readonly ReminderService _reminders;
    private readonly IConfigSource _config;
    private readonly ILogger<NewPopularController> _log;

    public NewPopularController(NewPopularService page, ReminderService reminders, IConfigSource config, ILogger<NewPopularController> log)
    {
        _page = page;
        _reminders = reminders;
        _config = config;
        _log = log;
    }

    [HttpGet("NewPopular")]
    public IActionResult NewPopular()
        => SafeApi.Run(_log, "loading New & Popular", () =>
            UserClaim.TryGet(User, out var userId)
                ? SafeApi.Json(_page.Build(userId, !string.IsNullOrWhiteSpace(_config.Current.TmdbApiKey), DateTime.UtcNow))
                : Unauthorized());

    [HttpPost("Remind")]
    public IActionResult Remind([FromBody] RemindRequest? request)
        => SafeApi.Run(_log, "saving your reminder", () =>
        {
            if (!UserClaim.TryGet(User, out var userId))
            {
                return Unauthorized();
            }

            if (request is null)
            {
                return SafeApi.Error(StatusCodes.Status400BadRequest, "That reminder could not be read.");
            }

            return _reminders.Set(userId, request.TmdbId, request.MediaType ?? string.Empty, request.On, DateTime.UtcNow) switch
            {
                RemindResult.Ok => NoContent(),
                RemindResult.LimitReached => SafeApi.Error(StatusCodes.Status400BadRequest, $"You have reached the limit of {ReminderService.MaxRemindersPerUser} reminders. Remove an older one first."),
                _ => SafeApi.Error(StatusCodes.Status400BadRequest, "That title is not on your Coming Soon list, so a reminder cannot be set for it."),
            };
        });

    [HttpGet("Reminders")]
    public IActionResult Reminders()
        => SafeApi.Run(_log, "loading your reminders", () =>
            UserClaim.TryGet(User, out var userId)
                ? SafeApi.Json(new RemindersResponse(_reminders.List(userId)
                    .Select(r => new ReminderDto(r.TmdbId, r.MediaType, r.Title, r.PosterPath, r.ReleaseDate, r.SeasonNumber, r.Notified)).ToList()))
                : Unauthorized());
}
