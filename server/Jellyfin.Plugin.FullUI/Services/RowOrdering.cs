using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Api;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>
/// Netflix's biggest lever is row order. The contract order stays, but the discovery rows swap places among their own
/// slots so a user who acts on "Trending" more than on "Because you watched" sees Trending first. Anchor rows (Continue,
/// Top Picks, charts, My List, Coming Soon) never move.
/// </summary>
public static class RowOrdering
{
    private static readonly HashSet<string> Movable = new(StringComparer.OrdinalIgnoreCase)
    {
        "because", "genre", "trending", "recent", "hidden", "again", "newseasons", "collection",
    };

    /// <summary>Returns the rows reordered by engagement; unchanged when there is no usable data.</summary>
    public static IReadOnlyList<HomeRow> Apply(IReadOnlyList<HomeRow> rows, IReadOnlyDictionary<string, double>? engagement)
    {
        if (engagement is null || engagement.Count == 0)
        {
            return rows;
        }

        var slots = new List<int>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (Movable.Contains(rows[i].Type))
            {
                slots.Add(i);
            }
        }

        // Need at least two kinds of movable row with data, otherwise there is nothing meaningful to compare.
        var known = slots.Select(i => rows[i].Type).Distinct(StringComparer.OrdinalIgnoreCase).Count(t => engagement.ContainsKey(t));
        if (slots.Count < 2 || known < 2)
        {
            return rows;
        }

        var neutral = Math.Round(engagement.Where(kv => Movable.Contains(kv.Key)).Select(kv => kv.Value).DefaultIfEmpty(0.5).Average(), 2);
        double Score(HomeRow r) => Math.Round(engagement.TryGetValue(r.Type, out var s) ? s : neutral, 2);

        // OrderByDescending is stable, so equal scores keep the contract order.
        var sorted = slots.Select(i => rows[i]).OrderByDescending(Score).ToList();
        var result = rows.ToList();
        for (var k = 0; k < slots.Count; k++)
        {
            result[slots[k]] = sorted[k];
        }

        return result;
    }
}
