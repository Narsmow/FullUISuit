package dev.fulluisuit.fullui.ui

import android.app.ActivityManager
import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.remember
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp

/**
 * Visual tokens from `.claude/skills/netflix-ui-spec/SKILL.md`.
 * The TV canvas is 960x540 dp on a 1080p Fire TV, so all sizes below are chosen for that.
 */
object FullUiColors {
    val Background = Color(0xFF141414)
    val Surface = Color(0xFF181818)
    val TextPrimary = Color.White
    val TextSecondary = Color(0xFFB3B3B3)
    val DefaultAccent = Color(0xFFE50914)
    val Scrim = Color(0xCC000000)
}

object FullUiDims {
    /** Page gutter (TV safe area). */
    val Gutter = 48.dp
    val NavHeight = 52.dp
    val HeroHeight = 300.dp

    val CardWidth = 170.dp
    val CardWidthExpanded = 252.dp
    val CardGap = 8.dp

    /** Height of the info + action panel shown under a focused card. */
    val PanelHeight = 112.dp
}

/** Font Awesome (solid) glyphs; render with [com.github.damontecres.wholphin.ui.FontAwesome]. */
object FaGlyph {
    const val Play = ""
    const val Plus = "+"
    const val Check = ""
    const val ThumbUp = ""
    const val ThumbDown = ""
    const val Heart = ""
    const val Info = ""
    const val Bell = ""
    const val Gear = ""
    const val Search = ""
    const val User = ""
    const val VolumeHigh = ""
    const val VolumeMute = ""
    const val Bookmark = ""
}

/** Runtime switches for the FullUI screens. */
data class FullUiConfig(
    /** Autoplay muted YouTube trailers in the hero and on focused cards. */
    val trailers: Boolean = true,
    /** Trailers inside focused cards (heavier than the hero one). */
    val cardTrailers: Boolean = true,
    /** System animations are off: no zoom and no autoplaying trailers. */
    val reduceMotion: Boolean = false,
)

val LocalFullUiConfig = compositionLocalOf { FullUiConfig() }

val LocalFullUiAccent = compositionLocalOf { FullUiColors.DefaultAccent }

/** Low-RAM devices (Fire TV Stick Lite, older sticks) get no per-card trailers. */
@Composable
fun rememberFullUiConfig(): FullUiConfig {
    val context = LocalContext.current
    return remember(context) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
        val lowRam = am?.isLowRamDevice == true
        val reduce =
            try {
                android.provider.Settings.Global
                    .getFloat(context.contentResolver, android.provider.Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f
            } catch (_: Exception) {
                false
            }
        FullUiConfig(trailers = true, cardTrailers = !lowRam && !reduce, reduceMotion = reduce)
    }
}
