package dev.fulluisuit.fullui.ui

import android.annotation.SuppressLint
import android.os.Handler
import android.os.Looper
import android.webkit.JavascriptInterface
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import dev.fulluisuit.fullui.TrailerHtml
import kotlinx.coroutines.delay
import timber.log.Timber

/** Events sent from the YouTube IFrame page to Kotlin. */
class TrailerBridge(
    private val onEvent: (event: String, code: Int) -> Unit,
) {
    private val main = Handler(Looper.getMainLooper())

    @JavascriptInterface
    fun onPlaying() {
        main.post { onEvent("playing", 0) }
    }

    @JavascriptInterface
    fun onEnded() {
        main.post { onEvent("ended", 0) }
    }

    @JavascriptInterface
    fun onError(code: Int) {
        main.post { onEvent("error", code) }
    }
}

/**
 * Muted-autoplay YouTube trailer in a WebView (YouTube IFrame Player API).
 *
 * The caller should keep showing its backdrop image until [onPlaying] fires, and fall back to
 * the backdrop (with a slow zoom) when [onFailed] fires. [onFailed] is called when the embed
 * reports an error, the page fails to load (offline, blocked, no WebView), or playback has not
 * started after [startTimeoutMs].
 *
 * The WebView is never focusable, so D-pad navigation is unaffected.
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun YouTubeTrailer(
    videoKey: String,
    muted: Boolean,
    onPlaying: () -> Unit,
    onEnded: () -> Unit,
    onFailed: () -> Unit,
    modifier: Modifier = Modifier,
    startTimeoutMs: Long = 12_000,
) {
    val currentOnPlaying by rememberUpdatedState(onPlaying)
    val currentOnEnded by rememberUpdatedState(onEnded)
    val currentOnFailed by rememberUpdatedState(onFailed)
    var started by remember(videoKey) { mutableStateOf(false) }
    val page = remember(videoKey) { TrailerHtml.page(videoKey, muted = true) }

    if (page == null) {
        LaunchedEffect(videoKey) { currentOnFailed() }
        return
    }

    LaunchedEffect(videoKey) {
        delay(startTimeoutMs)
        if (!started) {
            Timber.w("Trailer %s did not start in %d ms", videoKey, startTimeoutMs)
            currentOnFailed()
        }
    }

    AndroidView(
        modifier = modifier,
        factory = { context ->
            try {
                WebView(context).apply {
                    setBackgroundColor(android.graphics.Color.BLACK)
                    isFocusable = false
                    isFocusableInTouchMode = false
                    isClickable = false
                    isLongClickable = false
                    isVerticalScrollBarEnabled = false
                    isHorizontalScrollBarEnabled = false
                    settings.javaScriptEnabled = true
                    settings.domStorageEnabled = true
                    settings.mediaPlaybackRequiresUserGesture = false
                    settings.setSupportZoom(false)
                    webChromeClient = WebChromeClient()
                    webViewClient =
                        object : WebViewClient() {
                            override fun onReceivedError(
                                view: WebView?,
                                request: WebResourceRequest?,
                                error: WebResourceError?,
                            ) {
                                if (request?.isForMainFrame == true) {
                                    Timber.w("Trailer page failed: %s", error?.description)
                                    view?.post { currentOnFailed() }
                                }
                            }
                        }
                    addJavascriptInterface(
                        TrailerBridge { event, code ->
                            when (event) {
                                "playing" -> {
                                    started = true
                                    currentOnPlaying()
                                }

                                "ended" -> {
                                    currentOnEnded()
                                }

                                else -> {
                                    Timber.w("Trailer error code=%d", code)
                                    currentOnFailed()
                                }
                            }
                        },
                        "FullUiBridge",
                    )
                    loadDataWithBaseURL(TrailerHtml.BASE_URL, page, "text/html", "utf-8", null)
                }
            } catch (ex: Exception) {
                // e.g. no WebView implementation installed on this device
                Timber.e(ex, "Could not create WebView for trailer")
                Handler(Looper.getMainLooper()).post { currentOnFailed() }
                android.view.View(context)
            }
        },
        update = { view ->
            (view as? WebView)?.evaluateJavascript("if(window.setMuted){setMuted($muted);}", null)
        },
        onRelease = { view ->
            (view as? WebView)?.let { wv ->
                wv.stopLoading()
                wv.removeJavascriptInterface("FullUiBridge")
                wv.loadUrl("about:blank")
                wv.destroy()
            }
        },
    )
}
