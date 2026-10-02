package com.github.damontecres.wholphin.preferences

class InterfacePreferences(
    val fullUiDisabled: Boolean = false,
)

class AppPreferences(
    val interfacePreferences: InterfacePreferences = InterfacePreferences(),
)

data class UserPreferences(
    val appPreferences: AppPreferences,
)
