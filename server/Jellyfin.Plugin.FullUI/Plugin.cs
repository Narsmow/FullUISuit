using System;
using System.Collections.Generic;
using Jellyfin.Plugin.FullUI.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FullUI;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "FullUI";

    public override Guid Id => Guid.Parse("7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57");

    public override string Description => "A cinematic home screen with personalized rows, Coming Soon, requests and optional local AI search.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.settings.html",
        };

        yield return new PluginPageInfo
        {
            Name = "FullUIRequests",
            DisplayName = "FullUI Requests",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.requests.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "star",
        };

        yield return new PluginPageInfo
        {
            Name = "FullUIHealth",
            DisplayName = "FullUI Health",
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.health.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "healing",
        };
    }
}
