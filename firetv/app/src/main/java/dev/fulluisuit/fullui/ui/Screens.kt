package dev.fulluisuit.fullui.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.LocalBringIntoViewSpec
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.input.rememberTextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.tv.material3.Card
import androidx.tv.material3.ClickableSurfaceDefaults
import androidx.tv.material3.Text
import coil3.compose.AsyncImage
import com.github.damontecres.wholphin.ui.FontAwesome
import com.github.damontecres.wholphin.ui.components.Button
import com.github.damontecres.wholphin.ui.components.EditTextBox
import com.github.damontecres.wholphin.ui.tryRequestFocus
import com.github.damontecres.wholphin.ui.util.ScrollToTopBringIntoViewSpec
import dev.fulluisuit.fullui.ImageUrls
import dev.fulluisuit.fullui.MyServerUi
import dev.fulluisuit.fullui.NotificationDto
import dev.fulluisuit.fullui.RowKind
import dev.fulluisuit.fullui.SearchState
import dev.fulluisuit.fullui.UiCard
import dev.fulluisuit.fullui.UiRow
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest

enum class FullUiTab(
    val label: String,
) {
    HOME("Home"),
    SHOWS("Shows"),
    MOVIES("Movies"),
    MY_SERVER("My Server"),
    SEARCH("Search"),
}

// ---------------------------------------------------------------------------------------------
// Top navigation bar (replaces the drawer)
// ---------------------------------------------------------------------------------------------

@Composable
fun FullUiTopBar(
    selected: FullUiTab,
    serverName: String,
    unreadNotifications: Int,
    onSelect: (FullUiTab) -> Unit,
    onNotifications: () -> Unit,
    onSettings: () -> Unit,
    onProfile: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val accent = LocalFullUiAccent.current
    Box(
        modifier
            .fillMaxWidth()
            .height(FullUiDims.NavHeight)
            .background(Brush.verticalGradient(listOf(Color.Black.copy(alpha = 0.85f), Color.Transparent))),
    ) {
        Row(
            modifier = Modifier.fillMaxSize().padding(horizontal = FullUiDims.Gutter),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            Text(
                text = serverName.uppercase(),
                color = accent,
                fontSize = 17.sp,
                fontWeight = FontWeight.Black,
                maxLines = 1,
                modifier = Modifier.padding(end = 18.dp),
            )
            FullUiTab.entries.forEach { tab ->
                val label = if (tab == FullUiTab.MY_SERVER) "My $serverName" else tab.label
                NavTab(label = label, selected = tab == selected, onClick = { onSelect(tab) })
            }
            Spacer(Modifier.weight(1f))
            Box {
                GlyphButton(FaGlyph.Bell, "Notifications", active = false, onClick = onNotifications)
                if (unreadNotifications > 0) {
                    Box(
                        Modifier
                            .align(Alignment.TopEnd)
                            .width(8.dp)
                            .height(8.dp)
                            .background(accent, CircleShape),
                    )
                }
            }
            GlyphButton(FaGlyph.Gear, "Settings", active = false, onClick = onSettings)
            GlyphButton(FaGlyph.User, "Switch profile", active = false, onClick = onProfile)
        }
    }
}

@Composable
private fun NavTab(
    label: String,
    selected: Boolean,
    onClick: () -> Unit,
) {
    Button(
        onClick = onClick,
        contentHeight = 26.dp,
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 0.dp),
        colors =
            ClickableSurfaceDefaults.colors(
                containerColor = Color.Transparent,
                contentColor = if (selected) Color.White else FullUiColors.TextSecondary,
                focusedContainerColor = Color.White,
                focusedContentColor = Color.Black,
                pressedContainerColor = Color.White,
                pressedContentColor = Color.Black,
            ),
    ) {
        Text(
            text = label,
            fontSize = 13.sp,
            fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
            maxLines = 1,
        )
    }
}

// ---------------------------------------------------------------------------------------------
// My <Server>
// ---------------------------------------------------------------------------------------------

@Composable
fun MyServerScreen(
    serverName: String,
    data: MyServerUi?,
    images: ImageUrls,
    actions: CardActions,
    modifier: Modifier = Modifier,
) {
    if (data == null) {
        Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text("Loading...", color = FullUiColors.TextSecondary, fontSize = 14.sp)
        }
        return
    }
    val rows =
        remember(data) {
            buildList {
                if (data.continueWatching.isNotEmpty()) {
                    add(UiRow("my-continue", "Continue Watching", RowKind.CONTINUE, data.continueWatching, emptyList()))
                }
                if (data.myList.isNotEmpty()) {
                    add(UiRow("my-list", "My List", RowKind.MY_LIST, data.myList, emptyList()))
                }
                if (data.wanted.isNotEmpty()) {
                    add(UiRow("my-wanted", "I Want This", RowKind.COMING_SOON, emptyList(), data.wanted))
                }
            }
        }
    RowsColumn(
        rows = rows,
        images = images,
        actions = actions,
        modifier = modifier,
        header = {
            Text(
                text = "My $serverName",
                color = FullUiColors.TextPrimary,
                fontSize = 24.sp,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(start = FullUiDims.Gutter, bottom = 8.dp),
            )
        },
        empty = "Your list is empty. Add titles with the + button, or ask for something under Coming Soon.",
    )
}

/** A plain scrolling list of rows below the nav bar. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
fun RowsColumn(
    rows: List<UiRow>,
    images: ImageUrls,
    actions: CardActions,
    modifier: Modifier = Modifier,
    header: (@Composable () -> Unit)? = null,
    empty: String? = null,
) {
    val density = LocalDensity.current
    val spaceAbovePx = remember(density) { with(density) { 28.dp.toPx() } }
    val defaultSpec = LocalBringIntoViewSpec.current
    CompositionLocalProvider(LocalBringIntoViewSpec provides ScrollToTopBringIntoViewSpec(spaceAbovePx)) {
        LazyColumn(
            contentPadding = PaddingValues(top = FullUiDims.NavHeight + 8.dp, bottom = 120.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
            modifier = modifier.fillMaxSize(),
        ) {
            if (header != null) {
                item(key = "header") {
                    CompositionLocalProvider(LocalBringIntoViewSpec provides defaultSpec) { header() }
                }
            }
            if (rows.isEmpty() && empty != null) {
                item(key = "empty") {
                    Text(
                        text = empty,
                        color = FullUiColors.TextSecondary,
                        fontSize = 13.sp,
                        modifier = Modifier.padding(horizontal = FullUiDims.Gutter, vertical = 24.dp),
                    )
                }
            }
            items(rows, key = { it.id }) { row ->
                CompositionLocalProvider(LocalBringIntoViewSpec provides defaultSpec) {
                    ContentRow(row = row, images = images, actions = actions)
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Search
// ---------------------------------------------------------------------------------------------

@Composable
fun SearchScreen(
    state: SearchState,
    onQuery: (String) -> Unit,
    images: ImageUrls,
    onOpen: (UiCard) -> Unit,
    modifier: Modifier = Modifier,
) {
    val textState = rememberTextFieldState()
    val inputFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        delay(150)
        inputFocus.tryRequestFocus()
        snapshotFlow { textState.text.toString() }.collectLatest { onQuery(it) }
    }
    Column(
        modifier = modifier.fillMaxSize().padding(top = FullUiDims.NavHeight + 8.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        EditTextBox(
            state = textState,
            modifier =
                Modifier
                    .padding(horizontal = FullUiDims.Gutter)
                    .fillMaxWidth(0.6f)
                    .focusRequester(inputFocus),
        )
        when (state) {
            SearchState.Idle -> {
                Hint("Search your library by title, actor, or describe what you feel like watching.")
            }

            SearchState.Searching -> {
                Hint("Searching...")
            }

            is SearchState.Error -> {
                Hint(state.message)
            }

            is SearchState.Results -> {
                if (state.items.isEmpty()) {
                    Hint("No results.")
                } else {
                    if (state.mode == "semantic") {
                        Text(
                            text = "Smart search results",
                            color = FullUiColors.TextSecondary,
                            fontSize = 11.sp,
                            modifier = Modifier.padding(horizontal = FullUiDims.Gutter),
                        )
                    }
                    LazyVerticalGrid(
                        columns = GridCells.Adaptive(minSize = 150.dp),
                        contentPadding = PaddingValues(horizontal = FullUiDims.Gutter, vertical = 8.dp),
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                        verticalArrangement = Arrangement.spacedBy(12.dp),
                        modifier = Modifier.fillMaxSize(),
                    ) {
                        items(state.items, key = { it.id }) { card ->
                            SearchTile(card = card, images = images, onClick = { onOpen(card) })
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Hint(text: String) {
    Text(
        text = text,
        color = FullUiColors.TextSecondary,
        fontSize = 12.sp,
        modifier = Modifier.padding(horizontal = FullUiDims.Gutter, vertical = 8.dp),
    )
}

@Composable
private fun SearchTile(
    card: UiCard,
    images: ImageUrls,
    onClick: () -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Card(
            onClick = onClick,
            modifier = Modifier.fillMaxWidth().aspectRatio(16f / 9f),
        ) {
            val url = images.landscape(card, 480)
            if (url != null) {
                AsyncImage(
                    model = url,
                    contentDescription = card.name,
                    contentScale = ContentScale.Crop,
                    modifier = Modifier.fillMaxSize(),
                )
            } else {
                Box(Modifier.fillMaxSize().background(FullUiColors.Surface), contentAlignment = Alignment.Center) {
                    Text(card.name, color = FullUiColors.TextSecondary, fontSize = 11.sp, maxLines = 3)
                }
            }
        }
        Text(
            text = card.name,
            color = FullUiColors.TextPrimary,
            fontSize = 11.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

// ---------------------------------------------------------------------------------------------
// Notifications (the in-UI bell)
// ---------------------------------------------------------------------------------------------

@Composable
fun NotificationsDialog(
    items: List<NotificationDto>,
    onDismiss: () -> Unit,
    onMarkAllRead: () -> Unit,
) {
    val closeFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        delay(100)
        closeFocus.tryRequestFocus()
    }
    Dialog(onDismissRequest = onDismiss) {
        Column(
            modifier =
                Modifier
                    .width(420.dp)
                    .background(FullUiColors.Surface, RoundedCornerShape(10.dp))
                    .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Text("Notifications", color = FullUiColors.TextPrimary, fontSize = 16.sp, fontWeight = FontWeight.Bold)
            if (items.isEmpty()) {
                Text("You're all caught up.", color = FullUiColors.TextSecondary, fontSize = 12.sp)
            } else {
                items.take(8).forEach {
                    Text(
                        text = it.text,
                        color = if (it.read) FullUiColors.TextSecondary else FullUiColors.TextPrimary,
                        fontSize = 12.sp,
                        fontWeight = if (it.read) FontWeight.Normal else FontWeight.SemiBold,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextActionButton(text = "Close", active = false, onClick = onDismiss, modifier = Modifier.focusRequester(closeFocus))
                if (items.any { !it.read }) {
                    TextActionButton(text = "Mark all read", active = false, onClick = onMarkAllRead)
                }
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Error panel
// ---------------------------------------------------------------------------------------------

@Composable
fun HomeErrorPanel(
    message: String,
    onRetry: () -> Unit,
    onUseStock: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val retryFocus = remember { FocusRequester() }
    LaunchedEffect(Unit) {
        delay(100)
        retryFocus.tryRequestFocus()
    }
    Column(
        modifier = modifier.fillMaxSize(),
        verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(message, color = FullUiColors.TextPrimary, fontSize = 15.sp)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            TextActionButton("Try again", active = false, onClick = onRetry, modifier = Modifier.focusRequester(retryFocus))
            TextActionButton("Use classic interface", active = false, onClick = onUseStock)
        }
    }
}

/** Font Awesome glyph text, for places that need a bare icon. */
@Composable
fun FaIcon(
    glyph: String,
    color: Color = Color.White,
    size: Int = 12,
) {
    Text(text = glyph, fontFamily = FontAwesome, fontSize = size.sp, color = color)
}
