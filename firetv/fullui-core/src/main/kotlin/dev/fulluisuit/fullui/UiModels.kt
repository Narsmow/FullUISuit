package dev.fulluisuit.fullui

/** Row kinds from the plugin (`HomeRow.Type`). */
enum class RowKind(
    val wire: String,
) {
    CONTINUE("continue"),
    TOP_PICKS("toppicks"),
    BECAUSE("because"),
    TOP10("top10"),
    TRENDING("trending"),
    MY_LIST("mylist"),
    RECENT("recent"),
    GENRE("genre"),
    HIDDEN("hidden"),
    AGAIN("again"),
    NEW_SEASONS("newseasons"),
    COMING_SOON("comingsoon"),
    OTHER(""),
    ;

    companion object {
        fun fromWire(type: String?): RowKind {
            val t = type?.trim()?.lowercase().orEmpty()
            return entries.firstOrNull { it.wire.isNotEmpty() && it.wire == t } ?: OTHER
        }
    }
}

enum class BadgeStyle {
    /** "#3 in Movies" */
    RANK,

    /** "New Season", "New Episodes" */
    NEW,

    /** "Recently Added" */
    RECENT,

    /** "Top Rated" */
    TOP_RATED,

    /** "Now on <Server>" (a requested title arrived) */
    NOW_ON,

    OTHER,
}

data class UiBadge(
    val text: String,
    val style: BadgeStyle,
)

/** A library title as the TV UI needs it. */
data class UiCard(
    val id: String,
    val name: String,
    val isSeries: Boolean,
    /** "2021  TV-MA  1h 52m" */
    val metaLine: String,
    val overview: String?,
    val genres: List<String>,
    val badges: List<UiBadge>,
    val trailerKey: String?,
    val tmdbId: Int?,
    val progress: Float?,
    val hasBackdrop: Boolean,
    val hasLogo: Boolean,
    val myRating: Int,
    val inMyList: Boolean,
    val rank: Int?,
    val communityRating: Float?,
)

data class UiComingSoon(
    val tmdbId: Int,
    val mediaType: String,
    val title: String,
    val overview: String?,
    val posterPath: String?,
    val backdropPath: String?,
    /** Human readable, e.g. "Nov 20" or "2026" */
    val releaseLabel: String?,
    val rawReleaseDate: String?,
    val trailerKey: String?,
    val myVote: Int,
)

data class UiRow(
    val id: String,
    val title: String,
    val kind: RowKind,
    val cards: List<UiCard>,
    val comingSoon: List<UiComingSoon>,
) {
    /** Top 10 rows draw a big rank numeral next to a poster. */
    val numbered: Boolean get() = kind == RowKind.TOP10 && cards.any { it.rank != null }

    /** Continue Watching shows a progress bar and uses landscape art. */
    val showsProgress: Boolean get() = kind == RowKind.CONTINUE

    val isEmpty: Boolean get() = cards.isEmpty() && comingSoon.isEmpty()
}

data class HomeUi(
    val serverName: String,
    /** ARGB, null when the server sent none/invalid */
    val accentArgb: Int?,
    val rows: List<UiRow>,
    val hero: UiCard?,
)

/** "My <Server>" tab: Continue Watching, My List and the titles the user asked for. */
data class MyServerUi(
    val continueWatching: List<UiCard>,
    val myList: List<UiCard>,
    val wanted: List<UiComingSoon>,
) {
    val isEmpty: Boolean get() = continueWatching.isEmpty() && myList.isEmpty() && wanted.isEmpty()
}
