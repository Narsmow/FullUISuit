package dev.fulluisuit.fullui

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * Wire models for the FullUI server plugin (`/FullUI/...`).
 *
 * These mirror `server/Jellyfin.Plugin.FullUI/Api/Dtos.cs` and `docs/api-contract.md`.
 * The server sends camelCase JSON. Everything optional on the server is nullable/defaulted
 * here so that a newer or older plugin never breaks parsing.
 */
val FullUiJson: Json =
    Json {
        ignoreUnknownKeys = true
        coerceInputValues = true
        isLenient = true
        encodeDefaults = true
        explicitNulls = false
    }

@Serializable
data class ItemCard(
    val id: String,
    val name: String = "",
    /** "Movie" or "Series" */
    val type: String = "Movie",
    val year: Int? = null,
    val rating: Float? = null,
    val rated: String? = null,
    val overview: String? = null,
    val genres: List<String> = emptyList(),
    val runtimeMinutes: Int? = null,
    val badges: List<String> = emptyList(),
    /** YouTube video id */
    val trailerKey: String? = null,
    val tmdbId: Int? = null,
    /** 0..1 when resumable */
    val progress: Double? = null,
    val hasBackdrop: Boolean = false,
    val hasLogo: Boolean = false,
    /** -1 not for me, 0 none, 1 like, 2 love */
    val myRating: Int = 0,
    val inMyList: Boolean = false,
    /** 1..10 in Top 10 rows */
    val rank: Int? = null,
)

@Serializable
data class ComingSoonCard(
    val tmdbId: Int,
    /** "movie" or "tv" */
    val mediaType: String = "movie",
    val title: String = "",
    val overview: String? = null,
    /** TMDB path; image url is https://image.tmdb.org/t/p/w342{path} */
    val posterPath: String? = null,
    val backdropPath: String? = null,
    val releaseDate: String? = null,
    val trailerKey: String? = null,
    /** 1 want, -1 not for me, 0 none */
    val myVote: Int = 0,
)

@Serializable
data class HomeRow(
    val id: String,
    val title: String = "",
    val type: String = "",
    val items: List<ItemCard> = emptyList(),
    val comingSoon: List<ComingSoonCard>? = null,
)

@Serializable
data class HomeResponse(
    val serverName: String = "",
    val accentColor: String = "",
    val rows: List<HomeRow> = emptyList(),
)

@Serializable
data class MyServerResponse(
    val continueWatching: List<ItemCard> = emptyList(),
    val myList: List<ItemCard> = emptyList(),
    val wanted: List<ComingSoonCard> = emptyList(),
)

@Serializable
data class ComingSoonResponse(
    val cards: List<ComingSoonCard> = emptyList(),
)

@Serializable
data class SearchResponse(
    /** "semantic" or "keyword" */
    val mode: String = "keyword",
    val items: List<ItemCard> = emptyList(),
)

@Serializable
data class NotificationDto(
    val id: String,
    val text: String = "",
    /** ISO-8601 timestamp */
    val at: String? = null,
    val read: Boolean = false,
    val itemId: String? = null,
)

@Serializable
data class NotificationsResponse(
    val items: List<NotificationDto> = emptyList(),
)

@Serializable
data class RateRequest(
    val itemId: String,
    val rating: Int,
)

@Serializable
data class MyListRequest(
    val itemId: String,
    val add: Boolean,
)

@Serializable
data class VoteRequest(
    val tmdbId: Int,
    val mediaType: String,
    val vote: Int,
    val title: String? = null,
    val posterPath: String? = null,
    val backdropPath: String? = null,
    val releaseDate: String? = null,
    val overview: String? = null,
)

@Serializable
data class MarkReadRequest(
    val ids: List<String>? = null,
)
