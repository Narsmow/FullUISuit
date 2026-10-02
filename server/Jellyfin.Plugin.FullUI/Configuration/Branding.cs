using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.FullUI.Configuration;

/// <summary>Validates admin-typed branding values so a typo can never make the UI unreadable.</summary>
public static class Branding
{
    public const string DefaultAccent = "#e5383b";

    private static readonly Regex Hex = new("^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    /// <summary>
    /// Returns a valid CSS hex color. "e50914" (missing #) is repaired; anything else invalid falls back to the default red.
    /// </summary>
    public static string NormalizeAccent(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            return DefaultAccent;
        }

        if (v[0] != '#')
        {
            v = "#" + v;
        }

        return Hex.IsMatch(v) ? v.ToLowerInvariant() : DefaultAccent;
    }
}
