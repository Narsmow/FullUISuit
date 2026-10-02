package androidx.tv.material3

import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextLayoutResult
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.TextUnit

@Composable
fun Text(
    text: String,
    modifier: Modifier = Modifier,
    color: Color = Color.Unspecified,
    fontSize: TextUnit = TextUnit.Unspecified,
    fontStyle: FontStyle? = null,
    fontWeight: FontWeight? = null,
    fontFamily: FontFamily? = null,
    letterSpacing: TextUnit = TextUnit.Unspecified,
    textDecoration: TextDecoration? = null,
    textAlign: TextAlign? = null,
    lineHeight: TextUnit = TextUnit.Unspecified,
    overflow: TextOverflow = TextOverflow.Clip,
    softWrap: Boolean = true,
    maxLines: Int = Int.MAX_VALUE,
    minLines: Int = 1,
    onTextLayout: (TextLayoutResult) -> Unit = {},
    style: TextStyle = TextStyle.Default,
) {}

class CardScale
class CardShape
class CardColors
class CardBorder
class CardGlow

object CardDefaults {
    fun scale(
        scale: Float = 1f,
        focusedScale: Float = 1.1f,
        pressedScale: Float = 1f,
        disabledScale: Float = 1f,
        focusedDisabledScale: Float = 1f,
    ): CardScale = CardScale()

    fun colors(): CardColors = CardColors()
}

@Composable
fun Card(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onLongClick: (() -> Unit)? = null,
    shape: CardShape = CardShape(),
    colors: CardColors = CardDefaults.colors(),
    scale: CardScale = CardDefaults.scale(),
    border: CardBorder = CardBorder(),
    glow: CardGlow = CardGlow(),
    interactionSource: MutableInteractionSource? = null,
    content: @Composable ColumnScope.() -> Unit,
) {}

class ClickableSurfaceColors

object ClickableSurfaceDefaults {
    fun colors(
        containerColor: Color = Color.Transparent,
        contentColor: Color = Color.Unspecified,
        focusedContainerColor: Color = Color.Unspecified,
        focusedContentColor: Color = Color.Unspecified,
        pressedContainerColor: Color = Color.Unspecified,
        pressedContentColor: Color = Color.Unspecified,
        disabledContainerColor: Color = Color.Unspecified,
        disabledContentColor: Color = Color.Unspecified,
    ): ClickableSurfaceColors = ClickableSurfaceColors()
}

class Typography {
    val displayMedium: TextStyle = TextStyle.Default
}

object MaterialTheme {
    val typography: Typography = Typography()
}
