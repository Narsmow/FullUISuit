using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.FullUI.Data;
using Jellyfin.Plugin.FullUI.DiscoveryTests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

/// <summary>
/// Stand-in for any Jellyfin interface: every member returns a default (null / 0 / completed task), except where a handler
/// is registered by member name. Lets the real services be constructed and exercised without a running Jellyfin.
/// </summary>
public class Stub : DispatchProxy
{
    public Dictionary<string, Func<object?[]?, object?>> Handlers { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        if (Handlers.TryGetValue(targetMethod.Name, out var h))
        {
            return h(args);
        }

        var rt = targetMethod.ReturnType;
        if (rt == typeof(void))
        {
            return null;
        }

        if (rt == typeof(string))
        {
            return string.Empty;
        }

        if (rt == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = rt.GetGenericArguments()[0];
            var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }

        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }

    public static T Make<T>(params (string Member, Func<object?[]?, object?> Handler)[] handlers)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        foreach (var (member, handler) in handlers)
        {
            ((Stub)(object)proxy).Handlers[member] = handler;
        }

        return proxy;
    }
}

public static class Http
{
    public static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Gives a controller a signed-in user with the Jellyfin claim FullUI reads.</summary>
    public static T As<T>(this T controller, Guid? user = null)
        where T : ControllerBase
    {
        var ctx = new DefaultHttpContext();
        if (user != Guid.Empty)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", (user ?? User).ToString("N")) }, "test"));
        }

        controller.ControllerContext = new ControllerContext(new ActionContext(ctx, new RouteData(), new ControllerActionDescriptor()));
        return controller;
    }

    /// <summary>
    /// Executes a result exactly like ASP.NET would, with MVC configured the way Jellyfin configures it (PascalCase formatter),
    /// and returns the response body.
    /// </summary>
    public static async Task<(int Status, string Body, string? ContentType)> Render(IActionResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null);
        await using var sp = services.BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = sp };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteResultAsync(new ActionContext(ctx, new RouteData(), new ActionDescriptor()));
        ctx.Response.Body.Position = 0;
        var body = await new StreamReader(ctx.Response.Body, Encoding.UTF8).ReadToEndAsync();
        return (ctx.Response.StatusCode, body, ctx.Response.ContentType);
    }

    /// <summary>All property names anywhere in the JSON.</summary>
    public static List<string> PropertyNames(string json)
    {
        var names = new List<string>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        names.Add(p.Name);
                        Walk(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var i in e.EnumerateArray())
                    {
                        Walk(i);
                    }

                    break;
            }
        }

        using var doc = JsonDocument.Parse(json);
        Walk(doc.RootElement);
        return names;
    }

    public static void AssertCamelCase(string json)
    {
        var names = PropertyNames(json);
        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.True(char.IsLower(n[0]), $"property '{n}' is not camelCase in: {json}"));
    }
}

/// <summary>Temp dir + store for tests that also need to look at the files on disk.</summary>
public sealed class DiskStore : IDisposable
{
    public DiskStore()
    {
        Dir = Path.Combine(Path.GetTempPath(), "fullui-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
    }

    public string Dir { get; }

    public string StoreFile => Path.Combine(Dir, "fullui", "store.json");

    public string EmbeddingsFile => Path.Combine(Dir, "fullui", "embeddings.json");

    public PluginStore Open(TimeSpan? saveDelay = null)
    {
        var paths = Stub.Make<MediaBrowser.Common.Configuration.IApplicationPaths>(("get_DataPath", _ => Dir));
        return saveDelay is { } d
            ? new PluginStore(paths, NullLogger<PluginStore>.Instance, d)
            : new PluginStore(paths, NullLogger<PluginStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}
