using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Ai;

/// <summary>Nightly: embeddings for new library items and fresh LLM row titles. No-op when Ollama is disabled.</summary>
public class BuildEmbeddingsTask : IScheduledTask
{
    private readonly EmbeddingIndexer _indexer;
    private readonly RowTitleGenerator _titles;
    private readonly ILogger<BuildEmbeddingsTask> _log;
    private readonly Ops.ITaskRunLog? _runs;

    public BuildEmbeddingsTask(EmbeddingIndexer indexer, RowTitleGenerator titles, ILogger<BuildEmbeddingsTask> log, Ops.ITaskRunLog? runs = null)
    {
        _runs = runs;
        _log = log;
        _indexer = indexer;
        _titles = titles;
    }

    public string Name => "FullUI: Build AI index";

    public string Key => "FullUIBuildEmbeddings";

    public string Description => "Computes search embeddings and row titles with the local Ollama server (optional).";

    public string Category => "FullUI";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await _indexer.RunAsync(new Progress<double>(p => progress.Report(p * 0.9)), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "FullUI: building search embeddings failed; keyword search keeps working");
            _runs?.Note(Key, "The AI index could not be built (is Ollama running?). Keyword search keeps working.", problem: true);
        }

        try
        {
            await _titles.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "FullUI: generating row titles failed; default titles are used");
            _runs?.Note(Key, "Custom row titles could not be written; the standard titles are used.", problem: true);
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks };
    }
}
