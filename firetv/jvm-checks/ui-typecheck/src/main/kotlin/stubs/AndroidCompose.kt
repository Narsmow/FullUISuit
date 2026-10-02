package androidx.compose.ui.platform

import android.content.Context
import androidx.compose.runtime.ProvidableCompositionLocal
import androidx.compose.runtime.compositionLocalOf

val LocalContext: ProvidableCompositionLocal<Context> = compositionLocalOf { error("no context") }
