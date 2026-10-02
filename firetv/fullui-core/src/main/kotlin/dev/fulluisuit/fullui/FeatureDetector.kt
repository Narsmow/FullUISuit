package dev.fulluisuit.fullui

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.CancellationException

/** Outcome of probing a server for the FullUI plugin. */
sealed interface Availability {
    /** The plugin answered with a valid /FullUI/Home payload. */
    data class Available(
        val home: HomeResponse,
    ) : Availability

    /** Use the stock Wholphin UI. [permanent] means: do not probe again this session. */
    data class Unavailable(
        val reason: FailureReason,
        val permanent: Boolean = reason.permanent,
    ) : Availability

    /** The user turned FullUI off in settings. */
    data object DisabledByUser : Availability
}

val Availability.usable: Boolean get() = this is Availability.Available

/**
 * Decides, once per session and per (server, user) key, whether the FullUI home can be used,
 * and caches the answer.
 *
 * - Plugin absent / 401 / 403 / garbage response: cached for the whole session ("permanent").
 * - Timeouts, network errors, 5xx: cached for [transientRetryMs], then probed again.
 * - A successful probe is cached; later failures of real calls can flip it with [markUnavailable].
 *
 * The successful probe's [HomeResponse] is handed out once through [takePrefetchedHome] so the
 * home screen does not have to request it a second time.
 */
class FeatureDetector(
    private val clock: () -> Long = System::currentTimeMillis,
    private val transientRetryMs: Long = 5 * 60_000L,
) {
    private class Entry(
        val value: Availability,
        val at: Long,
    )

    private val mutex = Mutex()
    private val cache = HashMap<String, Entry>()
    private val prefetched = HashMap<String, HomeResponse>()

    /** Cached answer for [key] if one exists and has not expired; never does I/O. */
    fun peek(key: String): Availability? {
        val e = synchronized(cache) { cache[key] } ?: return null
        val v = e.value
        if (v is Availability.Unavailable && !v.permanent && clock() - e.at >= transientRetryMs) return null
        return v
    }

    /**
     * Returns the cached answer for [key], or runs [probe] (at most one probe at a time) and caches it.
     * [probe] should perform GET /FullUI/Home with a short timeout.
     */
    suspend fun detect(
        key: String,
        probe: suspend () -> HomeResponse,
    ): Availability {
        peek(key)?.let { return it }
        return mutex.withLock {
            peek(key)?.let { return@withLock it }
            val result =
                try {
                    val home = probe()
                    synchronized(cache) { prefetched[key] = home }
                    Availability.Available(home)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: FullUiException) {
                    Availability.Unavailable(e.reason)
                } catch (e: Exception) {
                    Availability.Unavailable(FailureReason.NETWORK)
                }
            synchronized(cache) { cache[key] = Entry(result, clock()) }
            result
        }
    }

    /** Hand out (and forget) the home payload fetched during detection, if any. */
    fun takePrefetchedHome(key: String): HomeResponse? = synchronized(cache) { prefetched.remove(key) }

    /**
     * A real call failed after detection said "available". Permanent-class failures switch the
     * session to the stock UI; transient ones are ignored (the screen shows its own retry).
     * @return true when the cached state changed to unavailable.
     */
    fun markUnavailable(
        key: String,
        error: FullUiException,
    ): Boolean {
        if (!error.reason.permanent) return false
        synchronized(cache) {
            cache[key] = Entry(Availability.Unavailable(error.reason), clock())
            prefetched.remove(key)
        }
        return true
    }

    /** Forget everything for [key] (e.g. user pressed "retry FullUI" in settings). */
    fun reset(key: String? = null) {
        synchronized(cache) {
            if (key == null) {
                cache.clear()
                prefetched.clear()
            } else {
                cache.remove(key)
                prefetched.remove(key)
            }
        }
    }

    companion object {
        fun key(
            serverId: String?,
            userId: String?,
        ): String = "${serverId.orEmpty()}|${userId.orEmpty()}"
    }
}
