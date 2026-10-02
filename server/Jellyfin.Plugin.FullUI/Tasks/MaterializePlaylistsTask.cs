using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FullUI.Discovery;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Tasks;

/// <summary>
/// For clients that ignore the reskin: writes "FullUI: &lt;row&gt;" playlists per user. Only runs when
/// MaterializePlaylists is on and an IHomeRowsProvider exists. Only touches playlists named "FullUI: *" owned by that user.
/// </summary>
public class MaterializePlaylistsTask : IScheduledTask
{
    public const string Prefix = "FullUI: ";

    private readonly IServiceProvider _sp;
    private readonly IConfigSource _config;
    private readonly IUserDirectory _users;
    private readonly IPlaylistManager _playlists;
    private readonly ILibraryManager _library;
    private readonly ILogger<MaterializePlaylistsTask> _log;

    public MaterializePlaylistsTask(
        IServiceProvider sp,
        IConfigSource config,
        IUserDirectory users,
        IPlaylistManager playlists,
        ILibraryManager library,
        ILogger<MaterializePlaylistsTask> log)
    {
        _sp = sp;
        _config = config;
        _users = users;
        _playlists = playlists;
        _library = library;
        _log = log;
    }

    public string Name => "FullUI: Write recommendation playlists";

    public string Key => "FullUIMaterializePlaylists";

    public string Description => "Creates per-user 'FullUI:' playlists for clients that cannot show the FullUI home screen.";

    public string Category => "FullUI";

    public static string PlaylistName(string rowTitle)
        => rowTitle.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? rowTitle : Prefix + rowTitle;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await RunAllAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "FullUI: writing playlists failed");
        }
    }

    private async Task RunAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = _config.Current;
        var provider = _sp.GetService<IHomeRowsProvider>();
        if (!cfg.MaterializePlaylists || provider is null)
        {
            progress.Report(100);
            return;
        }

        var excluded = new HashSet<string>(cfg.ExcludedUserIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var users = _users.UserIds.Where(u => !excluded.Contains(u.ToString("N")) && !excluded.Contains(u.ToString())).ToList();
        for (var i = 0; i < users.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await WriteForUserAsync(provider, users[i]).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "FullUI: could not write playlists for a user");
            }

            progress.Report(100.0 * (i + 1) / users.Count);
        }
    }

    private async Task WriteForUserAsync(IHomeRowsProvider provider, Guid userId)
    {
        var rows = provider.GetPlaylistRows(userId)
            .Where(r => r.itemIds.Count > 0 && !string.IsNullOrWhiteSpace(r.title))
            .ToList();
        if (rows.Count == 0)
        {
            return; // never wipe existing playlists because the engine had nothing
        }

        var wanted = rows.Select(r => PlaylistName(r.title)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mine = _playlists.GetPlaylists(userId)
            .Where(p => p.OwnerUserId.Equals(userId) && p.Name.StartsWith(Prefix, StringComparison.Ordinal))
            .ToList();

        foreach (var p in mine)
        {
            // Replace (or drop stale) FullUI playlists; foreign playlists never match the prefix + ownership filter.
            _library.DeleteItem(p, new DeleteOptions { DeleteFileLocation = true });
        }

        foreach (var (title, ids) in rows)
        {
            await _playlists.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = PlaylistName(title),
                ItemIdList = ids.Take(100).ToArray(),
                UserId = userId,
                MediaType = null,
            }).ConfigureAwait(false);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks };
    }
}
