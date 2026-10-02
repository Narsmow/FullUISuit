using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>Runs the request sync shortly after the library changes (debounced). Does nothing if no ICatalog exists.</summary>
public sealed class RequestSyncHostedService : IHostedService, IDisposable
{
    internal const string RunKey = "FullUILibraryWatcher";
    internal const string RunName = "Watch for new library titles";

    private readonly IServiceProvider _sp;
    private readonly ILogger<RequestSyncHostedService> _log;
    private readonly Timer _timer;
    private ICatalog? _catalog;

    public RequestSyncHostedService(IServiceProvider sp, ILogger<RequestSyncHostedService> log)
    {
        _sp = sp;
        _log = log;
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _catalog = _sp.GetService<ICatalog>();
            if (_catalog is not null)
            {
                _catalog.Changed += OnChanged;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: request sync could not start; the scheduled task still runs");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not null)
        {
            _catalog.Changed -= OnChanged;
        }

        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void OnChanged(object? sender, EventArgs e) => _timer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);

    private void Run()
    {
        var start = DateTime.UtcNow;
        var runs = _sp.GetService<Ops.ITaskRunLog>();
        try
        {
            var catalog = _catalog;
            var svc = _sp.GetService<RequestService>();
            if (catalog is null || svc is null)
            {
                return;
            }

            var n = svc.Sync(catalog.All);
            if (n > 0)
            {
                _log.LogInformation("FullUI: {Count} requested title(s) are now available", n);
            }

            // A reminded title that just arrived in the library fires its "now on <server>" notification.
            var sent = _sp.GetService<ReminderService>()?.Process(DateTime.UtcNow) ?? 0;
            if (sent > 0)
            {
                _log.LogInformation("FullUI: {Count} reminder(s) sent", sent);
            }

            runs?.Record(RunKey, RunName, start, DateTime.UtcNow, Ops.TaskOutcome.Success, $"{n} request(s) arrived, {sent} reminder(s) sent.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "FullUI: request sync failed");
            runs?.Record(RunKey, RunName, start, DateTime.UtcNow, Ops.TaskOutcome.Failed, "Checking for newly added titles failed. It will try again after the next library change.");
        }
    }
}
