using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FullUI.Discovery;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FullUI.Ai;

public interface IOllamaClient
{
    /// <summary>True when Ollama is enabled in settings (does not imply it is reachable).</summary>
    bool Enabled { get; }

    /// <summary>Name of the embedding model currently configured (stored with each vector).</summary>
    string EmbedModel => string.Empty;

    /// <summary>Embeddings for each text, or null on any failure.</summary>
    Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);

    /// <summary>
    /// Embedding for one interactive search query: short timeout, and after a failure it answers null immediately for a
    /// minute so a stopped Ollama does not make every search wait.
    /// </summary>
    Task<IReadOnlyList<float[]>?> EmbedQueryAsync(string text, CancellationToken ct) => EmbedAsync(new[] { text }, ct);

    /// <summary>One chat completion (non-streaming), or null on any failure.</summary>
    Task<string?> ChatAsync(string system, string user, CancellationToken ct);

    Task<(bool ok, string message)> TestAsync(string? url, string? embedModel, string? chatModel, CancellationToken ct);
}

/// <summary>Minimal Ollama client with short timeouts. Failures are swallowed (null result); callers fall back.</summary>
public sealed class OllamaClient : IOllamaClient
{
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ChatTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BackOff = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly IConfigSource _config;
    private readonly ILogger<OllamaClient> _log;
    private long _embedBlockedUntil;
    private long _chatBlockedUntil;

    public OllamaClient(IHttpClientFactory http, IConfigSource config, ILogger<OllamaClient> log)
    {
        _http = http;
        _config = config;
        _log = log;
    }

    public string EmbedModel => _config.Current.OllamaEmbedModel ?? string.Empty;

    /// <summary>True while a recent failure has the circuit open (calls that honour the breaker return null at once).</summary>
    public bool CircuitOpen => DateTime.UtcNow.Ticks < Interlocked.Read(ref _embedBlockedUntil);

    private bool ChatCircuitOpen => DateTime.UtcNow.Ticks < Interlocked.Read(ref _chatBlockedUntil);

    public bool Enabled => _config.Current.OllamaEnabled && BaseUri(_config.Current.OllamaUrl) is not null;

    public static Uri? BaseUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u))
        {
            return null;
        }

        return u.Scheme is "http" or "https" ? u : null;
    }

    public async Task<IReadOnlyList<float[]>?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (!Enabled || texts.Count == 0)
        {
            return null;
        }

        return await EmbedCore(cfg.OllamaUrl, cfg.OllamaEmbedModel, texts, EmbedTimeout, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<float[]>?> EmbedQueryAsync(string text, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (!Enabled || CircuitOpen)
        {
            return null;
        }

        var res = await EmbedCore(cfg.OllamaUrl, cfg.OllamaEmbedModel, new[] { text }, QueryTimeout, ct).ConfigureAwait(false);
        Record(ref _embedBlockedUntil, res is not null);
        return res;
    }

    private static void Record(ref long blockedUntil, bool success)
    {
        if (success)
        {
            Interlocked.Exchange(ref blockedUntil, 0);
        }
        else
        {
            Interlocked.Exchange(ref blockedUntil, (DateTime.UtcNow + BackOff).Ticks);
        }
    }

    public async Task<string?> ChatAsync(string system, string user, CancellationToken ct)
    {
        var cfg = _config.Current;
        if (!Enabled || ChatCircuitOpen)
        {
            return null;
        }

        var res = await ChatCore(cfg.OllamaUrl, cfg.OllamaChatModel, system, user, ChatTimeout, ct).ConfigureAwait(false);
        Record(ref _chatBlockedUntil, res is not null);
        return res;
    }

    public async Task<(bool ok, string message)> TestAsync(string? url, string? embedModel, string? chatModel, CancellationToken ct)
    {
        var cfg = _config.Current;
        var u = string.IsNullOrWhiteSpace(url) ? cfg.OllamaUrl : url;
        var em = string.IsNullOrWhiteSpace(embedModel) ? cfg.OllamaEmbedModel : embedModel;
        var cm = string.IsNullOrWhiteSpace(chatModel) ? cfg.OllamaChatModel : chatModel;
        if (BaseUri(u) is null)
        {
            return (false, "Ollama URL must be an http(s) address.");
        }

        var emb = await EmbedCore(u, em, new[] { "hello" }, TestTimeout, ct).ConfigureAwait(false);
        if (emb is null || emb.Count == 0)
        {
            return (false, $"Could not get an embedding from Ollama with model '{em}'. Is it running and is the model pulled?");
        }

        var chat = await ChatCore(u, cm, "Reply with one word.", "Say ok.", TestTimeout, ct).ConfigureAwait(false);
        return chat is null
            ? (false, $"Embeddings work ({emb[0].Length} dims) but chat model '{cm}' did not answer. Row titles will be skipped.")
            : (true, $"Connected. Embeddings: {emb[0].Length} dims; chat model replied.");
    }

    private async Task<IReadOnlyList<float[]>?> EmbedCore(string baseUrl, string model, IReadOnlyList<string> texts, TimeSpan timeout, CancellationToken ct)
    {
        var baseUri = BaseUri(baseUrl);
        if (baseUri is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = _http.CreateClient("FullUI");
            var body = JsonSerializer.Serialize(new { model, input = texts });
            using var resp = await client.PostAsync(new Uri(baseUri, "api/embed"), new StringContent(body, Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("embeddings", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var res = arr.EnumerateArray().Select(a => a.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
                    if (res.Count == texts.Count && res.All(r => r.Length > 0))
                    {
                        return res;
                    }
                }
            }

            // Older servers: one prompt per call.
            var list = new List<float[]>();
            foreach (var t in texts)
            {
                var b2 = JsonSerializer.Serialize(new { model, prompt = t });
                using var r2 = await client.PostAsync(new Uri(baseUri, "api/embeddings"), new StringContent(b2, Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
                if (!r2.IsSuccessStatusCode)
                {
                    return null;
                }

                using var d2 = JsonDocument.Parse(await r2.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
                if (!d2.RootElement.TryGetProperty("embedding", out var e) || e.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                if (v.Length == 0)
                {
                    return null;
                }

                list.Add(v);
            }

            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug("FullUI: Ollama embed failed ({Type})", ex.GetType().Name);
            return null;
        }
    }

    private async Task<string?> ChatCore(string baseUrl, string model, string system, string user, TimeSpan timeout, CancellationToken ct)
    {
        var baseUri = BaseUri(baseUrl);
        if (baseUri is null)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var client = _http.CreateClient("FullUI");
            var body = JsonSerializer.Serialize(new
            {
                model,
                stream = false,
                // Low temperature and a fixed seed: the same question gets (nearly) the same answer, so row titles do not wander.
                options = new { temperature = 0.2, seed = 42, num_predict = 40 },
                messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            });
            using var resp = await client.PostAsync(new Uri(baseUri, "api/chat"), new StringContent(body, Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                var s = c.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogDebug("FullUI: Ollama chat failed ({Type})", ex.GetType().Name);
            return null;
        }
    }
}
