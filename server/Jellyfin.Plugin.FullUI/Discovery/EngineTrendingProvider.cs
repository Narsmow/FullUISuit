using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;
using Jellyfin.Plugin.FullUI.Library;
using Jellyfin.Plugin.FullUI.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// The New &amp; Popular lists as the recommendation engine ranks them for this user: the same Trending and Top 10 rows as on Home
/// (per-user eligible: visible, not thumbed down, not finished or dropped; kids and excluded users left out of the shared signals;
/// duplicate editions merged), so the two screens never disagree. Falls back to <see cref="SignalTrendingProvider"/> whenever the
/// engine cannot answer (it throws, or the household is too small for the engine to show that row).
/// </summary>
public sealed class EngineTrendingProvider : ITrendingProvider
{
    internal const string TrendingRow = "trending";
    internal const string TopMoviesRow = "top10-movies";
    internal const string TopShowsRow = "top10-shows";

    private readonly HomeService _home;
    private readonly ITrendingProvider _fallback;
    private readonly ILogger<EngineTrendingProvider> _log;

    public EngineTrendingProvider(HomeService home, ITrendingProvider fallback, ILogger<EngineTrendingProvider> log)
    {
        _home = home;
        _fallback = fallback;
        _log = log;
    }

    public IReadOnlyList<Guid> Trending(Guid userId, int take)
        => FromEngine(userId, TrendingRow, take) ?? _fallback.Trending(userId, take);

    public IReadOnlyList<Guid> TopTen(Guid userId, CatalogKind kind)
        => FromEngine(userId, kind == CatalogKind.Movie ? TopMoviesRow : TopShowsRow, 10) ?? _fallback.TopTen(userId, kind);

    /// <summary>The ids of a home row, or null when the engine has no such row for this user (or failed).</summary>
    private IReadOnlyList<Guid>? FromEngine(Guid userId, string rowId, int take)
    {
        try
        {
            var row = _home.GetHomeStrict(userId).Rows.FirstOrDefault(r => r.Id == rowId);
            if (row is null || row.Items.Count == 0)
            {
                return null;
            }

            var ids = row.Items.Select(c => Guid.TryParseExact(c.Id, "N", out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty).Distinct().Take(Math.Max(0, take)).ToList();
            return ids.Count == 0 ? null : ids;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: the engine's {Row} list is unavailable; using play signals instead", rowId);
            return null;
        }
    }
}
