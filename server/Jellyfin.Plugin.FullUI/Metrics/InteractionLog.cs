using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.Ops;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Metrics;

public static class EventTypes
{
    public static readonly string[] All = { "rowShown", "cardExpanded", "cardClicked", "playStarted", "trailerViewed", "searchIssued", "searchClicked" };

    /// <summary>
    /// Written by the server itself (never accepted from a client) whenever Jellyfin reports that playback started, whatever
    /// started it: the denominator for "how many plays did FullUI's rows cause".
    /// </summary>
    public const string ServerPlay = "serverPlayStarted";

    /// <summary>A type a client may send.</summary>
    public static string? Canonical(string? type)
        => All.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase));

    /// <summary>A type that may be stored in the log: the client types plus the server's own.</summary>
    public static string? CanonicalStored(string? type)
        => Canonical(type) ?? (string.Equals(type, ServerPlay, StringComparison.OrdinalIgnoreCase) ? ServerPlay : null);
}

/// <summary>One validated interaction, as kept on disk. <c>UserId</c> is only ever used to compute that user's own engagement and distinct-user counts.</summary>
public sealed record StoredEvent(Guid UserId, string Type, string? RowType, string? ItemId, string? Query, DateTime At);

/// <summary>
/// Append-only, bounded log of impressions and interactions in <c>fullui/events.jsonl</c> (NOT in store.json).
/// One JSON object per line. The file rotates at 10 MB (one previous file is kept, so the total stays near 20 MB) and lines
/// older than 90 days are dropped by <see cref="Prune"/>. Nothing here ever throws into the caller.
/// </summary>
public sealed class InteractionLog
{
    public const long RotateBytes = 10L * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _lock = new();
    private readonly string _path;
    private readonly string _oldPath;
    private readonly ILogger<InteractionLog> _log;
    private long _length = -1;
    private long _version;

    public InteractionLog(PluginStore store, ILogger<InteractionLog> log)
        : this(store.DirectoryPath, log)
    {
    }

    internal InteractionLog(string directory, ILogger<InteractionLog> log)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "events.jsonl");
        _oldPath = Path.Combine(directory, "events.1.jsonl");
        _log = log;
    }

    /// <summary>Changes whenever lines are added or removed (readers use it to know when a cached aggregate is stale).</summary>
    public long Version => System.Threading.Interlocked.Read(ref _version);

    public string FilePath => _path;

    /// <summary>Total bytes on disk (current + rotated file).</summary>
    public long SizeBytes
    {
        get
        {
            lock (_lock)
            {
                return Len(_path) + Len(_oldPath);
            }
        }
    }

    public void Append(IReadOnlyCollection<StoredEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var e in events)
            {
                sb.Append(JsonSerializer.Serialize(new Line(e.UserId.ToString("N"), e.Type, e.RowType, e.ItemId, e.Query, e.At.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture)), Json));
                sb.Append('\n');
            }

            lock (_lock)
            {
                if (_length < 0)
                {
                    _length = Len(_path);
                }

                if (_length >= RotateBytes)
                {
                    File.Move(_path, _oldPath, overwrite: true);
                    _length = 0;
                }

                var text = sb.ToString();
                File.AppendAllText(_path, text);
                _length += System.Text.Encoding.UTF8.GetByteCount(text);
                _version++;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("FullUI: could not record interaction events ({Type})", ex.GetType().Name);
            ErrorLog.Add("Usage statistics could not be saved (the disk may be full or read-only).");
        }
    }

    /// <summary>Every event still on disk, oldest file first. Unreadable lines are skipped.</summary>
    public IReadOnlyList<StoredEvent> ReadAll()
    {
        var all = new List<StoredEvent>();
        lock (_lock)
        {
            foreach (var file in new[] { _oldPath, _path })
            {
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    foreach (var line in File.ReadLines(file))
                    {
                        var e = Parse(line);
                        if (e is not null)
                        {
                            all.Add(e);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning("FullUI: usage statistics file could not be read ({Type})", ex.GetType().Name);
                }
            }
        }

        return all;
    }

    /// <summary>Rewrites the files without lines older than the retention period. Returns how many lines were dropped.</summary>
    public int Prune(DateTime now)
    {
        var dropped = 0;
        lock (_lock)
        {
            foreach (var file in new[] { _oldPath, _path })
            {
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var cutoff = now - Retention;
                    var keep = new List<string>();
                    var any = false;
                    foreach (var line in File.ReadLines(file))
                    {
                        var e = Parse(line);
                        if (e is null || e.At < cutoff)
                        {
                            dropped++;
                            any = true;
                            continue;
                        }

                        keep.Add(line);
                    }

                    if (!any)
                    {
                        continue;
                    }

                    var tmp = file + ".tmp";
                    File.WriteAllLines(tmp, keep);
                    File.Move(tmp, file, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("FullUI: old usage statistics could not be tidied ({Type})", ex.GetType().Name);
                }
            }

            _length = -1;
            if (dropped > 0)
            {
                _version++;
            }
        }

        return dropped;
    }

    /// <summary>Deletes every event of one user (or of everyone when <paramref name="userId"/> is null). Returns the number of lines removed.</summary>
    public int Delete(Guid? userId)
    {
        var removed = 0;
        lock (_lock)
        {
            foreach (var file in new[] { _oldPath, _path })
            {
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    if (userId is null)
                    {
                        removed += File.ReadLines(file).Count();
                        File.Delete(file);
                        continue;
                    }

                    var n = userId.Value.ToString("N");
                    var keep = new List<string>();
                    foreach (var line in File.ReadLines(file))
                    {
                        var e = Parse(line);
                        if (e is not null && e.UserId.ToString("N") == n)
                        {
                            removed++;
                        }
                        else
                        {
                            keep.Add(line);
                        }
                    }

                    var tmp = file + ".tmp";
                    File.WriteAllLines(tmp, keep);
                    File.Move(tmp, file, overwrite: true);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("FullUI: usage statistics could not be deleted ({Type})", ex.GetType().Name);
                }
            }

            _length = -1;
            _version++;
        }

        return removed;
    }

    private static long Len(string p)
    {
        try
        {
            return File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    internal static StoredEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var l = JsonSerializer.Deserialize<Line>(line, Json);
            if (l is null || !Guid.TryParse(l.U, out var u) || EventTypes.CanonicalStored(l.T) is not { } type
                || !DateTime.TryParse(l.A, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            {
                return null;
            }

            return new StoredEvent(u, type, l.R, l.I, l.Q, at);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Line(string? U, string? T, string? R, string? I, string? Q, string? A);
}
