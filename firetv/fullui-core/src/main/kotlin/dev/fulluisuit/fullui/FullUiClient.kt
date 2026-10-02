package dev.fulluisuit.fullui

import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.serialization.SerializationException
import kotlinx.serialization.encodeToString
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/** Why a call to the plugin failed. Drives the stock-UI fallback decision. */
enum class FailureReason(
    /** True when retrying later in the same session is pointless (plugin absent / not allowed). */
    val permanent: Boolean,
) {
    /** 404/405/501: the plugin is not installed (or too old). */
    NOT_FOUND(true),

    /** 401: token not accepted for this endpoint. */
    UNAUTHORIZED(true),

    /** 403: user not allowed. */
    FORBIDDEN(true),

    /** 200 but not the JSON we expect (reverse-proxy HTML page, wrong content). */
    BAD_RESPONSE(true),

    /** Timeout while connecting or reading. */
    TIMEOUT(false),

    /** DNS / refused / reset / offline. */
    NETWORK(false),

    /** 5xx other than 501. */
    SERVER_ERROR(false),

    /** No server URL available (not signed in). */
    NO_SERVER(false),
}

class FullUiException(
    val reason: FailureReason,
    val httpCode: Int? = null,
    message: String? = null,
    cause: Throwable? = null,
) : Exception(message ?: "FullUI request failed: $reason${httpCode?.let { " (HTTP $it)" } ?: ""}", cause)

/**
 * Thin client for the FullUI server plugin.
 *
 * @param http an [OkHttpClient] that already adds the Jellyfin `Authorization` header
 * @param baseUrl returns the current server base URL (no trailing slash needed) or null
 */
class FullUiClient(
    private val http: OkHttpClient,
    private val baseUrl: () -> String?,
    /** Per-call timeout for ordinary requests. */
    private val callTimeoutMs: Long = 15_000,
) {
    suspend fun home(timeoutMs: Long = callTimeoutMs): HomeResponse = get("Home", emptyMap(), timeoutMs)

    suspend fun item(id: String): ItemCard = get("Item/${Ids.undashed(id)}", emptyMap())

    suspend fun myServer(): MyServerResponse = get("MyServer", emptyMap())

    suspend fun comingSoon(): ComingSoonResponse = get("ComingSoon", emptyMap())

    suspend fun search(query: String): SearchResponse = get("Search", mapOf("q" to query))

    suspend fun notifications(): NotificationsResponse = get("Notifications", emptyMap())

    suspend fun markNotificationsRead(ids: List<String>? = null) {
        post("Notifications/Read", FullUiJson.encodeToString(MarkReadRequest(ids?.map { Ids.dashed(it) })))
    }

    /** rating: -1 not for me, 0 clear, 1 like, 2 love */
    suspend fun rate(
        itemId: String,
        rating: Int,
    ) {
        post("Rate", FullUiJson.encodeToString(RateRequest(Ids.dashed(itemId), rating)))
    }

    suspend fun setMyList(
        itemId: String,
        add: Boolean,
    ) {
        post("MyList", FullUiJson.encodeToString(MyListRequest(Ids.dashed(itemId), add)))
    }

    suspend fun vote(request: VoteRequest) {
        post("Vote", FullUiJson.encodeToString(request))
    }

    // ---- plumbing -------------------------------------------------------------------------

    private fun urlFor(
        path: String,
        query: Map<String, String>,
    ): HttpUrl {
        val base = baseUrl()?.trim()?.trimEnd('/')
        if (base.isNullOrEmpty()) throw FullUiException(FailureReason.NO_SERVER)
        val url =
            "$base/FullUI/$path".toHttpUrlOrNull()
                ?: throw FullUiException(FailureReason.NO_SERVER, message = "Invalid server url: $base")
        return url
            .newBuilder()
            .apply { query.forEach { (k, v) -> addQueryParameter(k, v) } }
            .build()
    }

    private suspend inline fun <reified T> get(
        path: String,
        query: Map<String, String>,
        timeoutMs: Long = callTimeoutMs,
    ): T {
        val request = Request.Builder().url(urlFor(path, query)).get().header("Accept", "application/json").build()
        val body = execute(request, timeoutMs)
        return decode(body)
    }

    private suspend fun post(
        path: String,
        jsonBody: String,
    ) {
        val request =
            Request
                .Builder()
                .url(urlFor(path, emptyMap()))
                .post(jsonBody.toRequestBody(JSON_MEDIA))
                .header("Accept", "application/json")
                .build()
        execute(request, callTimeoutMs)
    }

    private inline fun <reified T> decode(body: String): T =
        try {
            FullUiJson.decodeFromString<T>(body)
        } catch (e: SerializationException) {
            throw FullUiException(FailureReason.BAD_RESPONSE, message = "Unexpected response: ${e.message}", cause = e)
        } catch (e: IllegalArgumentException) {
            throw FullUiException(FailureReason.BAD_RESPONSE, message = "Unexpected response: ${e.message}", cause = e)
        }

    /** Runs the call and returns the body text for a 2xx, otherwise throws a classified [FullUiException]. */
    private suspend fun execute(
        request: Request,
        timeoutMs: Long,
    ): String {
        val client = http.newBuilder().callTimeout(timeoutMs, TimeUnit.MILLISECONDS).build()
        val response =
            try {
                client.newCall(request).await()
            } catch (e: java.io.InterruptedIOException) {
                throw FullUiException(FailureReason.TIMEOUT, cause = e)
            } catch (e: IOException) {
                throw FullUiException(FailureReason.NETWORK, message = e.message, cause = e)
            }
        response.use { r ->
            val code = r.code
            if (code in 200..299) {
                val text = r.body.string()
                val type = r.header("Content-Type").orEmpty()
                // Reverse proxies often answer unknown paths with an HTML SPA page and HTTP 200.
                if (type.isNotEmpty() && !type.contains("json", ignoreCase = true) && text.isNotBlank()) {
                    throw FullUiException(FailureReason.BAD_RESPONSE, code, "Unexpected content type: $type")
                }
                return text
            }
            throw FullUiException(classify(code), code)
        }
    }

    companion object {
        private val JSON_MEDIA = "application/json; charset=utf-8".toMediaType()

        fun classify(code: Int): FailureReason =
            when (code) {
                401 -> FailureReason.UNAUTHORIZED
                403 -> FailureReason.FORBIDDEN
                404, 405, 501 -> FailureReason.NOT_FOUND
                in 500..599 -> FailureReason.SERVER_ERROR
                else -> FailureReason.BAD_RESPONSE
            }
    }
}

private suspend fun Call.await(): Response =
    suspendCancellableCoroutine { cont ->
        cont.invokeOnCancellation { runCatching { cancel() } }
        enqueue(
            object : Callback {
                override fun onFailure(
                    call: Call,
                    e: IOException,
                ) {
                    if (cont.isActive) cont.resumeWithException(e)
                }

                override fun onResponse(
                    call: Call,
                    response: Response,
                ) {
                    if (cont.isActive) cont.resume(response) else response.close()
                }
            },
        )
    }
