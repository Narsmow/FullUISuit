namespace Jellyfin.Plugin.FullUI.ApiTests;

public static class Dates
{
    /// <summary>A release date that is always in the future when the test runs.</summary>
    public static string Soon => DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd");

    public static string Past => DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

    public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");
}
