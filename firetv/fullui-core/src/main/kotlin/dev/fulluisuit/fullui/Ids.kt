package dev.fulluisuit.fullui

import java.util.UUID

/** Helpers for the id strings the plugin sends (Jellyfin ids may be 32 hex chars or dashed). */
object Ids {
    private val HEX32 = Regex("^[0-9a-fA-F]{32}$")

    /** Parse a Jellyfin id in either "N" (no dashes) or "D" (dashed) format. */
    fun parse(id: String?): UUID? {
        if (id.isNullOrBlank()) return null
        val s = id.trim()
        return try {
            if (HEX32.matches(s)) {
                UUID.fromString(
                    s.substring(0, 8) + "-" + s.substring(8, 12) + "-" + s.substring(12, 16) + "-" +
                        s.substring(16, 20) + "-" + s.substring(20),
                )
            } else {
                UUID.fromString(s)
            }
        } catch (_: IllegalArgumentException) {
            null
        }
    }

    /** Dashed lower-case form, which is what ASP.NET Guid binding always accepts. */
    fun dashed(id: String): String = parse(id)?.toString() ?: id

    /** The 32-hex form Jellyfin uses in image URLs. */
    fun undashed(id: String): String = (parse(id)?.toString() ?: id).replace("-", "")
}
