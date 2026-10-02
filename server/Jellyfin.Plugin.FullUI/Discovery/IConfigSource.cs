using Jellyfin.Plugin.FullUI.Configuration;

namespace Jellyfin.Plugin.FullUI.Discovery;

/// <summary>Indirection over the plugin configuration so services are unit-testable.</summary>
public interface IConfigSource
{
    PluginConfiguration Current { get; }
}

public sealed class PluginConfigSource : IConfigSource
{
    public PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
