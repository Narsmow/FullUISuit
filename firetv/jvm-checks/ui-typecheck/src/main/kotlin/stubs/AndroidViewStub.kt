package androidx.compose.ui.viewinterop

import android.content.Context
import android.view.View
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier

@Composable
fun <T : View> AndroidView(
    factory: (Context) -> T,
    modifier: Modifier = Modifier,
    onReset: ((T) -> Unit)? = null,
    onRelease: (T) -> Unit = {},
    update: (T) -> Unit = {},
) {}
