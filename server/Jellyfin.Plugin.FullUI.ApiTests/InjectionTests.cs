using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.FullUI.ApiTests;

public class WebInjectionTests
{
    private static string Transform(string? html) => WebInjection.Transform(new WebInjection.PatchRequestPayload { Contents = html });

    [Fact]
    public void Transform_InsertsTheBundle_JustBeforeBodyEnd()
    {
        var result = Transform("<html><head></head><body><div id=app></div></body></html>");

        Assert.Equal("<html><head></head><body><div id=app></div>" + WebInjection.Tag + "</body></html>", result);
    }

    [Fact]
    public void Transform_IsIdempotent()
    {
        var once = Transform("<body></body>");
        Assert.Equal(once, Transform(once));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Transform(once), "fullui.js"));
    }

    [Fact]
    public void Transform_IsCaseInsensitive_AndHandlesMissingBodyOrEmptyInput()
    {
        Assert.Equal("<BODY>x" + WebInjection.Tag + "</body>", Transform("<BODY>x</BODY>"));
        Assert.EndsWith(WebInjection.Tag, Transform("<div>no body tag</div>"));
        Assert.Equal(WebInjection.Tag, Transform(null));
        Assert.Equal(WebInjection.Tag, Transform(string.Empty));
    }

    [Fact]
    public void Tag_UsesPageRelativeUrls_SoABaseUrlOrReverseProxySubpathWorks()
    {
        // B-06: "/FullUI/..." is root-absolute and 404s under https://host/jellyfin/. "../FullUI/..." resolves against /jellyfin/web/index.html.
        Assert.DoesNotContain("href=\"/", WebInjection.Tag);
        Assert.DoesNotContain("src=\"/", WebInjection.Tag);
        Assert.Contains("href=\"../FullUI/web/fullui.css\"", WebInjection.Tag);
        Assert.Contains("src=\"../FullUI/web/fullui.js\"", WebInjection.Tag);

        foreach (var (page, expected) in new[]
        {
            ("http://h/web/index.html", "http://h/FullUI/web/fullui.js"),
            ("http://h/jellyfin/web/index.html", "http://h/jellyfin/FullUI/web/fullui.js"),
            ("http://h/a/b/web/", "http://h/a/b/FullUI/web/fullui.js"),
        })
        {
            Assert.Equal(expected, new Uri(new Uri(page), "../FullUI/web/fullui.js").ToString());
        }
    }

    public class JObjectLike
    {
        public string Json { get; }

        private JObjectLike(string json) => Json = json;

        public static JObjectLike Parse(string json) => new(json);
    }

    public class DtoPayload
    {
        public string? Id { get; set; }

        public string? FileNamePattern { get; set; }

        public string? CallbackMethod { get; set; }
    }

    [Fact]
    public void Payload_IsBuiltFromWhateverTypeTheOtherPluginDeclares()
    {
        // B-06: File Transformation takes a Newtonsoft JObject from its own copy of the library; we cannot reference that type.
        var parsed = Assert.IsType<JObjectLike>(WebInjection.BuildPayload(typeof(JObjectLike)));
        using var doc = JsonDocument.Parse(parsed.Json);
        Assert.Equal("index.html", doc.RootElement.GetProperty("fileNamePattern").GetString());
        Assert.Equal(nameof(WebInjection.Transform), doc.RootElement.GetProperty("callbackMethod").GetString());
        Assert.Equal(typeof(WebInjection).FullName, doc.RootElement.GetProperty("callbackClass").GetString());
        Assert.Equal(WebInjection.PluginId, doc.RootElement.GetProperty("id").GetString());

        Assert.IsType<string>(WebInjection.BuildPayload(typeof(string)));

        var dto = Assert.IsType<DtoPayload>(WebInjection.BuildPayload(typeof(DtoPayload)));
        Assert.Equal("index.html", dto.FileNamePattern);
        Assert.Equal(WebInjection.PluginId, dto.Id);
    }

    [Fact]
    public void PluginId_MatchesThePluginAndTheSettingsPage()
    {
        Assert.Equal(WebInjection.PluginId, ((Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin))).Id.ToString());
    }

    [Fact]
    public void TryRegister_WithoutFileTransformation_ExplainsItself_InPlainEnglish()
    {
        var result = WebInjection.TryRegister();

        Assert.Equal(InjectionOutcome.NotReady, result.Outcome);
        Assert.Contains("File Transformation", result.Message);
        Assert.False(WebInjection.Status.Registered);
        Assert.Equal(result.Message, WebInjection.Status.Message);
        Assert.True(WebInjection.Status.Attempts >= 1);
    }

    [Fact]
    public async Task HostedService_RetriesUntilTheOtherPluginIsReady_AndLogsEachStep()
    {
        var calls = 0;
        var svc = new WebInjectionHostedService(
            NullLogger<WebInjectionHostedService>.Instance,
            () => ++calls < 3
                ? new InjectionResult(InjectionOutcome.NotReady, "not yet")
                : new InjectionResult(InjectionOutcome.Registered, "done"),
            new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero });

        await svc.StartAsync(default);
        await svc.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, calls);
        await svc.StopAsync(default);
    }

    [Fact]
    public async Task HostedService_GivesUpAfterTheLastAttempt_WithoutThrowing()
    {
        var calls = 0;
        var svc = new WebInjectionHostedService(
            NullLogger<WebInjectionHostedService>.Instance,
            () =>
            {
                calls++;
                return new InjectionResult(InjectionOutcome.NotReady, "never");
            },
            new[] { TimeSpan.Zero, TimeSpan.Zero });

        await svc.StartAsync(default);
        await svc.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, calls); // first try + one per delay
    }

    [Fact]
    public async Task HostedService_StopsRetrying_OnIncompatiblePlugin_AndSurvivesAThrowingRegistration()
    {
        var calls = 0;
        var incompatible = new WebInjectionHostedService(
            NullLogger<WebInjectionHostedService>.Instance,
            () =>
            {
                calls++;
                return new InjectionResult(InjectionOutcome.Incompatible, "wrong version");
            },
            new[] { TimeSpan.Zero, TimeSpan.Zero });
        await incompatible.StartAsync(default);
        await incompatible.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);

        var throwing = new WebInjectionHostedService(
            NullLogger<WebInjectionHostedService>.Instance,
            () => throw new InvalidOperationException("boom"),
            new[] { TimeSpan.Zero });
        await throwing.StartAsync(default);
        await throwing.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(WebInjection.Status.Registered);
    }
}
