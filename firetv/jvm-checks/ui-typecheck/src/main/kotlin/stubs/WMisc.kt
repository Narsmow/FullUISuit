package com.github.damontecres.wholphin.ui.main.settings

import org.jellyfin.sdk.model.api.BaseItemKind
import org.jellyfin.sdk.model.api.CollectionType
import java.util.UUID

data class Library(
    val itemId: UUID,
    val name: String,
    val type: BaseItemKind,
    val collectionType: CollectionType,
    val isRecordingFolder: Boolean,
)
