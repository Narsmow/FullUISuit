using System;
using System.Text.Json;
using Jellyfin.Plugin.FullUI.Ops;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>Body of <c>POST Admin/Purge</c>: <c>userId</c> is a user id or the word "all"; <c>confirm</c> is the token from the first call.</summary>
public sealed record PurgeRequest(string? UserId, string? Confirm);

/// <summary>Admin only: the health page's data, backup (export / import) and "delete data".</summary>
[ApiController]
[Route("FullUI")]
[Authorize(Policy = "RequiresElevation")]
public class AdminOpsController : ControllerBase
{
    private const int MaxImportBytes = 20 * 1024 * 1024;

    private static readonly JsonSerializerOptions Pretty = new(SafeApi.CamelCase) { WriteIndented = true };

    private readonly HealthService _health;
    private readonly DataPortability _data;
    private readonly ILogger<AdminOpsController> _log;

    public AdminOpsController(HealthService health, DataPortability data, ILogger<AdminOpsController> log)
    {
        _health = health;
        _data = data;
        _log = log;
    }

    [HttpGet("Admin/Health")]
    public IActionResult Health()
        => SafeApi.Run(_log, "checking FullUI's health", () => SafeApi.Json(_health.Build(Clock.UtcNow)));

    /// <summary>A JSON backup file of ratings, My List, votes, request statuses, notifications, reminders and onboarding choices. <c>user</c> limits it to one user.</summary>
    [HttpGet("Admin/Export")]
    public IActionResult Export([FromQuery] string? user)
        => SafeApi.Run(_log, "exporting the data", () =>
        {
            Guid? only = null;
            if (!string.IsNullOrWhiteSpace(user) && !string.Equals(user, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(user, out var g))
                {
                    return SafeApi.Error(StatusCodes.Status400BadRequest, "That user id could not be read.");
                }

                only = g;
            }

            var now = DateTime.UtcNow;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_data.Export(only, now), Pretty);
            return File(bytes, "application/json", $"fullui-export-{now:yyyy-MM-dd}.json");
        });

    /// <summary>
    /// Restores a backup. <c>dryRun</c> defaults to true: it only reports what would happen. Pass <c>dryRun=false</c> to apply it.
    /// Bodies over 20 MB are refused.
    /// </summary>
    [HttpPost("Admin/Import")]
    [RequestSizeLimit(MaxImportBytes)]
    public IActionResult Import([FromBody] ExportDocument? request, [FromQuery] bool dryRun = true)
        => SafeApi.Run(_log, "importing the backup", () =>
        {
            if (request is null)
            {
                return SafeApi.Error(StatusCodes.Status400BadRequest, "That backup file could not be read.");
            }

            var result = _data.Import(request, dryRun);
            return SafeApi.Json(result, result.Valid ? null : StatusCodes.Status400BadRequest);
        });

    /// <summary>"Delete my data" / uninstall clean-up. The first call (no <c>confirm</c>) deletes nothing and returns a token; send it back to confirm.</summary>
    [HttpPost("Admin/Purge")]
    public IActionResult Purge([FromBody] PurgeRequest? request)
        => SafeApi.Run(_log, "deleting the data", () =>
        {
            var result = request is null ? null : _data.Purge(request.UserId, request.Confirm, DateTime.UtcNow);
            return result is null
                ? SafeApi.Error(StatusCodes.Status400BadRequest, "Say which user to delete (a user id), or \"all\".")
                : SafeApi.Json(result);
        });
}
