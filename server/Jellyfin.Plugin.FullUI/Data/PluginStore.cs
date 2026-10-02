using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Data;

/// <summary>
/// Thread-safe JSON-file store with debounced atomic saves. Chosen over SQLite to avoid shipping a native
/// dependency inside a plugin load context; fine for household-scale data.
/// <para>
/// Two files: <c>store.json</c> (signals, ratings, votes, ... small) and <c>embeddings.json</c> (AI vectors, can be tens of
/// MB). The embeddings file is loaded lazily on first use and written separately, so day-to-day writes never re-serialize it.
/// </para>
/// <para>
/// Saving never blocks readers for long: the data is copied under the lock and serialized outside it. The save timer is armed
/// once per burst of writes (not re-armed on every write), so continuous writes cannot postpone saving forever. Flush never throws.
/// </para>
/// </summary>
public sealed class PluginStore : IDisposable
{
    /// <summary>Delay between the first unsaved change and the write to disk.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotificationRetention = TimeSpan.FromDays(90);
    private const int MaxNotificationsPerUser = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly object _lock = new();
    private readonly object _embLock = new();
    private readonly object _flushLock = new();
    private readonly string _path;
    private readonly string _embPath;
    private readonly ILogger<PluginStore> _log;
    private readonly Timer _saveTimer;
    private readonly TimeSpan _saveDelay;
    private StoreData _data = new();
    private bool _dirty;
    private bool _timerArmed;
    private bool _disposed;
    private Dictionary<string, EmbeddingEntry>? _emb;
    private bool _embDirty;

    public PluginStore(IApplicationPaths paths, ILogger<PluginStore> log)
        : this(paths, log, SaveDelay)
    {
    }

    internal PluginStore(IApplicationPaths paths, ILogger<PluginStore> log, TimeSpan saveDelay)
    {
        _log = log;
        _saveDelay = saveDelay;
        var dir = Path.Combine(paths.DataPath, "fullui");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "store.json");
        _embPath = Path.Combine(dir, "embeddings.json");
        _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    /// <summary>Run a read-only function under the lock. Do not leak mutable collections out of it.</summary>
    public T Read<T>(Func<StoreData, T> f)
    {
        lock (_lock)
        {
            return f(_data);
        }
    }

    /// <summary>Mutate under the lock; schedules a debounced save.</summary>
    public void Write(Action<StoreData> f)
    {
        lock (_lock)
        {
            f(_data);
            _dirty = true;
        }

        Arm(_saveDelay);
    }

    /// <summary>Read the AI embeddings (loaded from disk on first use). Do not leak the dictionary out of the function.</summary>
    public T ReadEmbeddings<T>(Func<IReadOnlyDictionary<string, EmbeddingEntry>, T> f)
    {
        lock (_embLock)
        {
            return f(EnsureEmbeddings());
        }
    }

    /// <summary>Mutate the AI embeddings; schedules a debounced save of the embeddings file only.</summary>
    public void WriteEmbeddings(Action<Dictionary<string, EmbeddingEntry>> f)
    {
        lock (_embLock)
        {
            f(EnsureEmbeddings());
            _embDirty = true;
        }

        Arm(_saveDelay);
    }

    /// <summary>Writes pending changes now. Never throws; a failed write is retried later.</summary>
    public void Flush()
    {
        lock (_flushLock)
        {
            try
            {
                FlushCore();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FullUI: unexpected error while saving; will retry");
                Arm(RetryDelay);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _saveTimer.Dispose();
        Flush();
    }

    private void Arm(TimeSpan delay)
    {
        lock (_lock)
        {
            if (_disposed || _timerArmed)
            {
                return;
            }

            _timerArmed = true;
        }

        try
        {
            _saveTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the call: Dispose already flushed.
        }
    }

    private void FlushCore()
    {
        StoreData? snapshot = null;
        lock (_lock)
        {
            _timerArmed = false;
            if (_dirty)
            {
                var now = DateTime.UtcNow;
                var cutoff = now.AddDays(-400);
                _data.Signals.RemoveAll(s => s.At < cutoff);
                PruneNotifications(_data, now);
                snapshot = _data.Snapshot();
                _dirty = false;
            }
        }

        if (snapshot is not null && !TryWrite(_path, snapshot))
        {
            lock (_lock)
            {
                _dirty = true;
            }

            Arm(RetryDelay);
        }

        Dictionary<string, EmbeddingEntry>? emb = null;
        lock (_embLock)
        {
            if (_embDirty && _emb is not null)
            {
                emb = new Dictionary<string, EmbeddingEntry>(_emb);
                _embDirty = false;
            }
        }

        if (emb is not null && !TryWrite(_embPath, emb))
        {
            lock (_embLock)
            {
                _embDirty = true;
            }

            Arm(RetryDelay);
        }
    }

    private static void PruneNotifications(StoreData d, DateTime now)
    {
        var cutoff = now - NotificationRetention;
        d.Notifications.RemoveAll(n => n.At < cutoff);
        foreach (var userId in d.Notifications.GroupBy(n => n.UserId).Where(g => g.Count() > MaxNotificationsPerUser).Select(g => g.Key).ToList())
        {
            var drop = d.Notifications.Where(n => n.UserId == userId).OrderByDescending(n => n.At).Skip(MaxNotificationsPerUser).ToHashSet();
            d.Notifications.RemoveAll(drop.Contains);
        }
    }

    private bool TryWrite<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: failed to save {File}", Path.GetFileName(path));
            return false;
        }
    }

    private Dictionary<string, EmbeddingEntry> EnsureEmbeddings()
    {
        // Caller holds _embLock.
        if (_emb is not null)
        {
            return _emb;
        }

        _emb = new Dictionary<string, EmbeddingEntry>();
        try
        {
            if (File.Exists(_embPath))
            {
                _emb = JsonSerializer.Deserialize<Dictionary<string, EmbeddingEntry>>(File.ReadAllText(_embPath), Json) ?? new Dictionary<string, EmbeddingEntry>();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: AI index unreadable, it will be rebuilt (old file kept as .bad)");
            MoveAside(_embPath);
            _emb = new Dictionary<string, EmbeddingEntry>();
        }

        return _emb;
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _data = JsonSerializer.Deserialize<StoreData>(File.ReadAllText(_path), Json) ?? new StoreData();
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FullUI: store unreadable, starting empty (old file kept as .bad)");
            MoveAside(_path);
            _data = new StoreData();
        }

        // Older versions kept embeddings inside store.json. Move them to their own file once.
        if (_data.LegacyEmbeddings is { Count: > 0 } legacy)
        {
            lock (_embLock)
            {
                var emb = EnsureEmbeddings();
                foreach (var (k, v) in legacy)
                {
                    emb.TryAdd(k, new EmbeddingEntry { Vector = v });
                }

                _embDirty = true;
            }

            _data.LegacyEmbeddings = null;
            _dirty = true;
            Arm(_saveDelay);
        }
        else
        {
            _data.LegacyEmbeddings = null;
        }
    }

    private static void MoveAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
