using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FullUI.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    // Branding
    public string ServerName { get; set; } = "FullUI";
    public string AccentColor { get; set; } = "#e50914";

    // TMDB (v3 API key or v4 read access token). Never sent to clients.
    public string TmdbApiKey { get; set; } = string.Empty;
    public string TmdbLanguage { get; set; } = "en-US";
    public string TmdbRegion { get; set; } = "US";

    // Ollama (optional AI layer)
    public bool OllamaEnabled { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaEmbedModel { get; set; } = "nomic-embed-text";
    public string OllamaChatModel { get; set; } = "llama3.2:3b";

    // Recommendations
    public int TopTenWindowDays { get; set; } = 7;

    /// <summary>User ids (N format) excluded from Top 10 / Trending popularity.</summary>
    public string[] ExcludedUserIds { get; set; } = System.Array.Empty<string>();

    /// <summary>Also write per-user "FullUI:" playlists for clients that ignore the web reskin.</summary>
    public bool MaterializePlaylists { get; set; }

    public bool RequestNotifications { get; set; } = true;

    /// <summary>Lets admins switch off trailer autoplay for everyone (clients load the YouTube player only when this is true).</summary>
    public bool TrailersEnabled { get; set; } = true;
}
