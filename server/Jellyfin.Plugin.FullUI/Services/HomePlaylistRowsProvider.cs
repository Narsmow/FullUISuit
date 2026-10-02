using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.FullUI.Discovery;

namespace Jellyfin.Plugin.FullUI.Services;

/// <summary>
/// Feeds the "FullUI:" playlists for clients that cannot show the reskin. Uses the very same rows as the FullUI home,
/// minus the ones native clients already have (Continue Watching, My List) and Coming Soon (not library items).
/// </summary>
public sealed class HomePlaylistRowsProvider : IHomeRowsProvider
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "continue", "mylist", "comingsoon" };

    private readonly HomeService _home;

    public HomePlaylistRowsProvider(HomeService home)
    {
        _home = home;
    }

    public IReadOnlyList<(string title, IReadOnlyList<Guid> itemIds)> GetPlaylistRows(Guid userId)
    {
        var rows = new List<(string title, IReadOnlyList<Guid> itemIds)>();
        foreach (var row in _home.GetHome(userId).Rows)
        {
            if (Skipped.Contains(row.Type))
            {
                continue;
            }

            var ids = row.Items
                .Select(c => Guid.TryParse(c.Id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .Distinct()
                .ToList();
            if (ids.Count > 0)
            {
                rows.Add((row.Title, ids));
            }
        }

        return rows;
    }
}
