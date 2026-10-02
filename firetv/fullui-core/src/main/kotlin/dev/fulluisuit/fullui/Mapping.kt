package dev.fulluisuit.fullui

/** Pure functions turning wire models into what the TV UI renders. */
object Mapping {
    private val RANK_BADGE = Regex("^#\\s*\\d+\\s+in\\s+.+", RegexOption.IGNORE_CASE)
    private val MONTHS =
        listOf("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")

    /** Rows the hero is taken from, in preference order. Continue Watching is never used. */
    private val HERO_ROW_ORDER =
        listOf(
            RowKind.TOP_PICKS,
            RowKind.TRENDING,
            RowKind.TOP10,
            RowKind.BECAUSE,
            RowKind.RECENT,
            RowKind.NEW_SEASONS,
            RowKind.GENRE,
            RowKind.AGAIN,
            RowKind.HIDDEN,
            RowKind.MY_LIST,
            RowKind.OTHER,
        )

    fun home(response: HomeResponse): HomeUi {
        val rows =
            response.rows
                .map { row(it) }
                .filterNot { it.isEmpty }
        return HomeUi(
            serverName = response.serverName.ifBlank { "Server" },
            accentArgb = parseAccent(response.accentColor),
            rows = rows,
            hero = pickHero(rows),
        )
    }

    fun myServer(r: MyServerResponse): MyServerUi =
        MyServerUi(
            continueWatching = r.continueWatching.map { card(it) },
            myList = r.myList.map { card(it) },
            wanted = r.wanted.map { comingSoon(it) },
        )

    fun row(row: HomeRow): UiRow {
        val kind = RowKind.fromWire(row.type)
        var cards = row.items.map { card(it) }
        if (kind == RowKind.TOP10) {
            // The server already limits to 10 and sets rank; make order robust anyway.
            cards = cards.sortedBy { it.rank ?: Int.MAX_VALUE }.take(10)
        }
        return UiRow(
            id = row.id,
            title = row.title,
            kind = kind,
            cards = cards,
            comingSoon = row.comingSoon.orEmpty().map { comingSoon(it) },
        )
    }

    fun card(c: ItemCard): UiCard =
        UiCard(
            id = c.id,
            name = c.name,
            isSeries = c.type.equals("Series", ignoreCase = true),
            metaLine = metaLine(c.year, c.rated, c.runtimeMinutes),
            overview = c.overview?.trim()?.takeIf { it.isNotEmpty() },
            genres = c.genres,
            badges = c.badges.map { badge(it) }.sortedBy { priority(it.style) },
            trailerKey = c.trailerKey?.trim()?.takeIf { it.isNotEmpty() },
            tmdbId = c.tmdbId,
            progress = c.progress?.coerceIn(0.0, 1.0)?.toFloat()?.takeIf { it > 0f },
            hasBackdrop = c.hasBackdrop,
            hasLogo = c.hasLogo,
            myRating = c.myRating.coerceIn(-1, 2),
            inMyList = c.inMyList,
            rank = c.rank?.takeIf { it in 1..10 },
            communityRating = c.rating,
        )

    fun comingSoon(c: ComingSoonCard): UiComingSoon =
        UiComingSoon(
            tmdbId = c.tmdbId,
            mediaType = c.mediaType,
            title = c.title,
            overview = c.overview?.trim()?.takeIf { it.isNotEmpty() },
            posterPath = c.posterPath,
            backdropPath = c.backdropPath,
            releaseLabel = releaseLabel(c.releaseDate),
            rawReleaseDate = c.releaseDate,
            trailerKey = c.trailerKey?.trim()?.takeIf { it.isNotEmpty() },
            myVote = c.myVote.coerceIn(-1, 1),
        )

    fun badge(text: String): UiBadge {
        val t = text.trim()
        val style =
            when {
                RANK_BADGE.matches(t) -> BadgeStyle.RANK
                t.startsWith("Now on ", ignoreCase = true) -> BadgeStyle.NOW_ON
                t.startsWith("New ", ignoreCase = true) -> BadgeStyle.NEW
                t.equals("Recently Added", ignoreCase = true) -> BadgeStyle.RECENT
                t.equals("Top Rated", ignoreCase = true) -> BadgeStyle.TOP_RATED
                else -> BadgeStyle.OTHER
            }
        return UiBadge(t, style)
    }

    /** Lower is more important. Used to order and to cut the ribbon down to [limit]. */
    private fun priority(style: BadgeStyle): Int =
        when (style) {
            BadgeStyle.NOW_ON -> 0
            BadgeStyle.RANK -> 1
            BadgeStyle.NEW -> 2
            BadgeStyle.RECENT -> 3
            BadgeStyle.TOP_RATED -> 4
            BadgeStyle.OTHER -> 5
        }

    /** Badges to draw on the collapsed card (the expanded card shows all). */
    fun ribbon(
        card: UiCard,
        limit: Int = 1,
    ): List<UiBadge> = card.badges.take(limit)

    fun metaLine(
        year: Int?,
        rated: String?,
        runtimeMinutes: Int?,
    ): String =
        listOfNotNull(
            year?.toString(),
            rated?.trim()?.takeIf { it.isNotEmpty() },
            runtimeMinutes?.takeIf { it > 0 }?.let { runtime(it) },
        ).joinToString("  ·  ")

    fun runtime(minutes: Int): String {
        val h = minutes / 60
        val m = minutes % 60
        return when {
            h == 0 -> "${m}m"
            m == 0 -> "${h}h"
            else -> "${h}h ${m}m"
        }
    }

    /** "2026-11-20" -> "Nov 20, 2026"; "2026-11" / "2026" degrade gracefully; garbage -> null. */
    fun releaseLabel(date: String?): String? {
        val d = date?.trim()?.take(10)?.takeIf { it.isNotEmpty() } ?: return null
        val parts = d.split("-")
        val year = parts.getOrNull(0)?.toIntOrNull() ?: return null
        val month = parts.getOrNull(1)?.toIntOrNull()?.takeIf { it in 1..12 } ?: return year.toString()
        val day = parts.getOrNull(2)?.toIntOrNull()?.takeIf { it in 1..31 } ?: return "${MONTHS[month - 1]} $year"
        return "${MONTHS[month - 1]} $day, $year"
    }

    /** "#e50914", "e50914" or "#80e50914" (AARRGGBB). Returns packed ARGB or null. */
    fun parseAccent(value: String?): Int? {
        val v = value?.trim()?.removePrefix("#") ?: return null
        if (v.length != 6 && v.length != 8) return null
        val n = v.toLongOrNull(16) ?: return null
        return if (v.length == 6) (0xFF000000L or n).toInt() else n.toInt()
    }

    /**
     * Hero = first card (in [HERO_ROW_ORDER]) that has a backdrop and a synopsis, preferring one
     * with a trailer among the first few of each row. Deterministic.
     */
    fun pickHero(rows: List<UiRow>): UiCard? {
        val ordered =
            HERO_ROW_ORDER.flatMap { kind -> rows.filter { it.kind == kind } }
        for (row in ordered) {
            val usable = row.cards.filter { it.hasBackdrop && it.overview != null }
            val pick = usable.take(5).firstOrNull { it.trailerKey != null } ?: usable.firstOrNull()
            if (pick != null) return pick
        }
        // Last resort: anything with a backdrop (including Continue Watching).
        return rows.flatMap { it.cards }.firstOrNull { it.hasBackdrop }
    }

    /**
     * Thumbs behave like a radio group of 3 levels (not for me = -1, like = 1, love = 2);
     * pressing the active level again clears it (0).
     */
    fun nextRating(
        current: Int,
        pressed: Int,
    ): Int = if (current == pressed) 0 else pressed

    /** Same toggle semantics for I want this (1) / Not for me (-1) on Coming Soon. */
    fun nextVote(
        current: Int,
        pressed: Int,
    ): Int = if (current == pressed) 0 else pressed
}

// ---- optimistic local updates (after Rate / MyList / Vote succeed or are fired) ---------

fun HomeUi.updateCard(
    id: String,
    transform: (UiCard) -> UiCard,
): HomeUi {
    val rows2 =
        rows.map { r ->
            if (r.cards.none { it.id == id }) r else r.copy(cards = r.cards.map { if (it.id == id) transform(it) else it })
        }
    return copy(rows = rows2, hero = hero?.let { if (it.id == id) transform(it) else it })
}

fun HomeUi.updateVote(
    tmdbId: Int,
    mediaType: String,
    vote: Int,
): HomeUi =
    copy(
        rows =
            rows.map { r ->
                r.copy(
                    comingSoon =
                        r.comingSoon.map {
                            if (it.tmdbId == tmdbId && it.mediaType == mediaType) it.copy(myVote = vote) else it
                        },
                )
            },
    )

/**
 * The Shows / Movies tabs reuse the Home payload: keep only titles of that type (and Coming Soon
 * cards of the matching media type), drop rows that become empty and re-pick the hero.
 */
fun HomeUi.filterByType(series: Boolean): HomeUi {
    val mediaType = if (series) "tv" else "movie"
    val filtered =
        rows
            .map { r ->
                r.copy(
                    cards = r.cards.filter { it.isSeries == series },
                    comingSoon = r.comingSoon.filter { it.mediaType.equals(mediaType, ignoreCase = true) },
                )
            }.filterNot { it.isEmpty }
    return copy(rows = filtered, hero = Mapping.pickHero(filtered))
}

fun MyServerUi.updateCard(
    id: String,
    transform: (UiCard) -> UiCard,
): MyServerUi =
    copy(
        continueWatching = continueWatching.map { if (it.id == id) transform(it) else it },
        myList = myList.map { if (it.id == id) transform(it) else it },
    )

fun MyServerUi.updateVote(
    tmdbId: Int,
    mediaType: String,
    vote: Int,
): MyServerUi =
    copy(wanted = wanted.map { if (it.tmdbId == tmdbId && it.mediaType == mediaType) it.copy(myVote = vote) else it })
