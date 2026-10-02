using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Api;

public sealed record ApiError(string Message);

/// <summary>
/// Keeps controller failures friendly: log the details server-side, return a short plain-English message
/// (never an exception text, stack trace, URL or key).
/// </summary>
internal static class SafeApi
{
    public static ActionResult Fail(ControllerBase c, ILogger log, Exception ex, string what)
    {
        log.LogWarning(ex, "FullUI: {What} failed", what);
        return c.StatusCode(500, new ApiError($"Something went wrong while {what}. Please try again in a moment. The server log has the details."));
    }

    public static ActionResult Run(ControllerBase c, ILogger log, string what, Func<ActionResult> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return Fail(c, log, ex, what);
        }
    }

    public static ActionResult<T> Run<T>(ControllerBase c, ILogger log, string what, Func<ActionResult<T>> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex)
        {
            return Fail(c, log, ex, what);
        }
    }

    public static async Task<ActionResult<T>> RunAsync<T>(ControllerBase c, ILogger log, string what, Func<Task<ActionResult<T>>> f)
    {
        try
        {
            return await f().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return c.StatusCode(499);
        }
        catch (Exception ex)
        {
            return Fail(c, log, ex, what);
        }
    }
}
