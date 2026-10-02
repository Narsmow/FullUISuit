package com.github.damontecres.wholphin.data

import com.github.damontecres.wholphin.data.model.JellyfinServer
import com.github.damontecres.wholphin.data.model.JellyfinUser
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import javax.inject.Inject
import javax.inject.Singleton

data class CurrentUser(
    val server: JellyfinServer,
    val user: JellyfinUser,
)

@Singleton
class ServerRepository
    @Inject
    constructor() {
        private var _current = MutableStateFlow<CurrentUser?>(null)
        val current: StateFlow<CurrentUser?> = _current
        val currentServer: JellyfinServer? get() = _current.value?.server
    }
