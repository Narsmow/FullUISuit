package com.github.damontecres.wholphin.ui.components

import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.text.input.TextFieldState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.tv.material3.ClickableSurfaceColors
import androidx.tv.material3.ClickableSurfaceDefaults

// Mirrors com.github.damontecres.wholphin.ui.components.Button (parameters that matter here)
@Composable
fun Button(
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    onLongClick: (() -> Unit)? = null,
    enabled: Boolean = true,
    colors: ClickableSurfaceColors = ClickableSurfaceDefaults.colors(),
    contentPadding: PaddingValues = PaddingValues(4.dp),
    contentHeight: Dp = 40.dp,
    interactionSource: MutableInteractionSource? = null,
    content: @Composable RowScope.() -> Unit,
) {}

@Composable
fun EditTextBox(
    state: TextFieldState,
    modifier: Modifier = Modifier,
    keyboardOptions: KeyboardOptions = KeyboardOptions.Default,
    leadingIcon: @Composable (() -> Unit)? = null,
    enabled: Boolean = true,
    readOnly: Boolean = false,
    maxLines: Int = 1,
    isInputValid: (String) -> Boolean = { true },
    isPassword: Boolean = false,
    interactionSource: MutableInteractionSource = MutableInteractionSource(),
) {}

@Composable
fun LoadingPage(
    modifier: Modifier = Modifier,
    focusEnabled: Boolean = true,
) {}
