package com.github.damontecres.wholphin.ui.nav

import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import com.github.damontecres.wholphin.data.model.BaseItem
import com.github.damontecres.wholphin.preferences.UserPreferences
import com.github.damontecres.wholphin.ui.preferences.PreferenceScreenOption
import org.jellyfin.sdk.model.api.BaseItemKind
import org.jellyfin.sdk.model.api.CollectionType
import java.util.UUID

sealed class Destination(
    val fullScreen: Boolean = false,
) {
    data class Home(
        val id: Long = 0L,
    ) : Destination()

    data class Settings(
        val screen: PreferenceScreenOption,
    ) : Destination(true)

    data class MediaItem(
        val itemId: UUID,
        val type: BaseItemKind,
        val collectionType: CollectionType? = null,
        val initialSongId: UUID? = null,
    ) : Destination()

    data class Playback(
        val itemId: UUID,
        val positionMs: Long,
        val forceTranscoding: Boolean = false,
    ) : Destination(true) {
        constructor(item: BaseItem) : this(item.id, 0L)
    }
}

@Composable
fun DestinationContent(
    destination: Destination,
    preferences: UserPreferences,
    onClearBackdrop: () -> Unit,
    modifier: Modifier = Modifier,
) {}
