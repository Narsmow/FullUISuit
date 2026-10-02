package com.github.damontecres.wholphin.ui

import android.content.Context
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MutableIntState
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.text.font.FontFamily
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

val FontAwesome = FontFamily.Default

fun FocusRequester.tryRequestFocus(tag: String? = null): Boolean = true

@Composable
fun rememberInt(initial: Int = 0): MutableIntState = rememberSaveable { mutableIntStateOf(initial) }

fun CoroutineScope.launchIO(
    context: CoroutineContext = EmptyCoroutineContext,
    start: CoroutineStart = CoroutineStart.DEFAULT,
    block: suspend CoroutineScope.() -> Unit,
): Job = launch(context = context, start = start, block = block)

fun CoroutineScope.launchDefault(
    context: CoroutineContext = EmptyCoroutineContext,
    start: CoroutineStart = CoroutineStart.DEFAULT,
    block: suspend CoroutineScope.() -> Unit,
): Job = launch(context = context, start = start, block = block)

suspend fun showToast(
    context: Context,
    text: CharSequence,
    duration: Int,
) {}

suspend fun showToast(
    context: Context,
    text: CharSequence,
) {}
