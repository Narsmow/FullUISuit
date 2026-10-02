using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

public sealed record ApiError(string Message);

/// <summary>
/// Shared plumbing for every FullUI action. <see cref="Json"/> is THE way to return a body: Jellyfin's own MVC
/// formatter is PascalCase, the API contract is camelCase, so each action serializes with these options explicitly.
/// Failures stay friendly: log the details server-side, return a short plain-English message
/// (never an exception text, stack trace, URL or key).
/// </summary>
internal static class SafeApi
{
    /// <summary>The one set of serializer options for every FullUI response (camelCase, no indentation).</summary>
    internal static readonly JsonSerializerOptions CamelCase = new(JsonSerializerDefaults.Web);

    /// <summary>200 (or the given status) with a camelCase JSON body.</summary>
    public static JsonResult Json(object? value, int? statusCode = null)
        => new(value, CamelCase) { StatusCode = statusCode };

    /// <summary>A short error body <c>{ "message": "..." }</c> with the given HTTP status.</summary>
    public static JsonResult Error(int statusCode, string message)
        => Json(new ApiError(message), statusCode);

    /// <summary>An RFC 7807 problem body (<c>title</c>, <c>status</c>, <c>detail</c>) with the given HTTP status.</summary>
    public static JsonResult Problem(int statusCode, string title, string detail)
        => new(new ProblemDetails { Status = statusCode, Title = title, Detail = detail }, CamelCase)
        {
            StatusCode = statusCode,
            ContentType = "application/problem+json",
        };

    public static IActionResult Fail(ILogger log, Exception ex, string what)
    {
        log.LogWarning(ex, "FullUI: {What} failed", what);
        return Error(StatusCodes.Status500InternalServerError, $"Something went wrong while {what}. Please try again in a moment. The server log has the details.");
    }

    public static IActionResult Run(ILogger log, string what, Func<IActionResult> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return Fail(log, ex, what);
        }
    }

    public static async Task<IActionResult> RunAsync(ILogger log, string what, Func<Task<IActionResult>> f)
    {
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new StatusCodeResult(499);
        }
        catch (Exception ex)
        {
            return Fail(log, ex, what);
        }
    }
}
