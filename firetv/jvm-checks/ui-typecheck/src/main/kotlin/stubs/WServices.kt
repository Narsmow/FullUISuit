package com.github.damontecres.wholphin.services

import com.github.damontecres.wholphin.data.CurrentUser
import com.github.damontecres.wholphin.data.model.JellyfinServer
import com.github.damontecres.wholphin.ui.main.settings.Library
import com.github.damontecres.wholphin.ui.nav.Destination
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class NavigationManager
    @Inject
    constructor() {
        var backStack: MutableList<Destination> = mutableListOf()

        fun navigateTo(destination: Destination) {}
    }

@Singleton
class SetupNavigationManager
    @Inject
    constructor() {
        fun navigateTo(destination: SetupDestination) {}
    }

sealed interface SetupDestination {
    data class UserList(
        val server: JellyfinServer,
    ) : SetupDestination

    data class AppContent(
        val current: CurrentUser,
    ) : SetupDestination
}

@Singleton
class MusicService
    @Inject
    constructor() {
        suspend fun stop() {}
    }

data class NavDrawerItemState(
    val allLibraries: List<Library> = emptyList(),
)

@Singleton
class NavDrawerService
    @Inject
    constructor() {
        val state: StateFlow<NavDrawerItemState> = MutableStateFlow(NavDrawerItemState())
    }
