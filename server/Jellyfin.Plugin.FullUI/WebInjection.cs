using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.FullUI;

/// <summary>What happened the last time we tried to hook into the Jellyfin web page. Shown on the settings page.</summary>
public sealed class InjectionStatus
{
    private volatile bool _registered;
    private int _attempts;
    private volatile string _message = "FullUI has not tried to hook into the Jellyfin web page yet. This happens a few seconds after Jellyfin starts.";

    public bool Registered => _registered;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Plain-English explanation for a non-technical admin.</summary>
    public string Message => _message;

    internal void Record(bool registered, string message)
    {
        Interlocked.Increment(ref _attempts);
        _message = message;
        _registered = registered;
    }

    public object Describe() => new { registered = Registered, attempts = Attempts, message = Message };
}

public enum InjectionOutcome
{
    /// <summary>Hooked in (or already hooked in).</summary>
    Registered,

    /// <summary>The File Transformation plugin is not installed, or has not finished starting. Worth retrying.</summary>
    NotReady,

    /// <summary>File Transformation is there but its interface is not what we expect. Retrying will not help.</summary>
    Incompatible,

    /// <summary>It threw while registering. May be temporary.</summary>
    Failed,
}

public readonly record struct InjectionResult(InjectionOutcome Outcome, string Message, string? Technical = null);

/// <summary>
/// Registers an index.html transformation with the File Transformation plugin (by reflection,
/// so there is no hard dependency). If that plugin is missing, the reskin simply is not injected.
/// Registration is attempted from <see cref="WebInjectionHostedService"/> after Jellyfin has started, never from
/// service registration (File Transformation is not constructed yet at that point).
/// </summary>
public static class WebInjection
{
    public const string PluginId = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57";

    /// <summary>
    /// Relative to the page (<c>{base}/web/index.html</c>), so it works unchanged behind a reverse proxy or a Jellyfin
    /// "Base URL" such as <c>/jellyfin</c>: <c>../FullUI/...</c> resolves to <c>{base}/FullUI/...</c>.
    /// </summary>
    public const string Tag =
        "<link rel=\"stylesheet\" href=\"../FullUI/web/fullui.css\"><script defer src=\"../FullUI/web/fullui.js\"></script>";

    private const string Marker = "/FullUI/web/fullui.js";

    public static InjectionStatus Status { get; } = new();

    public static InjectionResult TryRegister()
    {
        var result = TryRegisterCore();
        Status.Record(result.Outcome == InjectionOutcome.Registered, result.Message);
        return result;
    }

    private static InjectionResult TryRegisterCore()
    {
        try
        {
            var asm = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.GetName().Name?.EndsWith(".FileTransformation", StringComparison.Ordinal) == true
                    || a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
            if (asm is null)
            {
                return new InjectionResult(
                    InjectionOutcome.NotReady,
                    "The FullUI screen is not active: the \"File Transformation\" plugin is not installed or has not started. Install it from the Jellyfin plugin catalog, then restart Jellyfin.");
            }

            var iface = asm.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = iface?.GetMethod("RegisterTransformation");
            if (iface is null || register is null)
            {
                return new InjectionResult(
                    InjectionOutcome.Incompatible,
                    "The FullUI screen is not active: the installed \"File Transformation\" plugin is a version FullUI does not recognise. Update both plugins and restart Jellyfin.");
            }

            var parameter = register.GetParameters().FirstOrDefault();
            if (parameter is null)
            {
                return new InjectionResult(
                    InjectionOutcome.Incompatible,
                    "The FullUI screen is not active: the installed \"File Transformation\" plugin is a version FullUI does not recognise. Update both plugins and restart Jellyfin.");
            }

            object? target = null;
            if (!register.IsStatic)
            {
                target = iface.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (target is null)
                {
                    return new InjectionResult(InjectionOutcome.NotReady, "The FullUI screen will turn on once the \"File Transformation\" plugin has finished starting.");
                }
            }

            register.Invoke(target, new[] { BuildPayload(parameter.ParameterType) });
            return new InjectionResult(InjectionOutcome.Registered, "The FullUI screen is hooked into the Jellyfin web page. Hard-refresh your browser (Ctrl+F5) to see it.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is NullReferenceException or InvalidOperationException)
        {
            // File Transformation sets its static Instance in its constructor; before that, registering throws.
            return new InjectionResult(
                InjectionOutcome.NotReady,
                "The FullUI screen will turn on once the \"File Transformation\" plugin has finished starting.",
                ex.InnerException?.GetType().Name);
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
            return new InjectionResult(
                InjectionOutcome.Failed,
                "The FullUI screen is not active: hooking into the Jellyfin web page failed. Restart Jellyfin; if it keeps happening, check the Jellyfin log for lines starting with \"FullUI\".",
                inner.GetType().Name + ": " + inner.Message);
        }
    }

    /// <summary>
    /// Builds the argument in whatever type File Transformation declares (a Newtonsoft JObject from ITS copy of the library,
    /// a string, or a DTO), so we never depend on type identity across plugin load contexts.
    /// </summary>
    internal static object BuildPayload(Type parameterType)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = PluginId,
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(WebInjection).Assembly.FullName ?? string.Empty,
            ["callbackClass"] = typeof(WebInjection).FullName ?? string.Empty,
            ["callbackMethod"] = nameof(Transform),
        });
        if (parameterType == typeof(string) || parameterType == typeof(object))
        {
            return json;
        }

        var parse = parameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (parse is not null)
        {
            return parse.Invoke(null, new object[] { json })!;
        }

        return JsonSerializer.Deserialize(json, parameterType, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    // File Transformation passes {"contents": "<file text>"} and expects the new contents back.
    public static string Transform(PatchRequestPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        if (html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        return html.Contains("</body>", StringComparison.OrdinalIgnoreCase)
            ? html.Replace("</body>", Tag + "</body>", StringComparison.OrdinalIgnoreCase)
            : html + Tag;
    }

    public class PatchRequestPayload
    {
        // File Transformation fills this from {"contents": "..."}; the attribute covers System.Text.Json, Newtonsoft matches by name ignoring case.
        [System.Text.Json.Serialization.JsonPropertyName("contents")]
        public string? Contents { get; set; }
    }
}
