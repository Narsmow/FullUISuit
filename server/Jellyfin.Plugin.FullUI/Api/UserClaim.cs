using System;
using System.Security.Claims;

namespace Jellyfin.Plugin.FullUI.Api;

/// <summary>The one place a FullUI endpoint learns who is calling: the Jellyfin token's claim, never anything the client sends.</summary>
internal static class UserClaim
{
    public static bool TryGet(ClaimsPrincipal? user, out Guid userId)
    {
        userId = Guid.Empty;
        var claim = user?.FindFirst("Jellyfin-UserId")?.Value;
        return Guid.TryParse(claim, out userId) && userId != Guid.Empty;
    }
}
