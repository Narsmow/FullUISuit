package dev.fulluisuit.fullui.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.LifecycleStartEffect
import com.github.damontecres.wholphin.data.model.JellyfinServer
import com.github.damontecres.wholphin.data.model.JellyfinUser
import com.github.damontecres.wholphin.preferences.UserPreferences
import com.github.damontecres.wholphin.ui.components.LoadingPage
import com.github.damontecres.wholphin.ui.nav.Destination
import com.github.damontecres.wholphin.ui.nav.DestinationContent
import dagger.hilt.android.lifecycle.HiltViewModel
import dev.fulluisuit.fullui.Availability
import dev.fulluisuit.fullui.FailureReason
import dev.fulluisuit.fullui.FullUiException
import dev.fulluisuit.fullui.FullUiService
import dev.fulluisuit.fullui.FullUiViewModel
import dev.fulluisuit.fullui.HomeLoad
import dev.fulluisuit.fullui.HomeUi
import dev.fulluisuit.fullui.filterByType
import javax.inject.Inject

/** Tiny holder so the host can reach the singleton [FullUiService] through Hilt. */
@HiltViewModel
class FullUiHostViewModel
    @Inject
    constructor(
        val service: FullUiService,
    ) : ViewModel()

/**
 * Decides, for every non-fullscreen page, between FullUI's top-nav shell and Wholphin's stock
 * navigation drawer ([stock]).
 *
 * FullUI is used when: the user has not switched it off in Settings AND the FullUI server plugin
 * answered a one-time probe (cached per session). Everything else (plugin missing, 401/403/404,
 * timeouts, garbage) shows [stock], i.e. exactly the original Wholphin UI.
 */
@Composable
fun FullUiHost(
    destination: Destination,
    preferences: UserPreferences,
    server: JellyfinServer,
    user: JellyfinUser,
    onClearBackdrop: () -> Unit,
    modifier: Modifier = Modifier,
    host: FullUiHostViewModel = hiltViewModel(),
    stock: @Composable () -> Unit,
) {
    val enabled = !preferences.appPreferences.interfacePreferences.fullUiDisabled
    val service = host.service
    val revision by service.revision.collectAsState()
    val availability = remember(revision, server.id, user.id, enabled) { service.availability() }

    LaunchedEffect(enabled, server.id, user.id) {
        if (enabled && service.availability() == null) service.ensureDetected()
    }

    when {
        !enabled -> {
            stock()
        }

        availability == null -> {
            // Probing (at most a few seconds)
            LoadingPage(modifier.fillMaxSize())
        }

        availability is Availability.Available -> {
            FullUiShell(
                destination = destination,
                preferences = preferences,
                server = server,
                user = user,
                onClearBackdrop = onClearBackdrop,
                modifier = modifier,
            )
        }

        else -> {
            stock()
        }
    }
}

/**
 * The FullUI experience. The Home destination gets the top-nav pages; every other destination
 * (details, library grids, settings, ...) is rendered by Wholphin's own [DestinationContent],
 * so playback, auth, profiles and settings keep working unchanged.
 */
@Composable
fun FullUiShell(
    destination: Destination,
    preferences: UserPreferences,
    server: JellyfinServer,
    user: JellyfinUser,
    onClearBackdrop: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (destination is Destination.Home) {
        FullUiHomeShell(server = server, onClearBackdrop = onClearBackdrop, modifier = modifier)
    } else {
        DestinationContent(
            destination = destination,
            preferences = preferences,
            onClearBackdrop = onClearBackdrop,
            modifier = modifier.fillMaxSize().padding(start = 32.dp, end = 16.dp),
        )
    }
}

@Composable
private fun FullUiHomeShell(
    server: JellyfinServer,
    onClearBackdrop: () -> Unit,
    modifier: Modifier = Modifier,
    viewModel: FullUiViewModel = hiltViewModel(),
) {
    LaunchedEffect(Unit) { onClearBackdrop() }
    LifecycleStartEffect(Unit) {
        viewModel.refreshIfStale()
        onStopOrDispose { }
    }

    val config = rememberFullUiConfig()
    val homeLoad by viewModel.home.collectAsState()
    val myServer by viewModel.myServer.collectAsState()
    val notifications by viewModel.notifications.collectAsState()
    val searchState by viewModel.search.collectAsState()

    var tabIndex by rememberSaveable { mutableIntStateOf(0) }
    val tab = FullUiTab.entries[tabIndex.coerceIn(0, FullUiTab.entries.lastIndex)]
    var showNotifications by remember { mutableStateOf(false) }
    BackHandler(enabled = tab != FullUiTab.HOME) { tabIndex = 0 }

    val ready = (homeLoad as? HomeLoad.Ready)?.ui
    val serverName = ready?.serverName ?: server.name ?: "Server"
    val accent = ready?.accentArgb?.let { Color(it) } ?: FullUiColors.DefaultAccent
    val images = viewModel.service.images

    val actions =
        remember(viewModel) {
            CardActions(
                onPlay = viewModel::play,
                onInfo = viewModel::info,
                onToggleList = viewModel::toggleMyList,
                onRate = viewModel::rate,
                onVote = viewModel::vote,
            )
        }

    CompositionLocalProvider(LocalFullUiConfig provides config, LocalFullUiAccent provides accent) {
        Box(modifier.fillMaxSize().background(FullUiColors.Background)) {
            when (tab) {
                FullUiTab.HOME, FullUiTab.SHOWS, FullUiTab.MOVIES -> {
                    when (val load = homeLoad) {
                        HomeLoad.Loading -> {
                            LoadingPage(Modifier.fillMaxSize(), focusEnabled = false)
                        }

                        is HomeLoad.Failed -> {
                            HomeErrorPanel(
                                message = load.message,
                                onRetry = { viewModel.loadHome(showSpinner = true) },
                                onUseStock = {
                                    viewModel.service.reportFailure(FullUiException(FailureReason.NOT_FOUND))
                                },
                            )
                        }

                        is HomeLoad.Ready -> {
                            key(tab) {
                                val shown: HomeUi =
                                    remember(load.ui, tab) {
                                        when (tab) {
                                            FullUiTab.SHOWS -> load.ui.filterByType(series = true)
                                            FullUiTab.MOVIES -> load.ui.filterByType(series = false)
                                            else -> load.ui
                                        }
                                    }
                                val isSeries = tab == FullUiTab.SHOWS
                                HomeContent(
                                    ui = shown,
                                    images = images,
                                    actions = actions,
                                    overlay =
                                        if (tab != FullUiTab.HOME && viewModel.hasLibrary(isSeries)) {
                                            {
                                                TextActionButton(
                                                    text = if (isSeries) "Browse all shows" else "Browse all movies",
                                                    active = false,
                                                    onClick = { viewModel.browseLibrary(isSeries) },
                                                )
                                            }
                                        } else {
                                            null
                                        },
                                )
                            }
                        }
                    }
                }

                FullUiTab.MY_SERVER -> {
                    LaunchedEffect(Unit) { viewModel.loadMyServer() }
                    MyServerScreen(
                        serverName = serverName,
                        data = myServer,
                        images = images,
                        actions = actions,
                    )
                }

                FullUiTab.SEARCH -> {
                    SearchScreen(
                        state = searchState,
                        onQuery = viewModel::search,
                        images = images,
                        onOpen = viewModel::info,
                    )
                }
            }

            FullUiTopBar(
                selected = tab,
                serverName = serverName,
                unreadNotifications = notifications.count { !it.read },
                onSelect = { tabIndex = it.ordinal },
                onNotifications = {
                    viewModel.loadNotifications()
                    showNotifications = true
                },
                onSettings = viewModel::openSettings,
                onProfile = { viewModel.switchUser(server) },
                modifier = Modifier.align(Alignment.TopStart),
            )

            if (showNotifications) {
                NotificationsDialog(
                    items = notifications,
                    onDismiss = { showNotifications = false },
                    onMarkAllRead = viewModel::markNotificationsRead,
                )
            }
        }
    }
}
