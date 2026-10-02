package androidx.lifecycle.compose

import androidx.compose.runtime.Composable

class LifecycleStartStopEffectScope {
    fun onStopOrDispose(onStopOrDisposeEffect: () -> Unit) {}
}

@Composable
fun LifecycleStartEffect(
    key1: Any?,
    effects: LifecycleStartStopEffectScope.() -> Unit,
) {}
