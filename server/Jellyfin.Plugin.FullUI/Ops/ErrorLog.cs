using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.FullUI.Ops;

public sealed record FriendlyError(DateTime At, string Message);

/// <summary>
/// The last 50 plain-English problem messages, newest last, kept in memory for the health page. Messages are scrubbed of
/// anything that looks like a key, token, URL or file path before they are stored, so the page can never leak a secret.
/// </summary>
public static class ErrorLog
{
    public const int Capacity = 50;

    private static readonly object Gate = new();
    private static readonly Queue<FriendlyError> Items = new();
    private static readonly Regex Url = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Secret = new(@"(?i)(api[_-]?key|token|secret|password|bearer)\s*[=:]?\s*\S+", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"\b[A-Za-z0-9_\-]{24,}\b", RegexOptions.Compiled);
    private static readonly Regex WinPath = new(@"[A-Za-z]:\\\S*", RegexOptions.Compiled);
    private static readonly Regex UnixPath = new(@"(?<![\w.])/(?:[\w.\-]+/)+[\w.\-]*", RegexOptions.Compiled);

    public static void Add(string message) => Add(message, DateTime.UtcNow);

    public static void Add(string message, DateTime at)
    {
        try
        {
            var clean = Scrub(message);
            if (clean.Length == 0)
            {
                return;
            }

            lock (Gate)
            {
                // The same message repeated back-to-back (a failing page polled every few seconds) is shown once.
                if (Items.Count > 0 && Items.Last().Message == clean)
                {
                    return;
                }

                Items.Enqueue(new FriendlyError(at, clean));
                while (Items.Count > Capacity)
                {
                    Items.Dequeue();
                }
            }
        }
        catch (Exception)
        {
            // Recording a problem must never cause one.
        }
    }

    /// <summary>Newest first.</summary>
    public static IReadOnlyList<FriendlyError> Recent()
    {
        lock (Gate)
        {
            return Items.Reverse().ToList();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Items.Clear();
        }
    }

    internal static string Scrub(string? message)
    {
        var s = (message ?? string.Empty).Trim();
        s = Url.Replace(s, "[link]");
        s = Secret.Replace(s, "$1 [hidden]");
        s = WinPath.Replace(s, "[path]");
        s = UnixPath.Replace(s, "[path]");
        s = LongToken.Replace(s, "[hidden]");
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > 300 ? s[..300] : s;
    }
}
