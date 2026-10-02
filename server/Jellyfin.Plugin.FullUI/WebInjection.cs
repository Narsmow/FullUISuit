using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.FullUI;

/// <summary>
/// Registers an index.html transformation with the File Transformation plugin (by reflection,
/// so there is no hard dependency). If that plugin is missing, the reskin simply is not injected.
/// </summary>
public static class WebInjection
{
    private const string Tag =
        "<link rel=\"stylesheet\" href=\"/FullUI/web/fullui.css\"><script defer src=\"/FullUI/web/fullui.js\"></script>";

    public static bool TryRegister()
    {
        try
        {
            var asm = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) == true);
            var iface = asm?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var register = iface?.GetMethod("RegisterTransformation");
            if (register is null)
            {
                return false;
            }

            var payload = new JObject
            {
                ["id"] = "7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57",
                ["fileNamePattern"] = "index.html",
                ["callbackAssembly"] = typeof(WebInjection).Assembly.FullName,
                ["callbackClass"] = typeof(WebInjection).FullName,
                ["callbackMethod"] = nameof(Transform),
            };
            register.Invoke(null, new object?[] { payload });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // File Transformation passes {"contents": "<file text>"} and expects the new contents back.
    public static string Transform(PatchRequestPayload payload)
    {
        var html = payload.Contents ?? string.Empty;
        return html.Contains("/FullUI/web/fullui.js", StringComparison.Ordinal)
            ? html
            : html.Replace("</body>", Tag + "</body>", StringComparison.OrdinalIgnoreCase);
    }

    public class PatchRequestPayload
    {
        [Newtonsoft.Json.JsonProperty("contents")]
        public string? Contents { get; set; }
    }
}
