package dev.fulluisuit.fullui.ui

import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.focusGroup
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.focusRestorer
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.tv.material3.Card
import androidx.tv.material3.CardDefaults
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.MaterialTheme
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import com.github.damontecres.wholphin.ui.FontAwesome
import com.github.damontecres.wholphin.ui.components.Button
import com.github.damontecres.wholphin.ui.rememberInt
import com.github.damontecres.wholphin.ui.tryRequestFocus
import dev.fulluisuit.fullui.BadgeStyle
import dev.fulluisuit.fullui.ImageUrls
import dev.fulluisuit.fullui.Mapping
import dev.fulluisuit.fullui.RowKind
import dev.fulluisuit.fullui.UiBadge
import dev.fulluisuit.fullui.UiCard
import dev.fulluisuit.fullui.UiComingSoon
import dev.fulluisuit.fullui.UiRow
import kotlinx.coroutines.delay

/** What the user can do with a title, wired to the view model by the screens. */
class CardActions(
    val onPlay: (UiCard) -> Unit,
    val onInfo: (UiCard) -> Unit,
    val onToggleList: (UiCard) -> Unit,
    /** pressed level: -1 not for me, 1 like, 2 love; pressing the active level clears it */
    val onRate: (UiCard, Int) -> Unit,
    /** pressed vote: 1 I want this, -1 not for me; pressing the active vote clears it */
    val onVote: (UiComingSoon, Int) -> Unit,
)

private val CollapsedImageHeight = FullUiDims.CardWidth * 9f / 16f
private val ExpandedImageHeight = FullUiDims.CardWidthExpanded * 9f / 16f

/** Height a row needs: just the artwork normally, artwork + info panel while it has focus. */
private fun rowHeight(focused: Boolean): Dp =
    if (focused) ExpandedImageHeight + FullUiDims.PanelHeight + 8.dp else CollapsedImageHeight + 8.dp

// ---------------------------------------------------------------------------------------------
// Row
// ---------------------------------------------------------------------------------------------

@Composable
fun ContentRow(
    row: UiRow,
    images: ImageUrls,
    actions: CardActions,
    modifier: Modifier = Modifier,
) {
    val state = rememberLazyListState()
    val firstFocus = remember { FocusRequester() }
    val rowFocus = remember { FocusRequester() }
    var position by rememberInt()
    var rowHasFocus by remember { mutableStateOf(false) }
    val height by animateDpAsState(rowHeight(rowHasFocus), label = "rowHeight")

    Column(
        modifier =
            modifier
                .onFocusChanged { rowHasFocus = it.hasFocus }
                .focusProperties { onEnter = { rowFocus.tryRequestFocus() } }
                .focusGroup(),
    ) {
        Text(
            text = row.title,
            color = FullUiColors.TextPrimary,
            fontSize = 16.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = FullUiDims.Gutter, bottom = 4.dp),
        )
        LazyRow(
            state = state,
            horizontalArrangement = Arrangement.spacedBy(FullUiDims.CardGap),
            contentPadding = PaddingValues(horizontal = FullUiDims.Gutter, vertical = 4.dp),
            modifier =
                Modifier
                    .fillMaxWidth()
                    .height(height)
                    .focusGroup()
                    .focusRestorer(firstFocus)
                    .focusRequester(rowFocus),
        ) {
            if (row.kind == RowKind.COMING_SOON && row.comingSoon.isNotEmpty()) {
                itemsIndexed(row.comingSoon, key = { _, c -> "${c.mediaType}-${c.tmdbId}" }) { index, cs ->
                    ComingSoonCard(
                        card = cs,
                        actions = actions,
                        modifier =
                            if (index == position) Modifier.focusRequester(firstFocus) else Modifier,
                        onFocused = { position = index },
                    )
                }
            }
            itemsIndexed(row.cards, key = { _, c -> c.id }) { index, card ->
                val itemIndex = index
                TitleCard(
                    card = card,
                    images = images,
                    actions = actions,
                    numbered = row.numbered,
                    showProgress = row.showsProgress,
                    modifier =
                        if (itemIndex == position && row.comingSoon.isEmpty()) {
                            Modifier.focusRequester(firstFocus)
                        } else {
                            Modifier
                        },
                    onFocused = { position = itemIndex },
                )
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Library title card
// ---------------------------------------------------------------------------------------------

@Composable
fun TitleCard(
    card: UiCard,
    images: ImageUrls,
    actions: CardActions,
    numbered: Boolean,
    showProgress: Boolean,
    modifier: Modifier = Modifier,
    onFocused: () -> Unit = {},
) {
    val config = LocalFullUiConfig.current
    val accent = LocalFullUiAccent.current
    var hasFocus by remember { mutableStateOf(false) }
    val width by animateDpAsState(
        if (hasFocus) FullUiDims.CardWidthExpanded else FullUiDims.CardWidth,
        label = "cardWidth",
    )

    // Trailer after a short dwell on the card
    var showTrailer by remember { mutableStateOf(false) }
    var trailerPlaying by remember { mutableStateOf(false) }
    var trailerFailed by remember(card.id) { mutableStateOf(false) }
    LaunchedEffect(hasFocus) {
        showTrailer = false
        trailerPlaying = false
        if (hasFocus && config.trailers && config.cardTrailers && card.trailerKey != null && !trailerFailed) {
            delay(TRAILER_DWELL_MS)
            showTrailer = true
        }
    }

    Column(
        modifier =
            modifier
                .width(width)
                .onFocusChanged {
                    hasFocus = it.hasFocus
                    if (it.hasFocus) onFocused()
                }.focusGroup(),
    ) {
        Card(
            onClick = { actions.onInfo(card) },
            onLongClick = { actions.onToggleList(card) },
            scale = CardDefaults.scale(focusedScale = 1f),
            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
        ) {
            Box(Modifier.fillMaxSize().background(FullUiColors.Surface)) {
                CardArt(url = images.landscape(card, 640), fallbackText = card.name)

                val key = card.trailerKey
                if (showTrailer && key != null && !trailerFailed) {
                    YouTubeTrailer(
                        videoKey = key,
                        muted = true,
                        onPlaying = { trailerPlaying = true },
                        onEnded = {
                            showTrailer = false
                            trailerPlaying = false
                        },
                        onFailed = {
                            trailerFailed = true
                            showTrailer = false
                            trailerPlaying = false
                        },
                        modifier = Modifier.fillMaxSize().alpha(if (trailerPlaying) 1f else 0f),
                    )
                }

                // Collapsed ribbon: the single most important badge
                if (!hasFocus) {
                    Mapping.ribbon(card, 1).firstOrNull()?.let {
                        BadgeChip(
                            badge = it,
                            modifier = Modifier.align(Alignment.TopStart).padding(6.dp),
                        )
                    }
                }
                val rank = card.rank
                if (numbered && rank != null) {
                    RankNumeral(
                        rank = rank,
                        modifier = Modifier.align(Alignment.BottomStart).padding(start = 8.dp, bottom = 2.dp),
                    )
                }
                val progress = card.progress
                if (showProgress && progress != null) {
                    val p = progress
                    Box(
                        Modifier
                            .align(Alignment.BottomStart)
                            .fillMaxWidth()
                            .height(3.dp)
                            .background(Color.White.copy(alpha = 0.3f)),
                    ) {
                        Box(Modifier.fillMaxWidth(p).fillMaxHeight().background(accent))
                    }
                }
            }
        }
        if (hasFocus) {
            TitlePanel(card = card, actions = actions)
        }
    }
}

@Composable
private fun TitlePanel(
    card: UiCard,
    actions: CardActions,
) {
    Column(
        modifier =
            Modifier
                .fillMaxWidth()
                .height(FullUiDims.PanelHeight)
                .background(FullUiColors.Surface)
                .padding(horizontal = 8.dp, vertical = 6.dp),
        verticalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        Text(
            text = card.name,
            color = FullUiColors.TextPrimary,
            fontSize = 12.sp,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Row(
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            card.badges.take(2).forEach { BadgeChip(it) }
            if (card.metaLine.isNotEmpty()) {
                Text(
                    text = card.metaLine,
                    color = FullUiColors.TextSecondary,
                    fontSize = 9.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        Text(
            text = card.overview ?: card.genres.joinToString(", "),
            color = FullUiColors.TextSecondary,
            fontSize = 9.sp,
            lineHeight = 12.sp,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            GlyphButton(FaGlyph.Play, "Play", active = false, onClick = { actions.onPlay(card) })
            GlyphButton(
                if (card.inMyList) FaGlyph.Check else FaGlyph.Plus,
                if (card.inMyList) "Remove from My List" else "Add to My List",
                active = card.inMyList,
                onClick = { actions.onToggleList(card) },
            )
            GlyphButton(FaGlyph.ThumbDown, "Not for me", active = card.myRating == -1, onClick = { actions.onRate(card, -1) })
            GlyphButton(FaGlyph.ThumbUp, "Like", active = card.myRating == 1, onClick = { actions.onRate(card, 1) })
            GlyphButton(FaGlyph.Heart, "Love", active = card.myRating == 2, onClick = { actions.onRate(card, 2) })
            GlyphButton(FaGlyph.Info, "More info", active = false, onClick = { actions.onInfo(card) })
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Coming Soon card
// ---------------------------------------------------------------------------------------------

@Composable
fun ComingSoonCard(
    card: UiComingSoon,
    actions: CardActions,
    modifier: Modifier = Modifier,
    onFocused: () -> Unit = {},
) {
    val config = LocalFullUiConfig.current
    var hasFocus by remember { mutableStateOf(false) }
    val width by animateDpAsState(
        if (hasFocus) FullUiDims.CardWidthExpanded else FullUiDims.CardWidth,
        label = "comingSoonWidth",
    )
    var showTrailer by remember { mutableStateOf(false) }
    var trailerPlaying by remember { mutableStateOf(false) }
    var trailerFailed by remember(card.tmdbId) { mutableStateOf(false) }
    LaunchedEffect(hasFocus) {
        showTrailer = false
        trailerPlaying = false
        if (hasFocus && config.trailers && config.cardTrailers && card.trailerKey != null && !trailerFailed) {
            delay(TRAILER_DWELL_MS)
            showTrailer = true
        }
    }
    val art = ImageUrls.tmdbBackdrop(card.backdropPath) ?: ImageUrls.tmdbPoster(card.posterPath)

    Column(
        modifier =
            modifier
                .width(width)
                .onFocusChanged {
                    hasFocus = it.hasFocus
                    if (it.hasFocus) onFocused()
                }.focusGroup(),
    ) {
        Card(
            // OK on the artwork = I want this
            onClick = { actions.onVote(card, 1) },
            scale = CardDefaults.scale(focusedScale = 1f),
            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
        ) {
            Box(Modifier.fillMaxSize().background(FullUiColors.Surface)) {
                CardArt(url = art, fallbackText = card.title)
                val key = card.trailerKey
                if (showTrailer && key != null && !trailerFailed) {
                    YouTubeTrailer(
                        videoKey = key,
                        muted = true,
                        onPlaying = { trailerPlaying = true },
                        onEnded = {
                            showTrailer = false
                            trailerPlaying = false
                        },
                        onFailed = {
                            trailerFailed = true
                            showTrailer = false
                            trailerPlaying = false
                        },
                        modifier = Modifier.fillMaxSize().alpha(if (trailerPlaying) 1f else 0f),
                    )
                }
                card.releaseLabel?.let {
                    Text(
                        text = it,
                        color = Color.White,
                        fontSize = 9.sp,
                        fontWeight = FontWeight.SemiBold,
                        modifier =
                            Modifier
                                .align(Alignment.BottomStart)
                                .padding(6.dp)
                                .background(Color.Black.copy(alpha = 0.7f), RoundedCornerShape(3.dp))
                                .padding(horizontal = 5.dp, vertical = 2.dp),
                    )
                }
                if (card.myVote == 1) {
                    Text(
                        text = "Reminder set",
                        color = Color.White,
                        fontSize = 8.sp,
                        modifier =
                            Modifier
                                .align(Alignment.TopStart)
                                .padding(6.dp)
                                .background(LocalFullUiAccent.current, RoundedCornerShape(3.dp))
                                .padding(horizontal = 5.dp, vertical = 2.dp),
                    )
                }
            }
        }
        if (hasFocus) {
            Column(
                modifier =
                    Modifier
                        .fillMaxWidth()
                        .height(FullUiDims.PanelHeight)
                        .background(FullUiColors.Surface)
                        .padding(horizontal = 8.dp, vertical = 6.dp),
                verticalArrangement = Arrangement.spacedBy(3.dp),
            ) {
                Text(
                    text = card.title,
                    color = FullUiColors.TextPrimary,
                    fontSize = 12.sp,
                    fontWeight = FontWeight.Bold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    text = listOfNotNull("Coming ${card.releaseLabel ?: "soon"}", if (card.mediaType == "tv") "Series" else "Movie").joinToString("  ·  "),
                    color = FullUiColors.TextSecondary,
                    fontSize = 9.sp,
                    maxLines = 1,
                )
                Text(
                    text = card.overview.orEmpty(),
                    color = FullUiColors.TextSecondary,
                    fontSize = 9.sp,
                    lineHeight = 12.sp,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    TextActionButton(
                        text = if (card.myVote == 1) "Wanted" else "I want this",
                        active = card.myVote == 1,
                        onClick = { actions.onVote(card, 1) },
                    )
                    TextActionButton(
                        text = "Not for me",
                        active = card.myVote == -1,
                        onClick = { actions.onVote(card, -1) },
                    )
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Small pieces
// ---------------------------------------------------------------------------------------------

@Composable
private fun CardArt(
    url: String?,
    fallbackText: String,
) {
    var error by remember(url) { mutableStateOf(false) }
    if (url != null && !error) {
        AsyncImage(
            model = url,
            contentDescription = fallbackText,
            contentScale = ContentScale.Crop,
            onError = { error = true },
            modifier = Modifier.fillMaxSize(),
        )
    } else {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(
                text = fallbackText,
                color = FullUiColors.TextSecondary,
                fontSize = 11.sp,
                textAlign = TextAlign.Center,
                maxLines = 3,
                modifier = Modifier.padding(8.dp),
            )
        }
    }
}

@Composable
fun BadgeChip(
    badge: UiBadge,
    modifier: Modifier = Modifier,
) {
    val accent = LocalFullUiAccent.current
    val bg =
        when (badge.style) {
            BadgeStyle.RANK -> accent
            BadgeStyle.NOW_ON -> accent
            BadgeStyle.NEW -> Color(0xFF2E7D32)
            BadgeStyle.RECENT -> Color(0xFF1565C0)
            BadgeStyle.TOP_RATED -> Color(0xFFB8860B)
            BadgeStyle.OTHER -> Color(0xFF424242)
        }
    Text(
        text = badge.text,
        color = Color.White,
        fontSize = 8.sp,
        fontWeight = FontWeight.Bold,
        maxLines = 1,
        modifier =
            modifier
                .clip(RoundedCornerShape(3.dp))
                .background(bg)
                .padding(horizontal = 4.dp, vertical = 1.dp),
    )
}

@Composable
private fun RankNumeral(
    rank: Int,
    modifier: Modifier = Modifier,
) {
    Text(
        text = rank.toString(),
        color = Color.White,
        fontSize = 44.sp,
        fontWeight = FontWeight.Black,
        modifier = modifier,
        style =
            MaterialTheme.typography.displayMedium.copy(
                fontSize = 44.sp,
                fontWeight = FontWeight.Black,
                color = Color.White,
                shadow =
                    androidx.compose.ui.graphics.Shadow(
                        color = Color.Black,
                        offset = androidx.compose.ui.geometry.Offset(2f, 2f),
                        blurRadius = 8f,
                    ),
            ),
    )
}

/** Small round icon button using a Font Awesome glyph. */
@Composable
fun GlyphButton(
    glyph: String,
    description: String,
    active: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val accent = LocalFullUiAccent.current
    Button(
        onClick = onClick,
        modifier = modifier,
        contentHeight = 24.dp,
        contentPadding = PaddingValues(horizontal = 6.dp, vertical = 0.dp),
        colors =
            ClickableSurfaceDefaults.colors(
                containerColor = if (active) accent else Color.White.copy(alpha = 0.18f),
                contentColor = Color.White,
                focusedContainerColor = Color.White,
                focusedContentColor = Color.Black,
                pressedContainerColor = Color.White,
                pressedContentColor = Color.Black,
            ),
    ) {
        Text(
            text = glyph,
            fontFamily = FontAwesome,
            fontSize = 10.sp,
            textAlign = TextAlign.Center,
            modifier = Modifier.width(14.dp),
        )
    }
}

/** Small pill button with text. */
@Composable
fun TextActionButton(
    text: String,
    active: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val accent = LocalFullUiAccent.current
    Button(
        onClick = onClick,
        modifier = modifier,
        contentHeight = 24.dp,
        contentPadding = PaddingValues(horizontal = 10.dp, vertical = 0.dp),
        colors =
            ClickableSurfaceDefaults.colors(
                containerColor = if (active) accent else Color.White.copy(alpha = 0.18f),
                contentColor = Color.White,
                focusedContainerColor = Color.White,
                focusedContentColor = Color.Black,
                pressedContainerColor = Color.White,
                pressedContentColor = Color.Black,
            ),
    ) {
        Text(text = text, fontSize = 10.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
    }
}

/** Dwell time on a card before its trailer starts. */
const val TRAILER_DWELL_MS = 800L
