using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Data;

namespace Jellyfin.Plugin.FullUI.Discovery;

public enum VoteResult
{
    Ok,
    Invalid,

    /// <summary>The user already holds <see cref="VoteService.MaxVotesPerUser"/> votes (clearing a vote is always allowed).</summary>
    LimitReached,
}

/// <summary>Vote upsert + per-user views. A user only ever sees their own votes.</summary>
public sealed class VoteService
{
    private static readonly Regex PathRx = new(@"^/[A-Za-z0-9_\-\.]{1,120}$", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"^\d{4}(-\d{2}(-\d{2})?)?$", RegexOptions.Compiled);

    /// <summary>Upper bound on stored votes per user so one account cannot grow the store without limit.</summary>
    public const int MaxVotesPerUser = 500;

    private readonly PluginStore _store;

    public VoteService(PluginStore store)
    {
        _store = store;
    }

    public static string StatusKey(string mediaType, int tmdbId) => $"{mediaType}:{tmdbId}";

    public VoteResult Cast(Guid userId, VoteRequest req, DateTime? now = null)
    {
        if (userId == Guid.Empty || req.TmdbId is <= 0 or > 100_000_000 || req.Vote is < -1 or > 1
            || (req.MediaType != "movie" && req.MediaType != "tv"))
        {
            return VoteResult.Invalid;
        }

        var at = now ?? DateTime.UtcNow;
        var result = VoteResult.Ok;
        _store.Write(d =>
        {
            var replacing = d.Votes.Any(v => v.UserId == userId && v.TmdbId == req.TmdbId && v.MediaType == req.MediaType);
            if (req.Vote != 0 && !replacing && d.Votes.Count(v => v.UserId == userId) >= MaxVotesPerUser)
            {
                result = VoteResult.LimitReached;
                return;
            }

            d.Votes.RemoveAll(v => v.UserId == userId && v.TmdbId == req.TmdbId && v.MediaType == req.MediaType);
            if (req.Vote == 0)
            {
                return;
            }

            // Keep any previously stored title metadata when this request omits it.
            var title = Clamp(req.Title, 300);
            var prior = d.Votes.FirstOrDefault(v => v.TmdbId == req.TmdbId && v.MediaType == req.MediaType);
            d.Votes.Add(new VoteEntry
            {
                UserId = userId,
                TmdbId = req.TmdbId,
                MediaType = req.MediaType,
                Vote = req.Vote,
                Title = string.IsNullOrWhiteSpace(title) ? (prior?.Title ?? string.Empty) : title,
                PosterPath = SafePath(req.PosterPath) ?? prior?.PosterPath,
                BackdropPath = SafePath(req.BackdropPath) ?? prior?.BackdropPath,
                ReleaseDate = SafeDate(req.ReleaseDate) ?? prior?.ReleaseDate,
                Overview = Clamp(req.Overview, 2000) is { Length: > 0 } o ? o : prior?.Overview,
                At = at,
            });

            if (req.Vote == 1)
            {
                var sk = StatusKey(req.MediaType, req.TmdbId);
                if (!d.Statuses.ContainsKey(sk))
                {
                    d.Statuses[sk] = new RequestStatusEntry { Status = "Requested", UpdatedAt = at };
                }
            }
        });
        return result;
    }

    /// <summary>The caller's own vote per "{mediaType}:{tmdbId}".</summary>
    public IReadOnlyDictionary<string, int> MyVotes(Guid userId)
        => _store.Read(d => d.Votes.Where(v => v.UserId == userId).ToDictionary(v => StatusKey(v.MediaType, v.TmdbId), v => v.Vote));

    private static string? Clamp(string? s, int max)
    {
        if (s is null)
        {
            return null;
        }

        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static string? SafePath(string? p) => p is not null && PathRx.IsMatch(p) ? p : null;

    private static string? SafeDate(string? p) => p is not null && DateRx.IsMatch(p) ? p : null;
}
