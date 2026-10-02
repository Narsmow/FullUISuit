package com.github.damontecres.wholphin.data.model

import org.jellyfin.sdk.model.api.BaseItemDto
import java.util.UUID

data class JellyfinServer(
    val id: UUID,
    val name: String?,
    val url: String,
    val version: String?,
)

data class JellyfinUser(
    val rowId: Int = 0,
    val id: UUID,
    val name: String?,
)

data class BaseItem(
    val data: BaseItemDto,
    val useSeriesForPrimary: Boolean = false,
    val imageUrlOverride: String? = null,
) {
    val id get() = data.id
}
