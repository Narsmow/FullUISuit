using Jellyfin.Plugin.FullUI.Compat;
using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.FullUI.Discovery;

public interface IUserDirectory
{
    IReadOnlyList<Guid> UserIds { get; }

    /// <summary>User name for display in the admin UI, or null when the user no longer exists.</summary>
    string? NameOf(Guid userId);

    /// <summary>
    /// The user's parental-rating cap in Jellyfin's rating-score scale, or null when the user has no cap (or the user is unknown).
    /// Titles that are not in the library (Coming Soon) are checked against this using TMDB age ratings.
    /// </summary>
    int? MaxParentalRatingScore(Guid userId) => null;
}

public sealed class JellyfinUserDirectory : IUserDirectory
{
    private readonly IUserManager _users;

    public JellyfinUserDirectory(IUserManager users)
    {
        _users = users;
    }

    public IReadOnlyList<Guid> UserIds => UserManagerCompat.GetUserIds(_users);

    public string? NameOf(Guid userId)
    {
        var u = _users.GetUserById(userId);
        return u?.Username;
    }

    public int? MaxParentalRatingScore(Guid userId) => _users.GetUserById(userId)?.MaxParentalRatingScore;
}

/// <summary>Turns an age rating such as "PG-13" into Jellyfin's numeric rating score so it can be compared with a user's cap.</summary>
public interface IRatingScorer
{
    /// <summary>The score, or null when the rating is not recognised.</summary>
    int? Score(string rating, string country);
}

public sealed class JellyfinRatingScorer : IRatingScorer
{
    private readonly ILocalizationManager _localization;

    public JellyfinRatingScorer(ILocalizationManager localization)
    {
        _localization = localization;
    }

    public int? Score(string rating, string country)
    {
        var s = _localization.GetRatingScore(rating, country);
        return s?.Score;
    }
}

/// <summary>Pure parental-control decision for titles that are not in the library.</summary>
public static class ParentalGate
{
    /// <summary>
    /// A user without a cap sees everything. A capped user sees a title only when TMDB gave it an age rating that is
    /// recognised and not above the cap: no rating, or an unrecognised rating, means hidden.
    /// </summary>
    public static bool Allows(int? userCap, TmdbCertification? certification, IRatingScorer? scorer)
    {
        if (userCap is null)
        {
            return true;
        }

        if (certification is null || scorer is null)
        {
            return false;
        }

        var score = scorer.Score(certification.Rating, certification.Country);
        return score is not null && score <= userCap;
    }

    public static string Format(TmdbCertification? c) => c is null ? string.Empty : c.Country + ":" + c.Rating;

    public static TmdbCertification? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var i = stored.IndexOf(':', StringComparison.Ordinal);
        return i > 0 && i < stored.Length - 1 ? new TmdbCertification(stored[..i], stored[(i + 1)..]) : null;
    }
}
