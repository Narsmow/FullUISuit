using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Data;

namespace Jellyfin.Plugin.FullUI.Recs;

/// <summary>How far one user is through one series. Specials (season 0) never count.</summary>
public sealed class SeriesState
{
    public Guid SeriesId { get; init; }

    /// <summary>Distinct episodes watched (capped at <see cref="Total"/> when that is known).</summary>
    public int Watched { get; init; }

    /// <summary>Real episodes in the library; 0 when the library has no episode data for the series.</summary>
    public int Total { get; init; }

    public bool TotalKnown => Total > 0;

    public int HighestSeason { get; init; }

    public IReadOnlyDictionary<int, int> BySeason { get; init; } = new Dictionary<int, int>();

    /// <summary>Time of the user's newest play of this series (specials ignored).</summary>
    public DateTime Last { get; init; }

    public bool Started { get; init; }

    /// <summary>Share of the series watched, 0..1 (estimated from the count alone when the library has no episode data).</summary>
    public double Share { get; init; }

    /// <summary>Nothing left to watch right now: every existing episode is watched. Never true when the library has no episode data for the series.</summary>
    public bool CaughtUp { get; init; }

    /// <summary>Started, never got far, and has not been touched for weeks: stop recommending or continuing it.</summary>
    public bool Dropped { get; init; }

    public int? LastSeason { get; init; }

    public int? LastEpisode { get; init; }
}

public static class SeriesStates
{
    /// <summary>Series with fewer than this share watched can be called dropped once stale.</summary>
    public const double DropShare = 0.5;
    public const int DropAfterDays = 45;
    public const int DropEarlyAfterDays = 21;

    /// <summary>The state of every series the user has any episode signal for.</summary>
    public static Dictionary<Guid, SeriesState> Compute(
        RecInput input,
        CatalogIndex index,
        IEnumerable<PlaySignal> userSignals,
        Func<Guid, int> rating,
        Func<Guid, bool> inMyList,
        bool useWatchSource)
    {
        var result = new Dictionary<Guid, SeriesState>();
        foreach (var grp in userSignals.Where(s => s.IsEpisode && s.Season != 0).GroupBy(s => s.ItemId))
        {
            var id = grp.Key;
            var bySeason = new Dictionary<int, int>();
            var pairs = new Dictionary<int, HashSet<int>>();
            var legacy = 0;
            var started = false;
            PlaySignal? latest = null;
            foreach (var s in grp)
            {
                if (latest is null || s.At > latest.At)
                {
                    latest = s;
                }

                if (s.Completion >= 0.03 || s.Completed)
                {
                    started = true;
                }

                if (!(s.Completed || s.Completion >= 0.9))
                {
                    continue;
                }

                if (s.Season is int sn && s.Episode is int en)
                {
                    if (!pairs.TryGetValue(sn, out var set))
                    {
                        pairs[sn] = set = new HashSet<int>();
                    }

                    set.Add(en);
                }
                else
                {
                    legacy++;
                }
            }

            foreach (var (sn, set) in pairs)
            {
                bySeason[sn] = set.Count;
            }

            var hasSource = false;
            if (useWatchSource && input.SeriesWatch.TryGetValue(id, out var src))
            {
                hasSource = true;
                foreach (var (sn, n) in src.EpisodesBySeason)
                {
                    if (sn > 0)
                    {
                        bySeason[sn] = Math.Max(bySeason.GetValueOrDefault(sn), n);
                    }
                }
            }

            // Signals from older versions know no season: each finished one counts as one episode, unless Jellyfin told us better.
            if (!hasSource && legacy > 0)
            {
                bySeason[-1] = legacy;
            }

            var total = index.ById.TryGetValue(id, out var item) ? item.EpisodeCount : 0;
            var watched = bySeason.Values.Sum();
            if (total > 0)
            {
                watched = Math.Min(watched, total);
            }

            var highest = bySeason.Where(kv => kv.Key > 0 && kv.Value > 0).Select(kv => kv.Key).DefaultIfEmpty(0).Max();
            var share = total > 0 ? Math.Clamp((double)watched / total, 0, 1) : Math.Clamp(watched / 12.0, 0, 1);
            var caughtUp = watched > 0 && total > 0 && watched >= total - (total / 30);
            var last = latest!.At;
            var staleDays = (input.Now - last).TotalDays;
            var dropped = (started || watched > 0) && !caughtUp && share < DropShare
                && (staleDays >= DropAfterDays || (watched <= 2 && staleDays >= DropEarlyAfterDays))
                && rating(id) <= 0 && !inMyList(id);

            result[id] = new SeriesState
            {
                SeriesId = id,
                Watched = watched,
                Total = total,
                HighestSeason = highest,
                BySeason = bySeason,
                Last = last,
                Started = started || watched > 0,
                Share = share,
                CaughtUp = caughtUp,
                Dropped = dropped,
                LastSeason = latest.Season,
                LastEpisode = latest.Episode,
            };
        }

        return result;
    }
}
