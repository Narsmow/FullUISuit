using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Discovery;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.FullUI.Tasks;

public class SyncRequestsTask : IScheduledTask
{
    private readonly RequestService _requests;
    private readonly ICatalog _catalog;
    private readonly ILogger<SyncRequestsTask> _log;

    public SyncRequestsTask(RequestService requests, ICatalog catalog, ILogger<SyncRequestsTask> log)
    {
        _log = log;
        _requests = requests;
        _catalog = catalog;
    }

    public string Name => "FullUI: Sync requested titles";

    public string Key => "FullUISyncRequests";

    public string Description => "Marks requested titles that are now in the library as Added and notifies voters.";

    public string Category => "FullUI";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            _requests.Sync(_catalog.All);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: syncing requested titles failed");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }
}
