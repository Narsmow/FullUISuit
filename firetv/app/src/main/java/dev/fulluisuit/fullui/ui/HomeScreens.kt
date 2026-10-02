package dev.fulluisuit.fullui.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.LocalBringIntoViewSpec
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.focusGroup
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredSize
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusRestorer
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import com.github.damontecres.wholphin.ui.FontAwesome
import com.github.damontecres.wholphin.ui.components.Button
import com.github.damontecres.wholphin.ui.tryRequestFocus
import com.github.damontecres.wholphin.ui.util.ScrollToTopBringIntoViewSpec
import dev.fulluisuit.fullui.HomeUi
import dev.fulluisuit.fullui.ImageUrls
import dev.fulluisuit.fullui.UiCard
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private const val HERO_TRAILER_DELAY_MS = 2_000L

/** Hero / billboard: backdrop (slow zoom), optional muted trailer, synopsis and Play / More Info. */
@Composable
fun HeroBillboard(
    card: UiCard,
    images: ImageUrls,
    onPlay: () -> Unit,
    onInfo: () -> Unit,
    playFocusRequester: FocusRequester,
    modifier: Modifier = Modifier,
) {
    val config = LocalFullUiConfig.current
    val key = card.trailerKey
    var trailerReady by remember(card.id) { mutableStateOf(false) }
    var trailerPlaying by remember(card.id) { mutableStateOf(false) }
    var trailerFailed by remember(card.id) { mutableStateOf(false) }
    var muted by remember { mutableStateOf(true) }
    LaunchedEffect(card.id) {
        trailerReady = false
        if (config.trailers && !config.reduceMotion && key != null) {
            delay(HERO_TRAILER_DELAY_MS)
            trailerReady = true
        }
    }

    val transition = rememberInfiniteTransition(label = "heroZoom")
    val zoom by transition.animateFloat(
        initialValue = 1f,
        targetValue = 1.08f,
        animationSpec = infiniteRepeatable(tween(durationMillis = 26_000, easing = LinearEasing), RepeatMode.Reverse),
        label = "heroZoomScale",
    )

    Box(modifier.fillMaxWidth().height(FullUiDims.HeroHeight).clipToBounds().background(Color.Black)) {
        // Backdrop: always present, the trailer fades in over it and it is the fallback if the embed fails.
        val backdropUrl = images.backdrop(card.id, 1280) ?: images.primary(card.id, 1280)
        if (backdropUrl != null) {
            val scale = if (trailerPlaying || config.reduceMotion) 1f else zoom
            AsyncImage(
                model = backdropUrl,
                contentDescription = null,
                contentScale = ContentScale.Crop,
                modifier =
                    Modifier
                        .fillMaxSize()
                        .graphicsLayer {
                            scaleX = scale
                            scaleY = scale
                        },
            )
        }
        if (trailerReady && !trailerFailed && key != null) {
            BoxWithConstraints(Modifier.fillMaxSize()) {
                YouTubeTrailer(
                    videoKey = key,
                    muted = muted,
                    onPlaying = { trailerPlaying = true },
                    onEnded = {
                        trailerPlaying = false
                        trailerReady = false
                    },
                    onFailed = {
                        trailerFailed = true
                        trailerPlaying = false
                    },
                    modifier =
                        Modifier
                            .align(Alignment.Center)
                            .requiredSize(maxWidth, maxWidth * 9f / 16f)
                            .alpha(if (trailerPlaying) 1f else 0f),
                )
            }
        }
        // Scrims for legibility
        Box(
            Modifier
                .fillMaxSize()
                .background(
                    Brush.horizontalGradient(
                        0f to Color.Black.copy(alpha = 0.85f),
                        0.5f to Color.Black.copy(alpha = 0.4f),
                        1f to Color.Transparent,
                    ),
                ),
        )
        Box(
            Modifier
                .fillMaxSize()
                .background(
                    Brush.verticalGradient(
                        0f to Color.Black.copy(alpha = 0.5f),
                        0.25f to Color.Transparent,
                        0.65f to Color.Transparent,
                        1f to FullUiColors.Background,
                    ),
                ),
        )

        Column(
            modifier =
                Modifier
                    .align(Alignment.BottomStart)
                    .padding(start = FullUiDims.Gutter, bottom = 30.dp)
                    .fillMaxWidth(0.5f),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            var logoError by remember(card.id) { mutableStateOf(false) }
            val logoUrl = if (card.hasLogo && !logoError) images.logo(card.id, 600) else null
            if (logoUrl != null) {
                AsyncImage(
                    model = logoUrl,
                    contentDescription = card.name,
                    contentScale = ContentScale.Fit,
                    alignment = Alignment.CenterStart,
                    onError = { logoError = true },
                    modifier = Modifier.height(64.dp).widthIn(max = 260.dp),
                )
            } else {
                Text(
                    text = card.name,
                    color = FullUiColors.TextPrimary,
                    fontSize = 28.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                card.badges.take(2).forEach { BadgeChip(it) }
                if (card.metaLine.isNotEmpty()) {
                    Text(card.metaLine, color = FullUiColors.TextSecondary, fontSize = 11.sp, maxLines = 1)
                }
            }
            card.overview?.let {
                Text(
                    text = it,
                    color = FullUiColors.TextPrimary,
                    fontSize = 12.sp,
                    lineHeight = 16.sp,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                Button(
                    onClick = onPlay,
                    modifier = Modifier.focusRequester(playFocusRequester),
                    contentHeight = 30.dp,
                    contentPadding = PaddingValues(horizontal = 18.dp),
                    colors = heroButtonColors(primary = true),
                ) {
                    Text(FaGlyph.Play, fontFamily = FontAwesome, fontSize = 11.sp)
                    Spacer(Modifier.padding(horizontal = 4.dp))
                    Text("Play", fontSize = 13.sp, fontWeight = FontWeight.Bold)
                }
                Button(
                    onClick = onInfo,
                    contentHeight = 30.dp,
                    contentPadding = PaddingValues(horizontal = 18.dp),
                    colors = heroButtonColors(primary = false),
                ) {
                    Text(FaGlyph.Info, fontFamily = FontAwesome, fontSize = 11.sp)
                    Spacer(Modifier.padding(horizontal = 4.dp))
                    Text("More Info", fontSize = 13.sp, fontWeight = FontWeight.Bold)
                }
                if (trailerPlaying) {
                    GlyphButton(
                        glyph = if (muted) FaGlyph.VolumeMute else FaGlyph.VolumeHigh,
                        description = if (muted) "Unmute trailer" else "Mute trailer",
                        active = false,
                        onClick = { muted = !muted },
                    )
                }
            }
        }
    }
}

@Composable
private fun heroButtonColors(primary: Boolean) =
    ClickableSurfaceDefaults.colors(
        containerColor = if (primary) Color.White else Color(0xFF6D6D6E).copy(alpha = 0.7f),
        contentColor = if (primary) Color.Black else Color.White,
        focusedContainerColor = if (primary) Color.White else Color(0xFFE5E5E5),
        focusedContentColor = Color.Black,
        pressedContainerColor = Color.White,
        pressedContentColor = Color.Black,
    )

/**
 * Scrollable page: hero, then the rows. Used for Home, Shows and Movies.
 *
 * The hero lives in a layer *behind* the list (not inside it) so its buttons never take part in the
 * list's scroll-to-top focus behaviour; it is moved up by the list's scroll offset instead and the
 * list is scrolled back to the top whenever a hero button gets focus.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun HomeContent(
    ui: HomeUi,
    images: ImageUrls,
    actions: CardActions,
    modifier: Modifier = Modifier,
    /** Small overlay in the top-right corner below the nav bar (e.g. "Browse all shows"). */
    overlay: (@Composable () -> Unit)? = null,
) {
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    val playFocus = remember { FocusRequester() }
    val density = LocalDensity.current
    val spaceAbovePx = remember(density) { with(density) { 28.dp.toPx() } }
    val heroHeightPx = remember(density) { with(density) { FullUiDims.HeroHeight.roundToPx() } }
    val defaultSpec = LocalBringIntoViewSpec.current

    LaunchedEffect(Unit) {
        // Land on the hero's Play button, like a billboard should
        if (ui.hero != null) {
            delay(200)
            playFocus.tryRequestFocus()
        }
    }

    if (ui.hero == null && ui.rows.isEmpty()) {
        Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(
                text = "Nothing here yet. Watch something and we'll start recommending.",
                color = FullUiColors.TextSecondary,
                fontSize = 14.sp,
            )
        }
        return
    }

    Box(modifier.fillMaxSize()) {
        val hero = ui.hero
        val heroVisible by remember { derivedStateOf { listState.firstVisibleItemIndex == 0 } }
        if (hero != null && heroVisible) {
            Box(
                Modifier
                    .offset {
                        IntOffset(0, -minOf(listState.firstVisibleItemScrollOffset, heroHeightPx))
                    }.onFocusChanged {
                        if (it.hasFocus) scope.launch { listState.animateScrollToItem(0) }
                    }.focusGroup(),
            ) {
                HeroBillboard(
                    card = hero,
                    images = images,
                    onPlay = { actions.onPlay(hero) },
                    onInfo = { actions.onInfo(hero) },
                    playFocusRequester = playFocus,
                )
            }
        }

        CompositionLocalProvider(LocalBringIntoViewSpec provides ScrollToTopBringIntoViewSpec(spaceAbovePx)) {
            LazyColumn(
                state = listState,
                contentPadding = PaddingValues(bottom = 120.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
                modifier = Modifier.fillMaxSize().focusRestorer(),
            ) {
                if (hero != null) {
                    item(key = "hero-spacer") {
                        Spacer(Modifier.height(FullUiDims.HeroHeight - 20.dp))
                    }
                } else {
                    item(key = "nav-spacer") { Spacer(Modifier.height(FullUiDims.NavHeight)) }
                }
                items(ui.rows, key = { it.id }) { row ->
                    CompositionLocalProvider(LocalBringIntoViewSpec provides defaultSpec) {
                        ContentRow(row = row, images = images, actions = actions)
                    }
                }
            }
        }

        if (overlay != null) {
            Box(
                Modifier
                    .align(Alignment.TopEnd)
                    .padding(top = FullUiDims.NavHeight, end = FullUiDims.Gutter),
            ) { overlay() }
        }
    }
}
