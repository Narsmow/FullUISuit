using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Data;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Ops;

public static class TaskOutcome
{
    public const string Success = "Success";
    public const string Problem = "Problem";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Remembers when each FullUI task or background service last ran and how it went (for the health page).
/// Anything in the plugin - including the recommendation engine's own tasks and services - can call <see cref="Record"/>.
/// </summary>
public interface ITaskRunLog
{
    /// <summary>Stores one run. Never throws. <paramref name="message"/> must be short, plain English and free of secrets.</summary>
    void Record(string key, string name, DateTime startUtc, DateTime endUtc, string outcome, string message);

    /// <summary>
    /// A task can leave a note while it runs ("3 of 12 users could not be updated"); the recorder attaches it to the run that
    /// Jellyfin reports when the task ends. <paramref name="problem"/> turns an otherwise "Completed" run into a "Problem".
    /// </summary>
    void Note(string key, string message, bool problem = false);

    /// <summary>The most recent run of every key, newest first.</summary>
    IReadOnlyList<TaskRunRecord> Latest();

    /// <summary>Takes (and clears) the pending note for a task key.</summary>
    (string Message, bool Problem)? TakeNote(string key);
}

public sealed class TaskRunLog : ITaskRunLog
{
    public const int KeepPerKey = 20;

    private readonly PluginStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Message, bool Problem)> _notes = new(StringComparer.Ordinal);

    public TaskRunLog(PluginStore store)
    {
        _store = store;
    }

    public void Record(string key, string name, DateTime startUtc, DateTime endUtc, string outcome, string message)
    {
        try
        {
            var msg = ErrorLog.Scrub(message);
            _store.Write(d =>
            {
                d.TaskRuns.Add(new TaskRunRecord { Key = key, Name = name, StartUtc = startUtc, EndUtc = endUtc, Outcome = outcome, Message = msg });
                var mine = d.TaskRuns.Where(r => r.Key == key).OrderByDescending(r => r.StartUtc).Skip(KeepPerKey).ToHashSet();
                if (mine.Count > 0)
                {
                    d.TaskRuns.RemoveAll(mine.Contains);
                }
            });
            if (outcome is TaskOutcome.Failed or TaskOutcome.Problem)
            {
                ErrorLog.Add($"{name}: {msg}", endUtc);
            }
        }
        catch (Exception)
        {
            // Bookkeeping must never be the thing that breaks a task.
        }
    }

    public void Note(string key, string message, bool problem = false)
    {
        lock (_lock)
        {
            _notes[key] = (ErrorLog.Scrub(message), problem);
        }
    }

    public (string Message, bool Problem)? TakeNote(string key)
    {
        lock (_lock)
        {
            if (_notes.Remove(key, out var n))
            {
                return n;
            }

            return null;
        }
    }

    public IReadOnlyList<TaskRunRecord> Latest()
        => _store.Read(d => d.TaskRuns.GroupBy(r => r.Key).Select(g => g.OrderByDescending(r => r.StartUtc).First())
            .OrderByDescending(r => r.StartUtc)
            .Select(r => new TaskRunRecord { Key = r.Key, Name = r.Name, StartUtc = r.StartUtc, EndUtc = r.EndUtc, Outcome = r.Outcome, Message = r.Message })
            .ToList());
}

/// <summary>
/// Listens to Jellyfin's task manager and records every FullUI task that finishes (including ones this plugin does not own,
/// such as the recommendation rebuild). Does nothing if the task manager cannot be reached.
/// </summary>
public sealed class TaskRunRecorderService : IHostedService
{
    private readonly ITaskManager _tasks;
    private readonly ITaskRunLog _runs;
    private readonly ILogger<TaskRunRecorderService> _log;
    private bool _subscribed;

    public TaskRunRecorderService(ITaskManager tasks, ITaskRunLog runs, ILogger<TaskRunRecorderService> log)
    {
        _tasks = tasks;
        _runs = runs;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _tasks.TaskCompleted += OnCompleted;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("FullUI: task history is unavailable ({Type})", ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscribed)
        {
            try
            {
                _tasks.TaskCompleted -= OnCompleted;
            }
            catch (Exception)
            {
                // shutting down
            }
        }

        return Task.CompletedTask;
    }

    internal void OnCompleted(object? sender, TaskCompletionEventArgs e)
    {
        try
        {
            var r = e.Result;
            if (r is null || r.Key is null || !r.Key.StartsWith("FullUI", StringComparison.Ordinal))
            {
                return;
            }

            var note = _runs.TakeNote(r.Key);
            var (outcome, message) = r.Status switch
            {
                TaskCompletionStatus.Completed => note is { Problem: true } ? (TaskOutcome.Problem, note.Value.Message) : (TaskOutcome.Success, note?.Message ?? "Finished normally."),
                TaskCompletionStatus.Cancelled => (TaskOutcome.Cancelled, "It was stopped before it finished."),
                _ => (TaskOutcome.Failed, note?.Message ?? "It did not finish. The server log has the details."),
            };
            _runs.Record(r.Key, r.Name ?? r.Key, r.StartTimeUtc, r.EndTimeUtc, outcome, message);
        }
        catch (Exception ex)
        {
            _log.LogDebug("FullUI: could not record a task run ({Type})", ex.GetType().Name);
        }
    }
}
