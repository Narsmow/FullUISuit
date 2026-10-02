package dev.fulluisuit.fullui

/** Builds image/trailer URLs the way `docs/api-contract.md` prescribes. */
class ImageUrls(
    private val baseUrl: () -> String?,
) {
    private fun base(): String? = baseUrl()?.trim()?.trimEnd('/')?.takeIf { it.isNotEmpty() }

    fun primary(
        id: String,
        maxWidth: Int,
    ): String? = item(id, "Primary", maxWidth)

    fun backdrop(
        id: String,
        maxWidth: Int,
    ): String? = item(id, "Backdrop", maxWidth)

    fun logo(
        id: String,
        maxWidth: Int,
    ): String? = item(id, "Logo", maxWidth)

    /** 16:9 art for a card: backdrop when the title has one, otherwise the poster. */
    fun landscape(
        card: UiCard,
        maxWidth: Int,
    ): String? = if (card.hasBackdrop) backdrop(card.id, maxWidth) else primary(card.id, maxWidth)

    private fun item(
        id: String,
        type: String,
        maxWidth: Int,
    ): String? = base()?.let { "$it/Items/${Ids.undashed(id)}/Images/$type?maxWidth=$maxWidth&quality=90" }

    companion object {
        /** TMDB poster (w342) or backdrop (w780) for Coming Soon cards. */
        fun tmdbPoster(path: String?): String? = tmdb(path, "w342")

        fun tmdbBackdrop(path: String?): String? = tmdb(path, "w780")

        private fun tmdb(
            path: String?,
            size: String,
        ): String? = path?.trim()?.takeIf { it.isNotEmpty() }?.let { "https://image.tmdb.org/t/p/$size/${it.trimStart('/')}" }

        fun youtubeWatchUrl(key: String): String = "https://www.youtube.com/watch?v=$key"

        /** Embed URL used by the IFrame trailer player. */
        fun youtubeEmbedUrl(key: String): String =
            "https://www.youtube.com/embed/$key?autoplay=1&mute=1&controls=0&rel=0&playsinline=1&modestbranding=1&iv_load_policy=3"
    }
}
