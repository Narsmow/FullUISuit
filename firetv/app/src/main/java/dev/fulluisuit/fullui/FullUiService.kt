package dev.fulluisuit.fullui

import com.github.damontecres.wholphin.data.ServerRepository
import com.github.damontecres.wholphin.services.hilt.AuthOkHttpClient
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import okhttp3.OkHttpClient
import timber.log.Timber
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Session-wide access to the FullUI server plugin.
 *
 * - Owns the [FullUiClient] (uses Wholphin's authenticated OkHttp client and current server URL)
 * - Owns the [FeatureDetector]: the plugin is probed once per server+user per session and the
 *   answer is cached. If the plugin is absent (404), refuses us (401/403) or answers with
 *   garbage, the app keeps using the stock Wholphin UI.
 */
@Singleton
class FullUiService
    @Inject
    constructor(
        private val serverRepository: ServerRepository,
        @param:AuthOkHttpClient private val okHttpClient: OkHttpClient,
    ) {
        private fun baseUrl(): String? = serverRepository.currentServer?.url

        val client: FullUiClient = FullUiClient(okHttpClient, baseUrl = { baseUrl() })
        val images: ImageUrls = ImageUrls { baseUrl() }

        private val detector = FeatureDetector()

        private val _revision = MutableStateFlow(0)

        /** Bumped whenever the cached [Availability] may have changed. */
        val revision: StateFlow<Int> = _revision

        private fun currentKey(): String? {
            val cur = serverRepository.current.value ?: return null
            return FeatureDetector.key(cur.server.id.toString(), cur.user.id.toString())
        }

        /** Cached answer for the current server+user, or null while unknown. Never does I/O. */
        fun availability(): Availability? = currentKey()?.let { detector.peek(it) }

        /** Probe (once per session) whether the plugin is usable. */
        suspend fun ensureDetected(): Availability? {
            val key = currentKey() ?: return null
            val result = detector.detect(key) { client.home(DETECT_TIMEOUT_MS) }
            Timber.i("FullUI availability for current user: %s", result.describe())
            _revision.update { it + 1 }
            return result
        }

        /** The Home payload fetched during detection, handed out once. */
        fun takePrefetchedHome(): HomeResponse? = currentKey()?.let { detector.takePrefetchedHome(it) }

        /** Call with any failure of a real FullUI call; permanent failures switch to the stock UI. */
        fun reportFailure(error: Throwable) {
            if (error !is FullUiException) return
            val key = currentKey() ?: return
            if (detector.markUnavailable(key, error)) {
                Timber.w("FullUI disabled for this session: %s", error.message)
                _revision.update { it + 1 }
            }
        }

        /** Forget the cached answer and probe again (e.g. after the admin installed the plugin). */
        fun retry() {
            detector.reset(currentKey())
            _revision.update { it + 1 }
        }

        private fun Availability.describe(): String =
            when (this) {
                is Availability.Available -> "available"
                is Availability.Unavailable -> "unavailable($reason, permanent=$permanent)"
                Availability.DisabledByUser -> "disabled by user"
            }

        companion object {
            /** Detection must not hold up the app for long; fall back to stock after this. */
            const val DETECT_TIMEOUT_MS = 8_000L
        }
    }
