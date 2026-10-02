using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>
/// Implemented by the home/engine layer. Optional: MaterializePlaylistsTask does nothing when no implementation is
/// registered. Titles are the row names without the "FullUI: " prefix (e.g. "Top Picks").
/// </summary>
public interface IHomeRowsProvider
{
    IReadOnlyList<(string title, IReadOnlyList<Guid> itemIds)> GetPlaylistRows(Guid userId);
}
