package dev.fulluisuit.fullui

import android.content.Context
import android.widget.Toast
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.github.damontecres.wholphin.data.model.BaseItem
import com.github.damontecres.wholphin.data.model.JellyfinServer
import com.github.damontecres.wholphin.services.MusicService
import com.github.damontecres.wholphin.services.NavDrawerService
import com.github.damontecres.wholphin.services.NavigationManager
import com.github.damontecres.wholphin.services.SetupDestination
import com.github.damontecres.wholphin.services.SetupNavigationManager
import com.github.damontecres.wholphin.ui.launchDefault
import com.github.damontecres.wholphin.ui.launchIO
import com.github.damontecres.wholphin.ui.nav.Destination
import com.github.damontecres.wholphin.ui.preferences.PreferenceScreenOption
import com.github.damontecres.wholphin.ui.showToast
import com.github.damontecres.wholphin.util.WholphinDispatchers
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.withContext
import org.jellyfin.sdk.api.client.ApiClient
import org.jellyfin.sdk.api.client.extensions.tvShowsApi
import org.jellyfin.sdk.api.client.extensions.userLibraryApi
import org.jellyfin.sdk.model.api.BaseItemKind
import org.jellyfin.sdk.model.api.CollectionType
import timber.log.Timber
import javax.inject.Inject
import javax.inject.Singleton

/** Loading state of the Home payload. */
sealed interface HomeLoad {
    data object Loading : HomeLoad

    data class Ready(
        val ui: HomeUi,
    ) : HomeLoad

    /** Transient failure (timeout, offline, 5xx). Permanent ones switch the app to the stock UI instead. */
    data class Failed(
        val message: String,
    ) : HomeLoad
}

/** Turns a card into a Wholphin [Destination] (playback / details). Uses the stock playback stack. */
@Singleton
class FullUiPlayResolver
    @Inject
    constructor(
        private val api: ApiClient,
    ) {
        /**
         * Movies play (resuming where the user left off); series play the next-up episode,
         * or the first episode.
         */
        suspend fun playbackFor(card: UiCard): Destination? {
            val id = Ids.parse(card.id) ?: return null
            return if (card.isSeries) {
                val nextUp =
                    api.tvShowsApi
                        .getNextUp(seriesId = id)
                        .content.items
                        .firstOrNull()
                        ?: api.tvShowsApi
                            .getEpisodes(id, limit = 1)
                            .content.items
                            .firstOrNull()
                nextUp?.let { Destination.Playback(BaseItem(it)) }
            } else {
                val dto = api.userLibraryApi.getItem(id).content
                Destination.Playback(BaseItem(dto))
            }
        }

        fun infoFor(card: UiCard): Destination? =
            Ids.parse(card.id)?.let {
                Destination.MediaItem(it, if (card.isSeries) BaseItemKind.SERIES else BaseItemKind.MOVIE)
            }
    }

@HiltViewModel
class FullUiViewModel
    @Inject
    constructor(
        @param:ApplicationContext private val context: Context,
        val service: FullUiService,
        val navigationManager: NavigationManager,
        private val setupNavigationManager: SetupNavigationManager,
        private val musicService: MusicService,
        private val navDrawerService: NavDrawerService,
        private val playResolver: FullUiPlayResolver,
    ) : ViewModel() {
        private val _home = MutableStateFlow<HomeLoad>(HomeLoad.Loading)
        val home: StateFlow<HomeLoad> = _home

        private val _myServer = MutableStateFlow<MyServerUi?>(null)
        val myServer: StateFlow<MyServerUi?> = _myServer

        private val _notifications = MutableStateFlow<List<NotificationDto>>(emptyList())
        val notifications: StateFlow<List<NotificationDto>> = _notifications

        private val _search = MutableStateFlow<SearchState>(SearchState.Idle)
        val search: StateFlow<SearchState> = _search

        private var loadJob: Job? = null
        private var searchJob: Job? = null
        private var lastLoadedAt = 0L

        init {
            loadHome(showSpinner = true)
            loadNotifications()
        }

        // ---- loading -------------------------------------------------------------------------

        fun loadHome(showSpinner: Boolean) {
            loadJob?.cancel()
            loadJob =
                viewModelScope.launchIO {
                    if (showSpinner) _home.value = HomeLoad.Loading
                    try {
                        val response = service.takePrefetchedHome() ?: service.client.home()
                        _home.value = HomeLoad.Ready(Mapping.home(response))
                        lastLoadedAt = System.currentTimeMillis()
                    } catch (ex: CancellationException) {
                        throw ex
                    } catch (ex: Exception) {
                        Timber.w(ex, "FullUI home failed")
                        // Plugin vanished / token refused: the shell flips to the stock UI by itself.
                        service.reportFailure(ex)
                        if (_home.value !is HomeLoad.Ready) {
                            _home.value = HomeLoad.Failed(ex.userMessage())
                        }
                    }
                }
        }

        /** Refresh in the background when returning to the page (e.g. after playback). */
        fun refreshIfStale() {
            if (_home.value is HomeLoad.Ready && System.currentTimeMillis() - lastLoadedAt > STALE_MS) {
                loadHome(showSpinner = false)
            }
        }

        fun loadMyServer() {
            viewModelScope.launchIO {
                try {
                    _myServer.value = Mapping.myServer(service.client.myServer())
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    Timber.w(ex, "FullUI MyServer failed")
                    service.reportFailure(ex)
                    if (_myServer.value == null) _myServer.value = Mapping.myServer(MyServerResponse())
                }
            }
        }

        fun loadNotifications() {
            viewModelScope.launchIO {
                try {
                    _notifications.value = service.client.notifications().items
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    Timber.d(ex, "FullUI notifications unavailable")
                }
            }
        }

        fun markNotificationsRead() {
            _notifications.update { list -> list.map { it.copy(read = true) } }
            viewModelScope.launchIO {
                try {
                    service.client.markNotificationsRead(null)
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    Timber.d(ex, "Could not mark notifications read")
                }
            }
        }

        fun search(query: String) {
            searchJob?.cancel()
            val q = query.trim()
            if (q.length < 2) {
                _search.value = SearchState.Idle
                return
            }
            searchJob =
                viewModelScope.launchIO {
                    delay(SEARCH_DEBOUNCE_MS)
                    _search.value = SearchState.Searching
                    try {
                        val res = service.client.search(q)
                        _search.value = SearchState.Results(res.mode, res.items.map { Mapping.card(it) })
                    } catch (ex: CancellationException) {
                        throw ex
                    } catch (ex: Exception) {
                        Timber.w(ex, "FullUI search failed")
                        service.reportFailure(ex)
                        _search.value = SearchState.Error(ex.userMessage())
                    }
                }
        }

        // ---- card actions --------------------------------------------------------------------

        fun play(card: UiCard) {
            viewModelScope.launchIO {
                try {
                    val dest = playResolver.playbackFor(card)
                    if (dest != null) {
                        withContext(WholphinDispatchers.Main) { navigationManager.navigateTo(dest) }
                    } else {
                        showToast(context, "Could not find anything to play", Toast.LENGTH_SHORT)
                    }
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    Timber.w(ex, "Could not start playback for %s", card.id)
                    showToast(context, "Could not start playback", Toast.LENGTH_SHORT)
                }
            }
        }

        fun info(card: UiCard) {
            playResolver.infoFor(card)?.let { navigationManager.navigateTo(it) }
        }

        fun toggleMyList(card: UiCard) {
            val add = !card.inMyList
            patchCard(card.id) { it.copy(inMyList = add) }
            viewModelScope.launchIO {
                try {
                    service.client.setMyList(card.id, add)
                    // The My List row changes server-side; pick that up quietly.
                    delay(500)
                    loadHome(showSpinner = false)
                    loadMyServerIfLoaded()
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    patchCard(card.id) { it.copy(inMyList = !add) }
                    onActionFailed(ex)
                }
            }
        }

        fun rate(
            card: UiCard,
            pressed: Int,
        ) {
            val previous = card.myRating
            val next = Mapping.nextRating(previous, pressed)
            patchCard(card.id) { it.copy(myRating = next) }
            viewModelScope.launchIO {
                try {
                    service.client.rate(card.id, next)
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    patchCard(card.id) { it.copy(myRating = previous) }
                    onActionFailed(ex)
                }
            }
        }

        fun vote(
            card: UiComingSoon,
            pressed: Int,
        ) {
            val previous = card.myVote
            val next = Mapping.nextVote(previous, pressed)
            patchVote(card.tmdbId, card.mediaType, next)
            viewModelScope.launchIO {
                try {
                    service.client.vote(
                        VoteRequest(
                            tmdbId = card.tmdbId,
                            mediaType = card.mediaType,
                            vote = next,
                            title = card.title,
                            posterPath = card.posterPath,
                            backdropPath = card.backdropPath,
                            releaseDate = card.rawReleaseDate,
                            overview = card.overview,
                        ),
                    )
                    loadMyServerIfLoaded()
                } catch (ex: CancellationException) {
                    throw ex
                } catch (ex: Exception) {
                    patchVote(card.tmdbId, card.mediaType, previous)
                    onActionFailed(ex)
                }
            }
        }

        private fun loadMyServerIfLoaded() {
            if (_myServer.value != null) loadMyServer()
        }

        private fun patchCard(
            id: String,
            transform: (UiCard) -> UiCard,
        ) {
            _home.update { s -> if (s is HomeLoad.Ready) HomeLoad.Ready(s.ui.updateCard(id, transform)) else s }
            _myServer.update { m -> m?.updateCard(id, transform) }
            _search.update { s ->
                if (s is SearchState.Results) s.copy(items = s.items.map { if (it.id == id) transform(it) else it }) else s
            }
        }

        private fun patchVote(
            tmdbId: Int,
            mediaType: String,
            vote: Int,
        ) {
            _home.update { s -> if (s is HomeLoad.Ready) HomeLoad.Ready(s.ui.updateVote(tmdbId, mediaType, vote)) else s }
            _myServer.update { m -> m?.updateVote(tmdbId, mediaType, vote) }
        }

        private suspend fun onActionFailed(ex: Exception) {
            Timber.w(ex, "FullUI action failed")
            service.reportFailure(ex)
            showToast(context, "That didn't go through. Check the connection and try again.", Toast.LENGTH_SHORT)
        }

        // ---- navigation helpers --------------------------------------------------------------

        fun openSettings() {
            navigationManager.navigateTo(Destination.Settings(PreferenceScreenOption.BASIC))
        }

        fun switchUser(server: JellyfinServer) {
            viewModelScope.launchDefault {
                musicService.stop()
                setupNavigationManager.navigateTo(SetupDestination.UserList(server))
            }
        }

        /** The stock library grid for all shows / movies, if the user has such a library. */
        fun browseLibrary(series: Boolean) {
            val wanted = if (series) CollectionType.TVSHOWS else CollectionType.MOVIES
            val lib = navDrawerService.state.value.allLibraries.firstOrNull { it.collectionType == wanted }
            if (lib != null) {
                navigationManager.navigateTo(Destination.MediaItem(lib.itemId, lib.type, lib.collectionType))
            }
        }

        fun hasLibrary(series: Boolean): Boolean {
            val wanted = if (series) CollectionType.TVSHOWS else CollectionType.MOVIES
            return navDrawerService.state.value.allLibraries.any { it.collectionType == wanted }
        }

        private fun Exception.userMessage(): String =
            when ((this as? FullUiException)?.reason) {
                FailureReason.TIMEOUT -> "The server took too long to answer."
                FailureReason.NETWORK -> "Can't reach the server."
                FailureReason.SERVER_ERROR -> "The server had a problem. Try again in a moment."
                else -> "Something went wrong loading your home."
            }

        companion object {
            private const val STALE_MS = 60_000L
            private const val SEARCH_DEBOUNCE_MS = 400L
        }
    }

sealed interface SearchState {
    data object Idle : SearchState

    data object Searching : SearchState

    data class Results(
        val mode: String,
        val items: List<UiCard>,
    ) : SearchState

    data class Error(
        val message: String,
    ) : SearchState
}
