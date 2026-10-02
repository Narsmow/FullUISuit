package dev.fulluisuit.fullui

import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.SocketPolicy
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test

class FullUiClientTest {
    private lateinit var server: MockWebServer
    private lateinit var client: FullUiClient

    private fun fixture(name: String): String = javaClass.classLoader!!.getResource(name)!!.readText()

    @Before
    fun setUp() {
        server = MockWebServer()
        server.start()
        val http =
            OkHttpClient
                .Builder()
                .addInterceptor { chain ->
                    chain.proceed(chain.request().newBuilder().addHeader("Authorization", "MediaBrowser Token=\"tok\"").build())
                }.build()
        client = FullUiClient(http, baseUrl = { server.url("/").toString() })
    }

    @After
    fun tearDown() {
        server.shutdown()
    }

    private fun json(
        body: String,
        code: Int = 200,
    ) = MockResponse().setResponseCode(code).setHeader("Content-Type", "application/json; charset=utf-8").setBody(body)

    private fun expectFailure(
        reason: FailureReason,
        block: suspend () -> Unit,
    ): FullUiException {
        try {
            runBlocking { block() }
        } catch (e: FullUiException) {
            assertEquals(reason, e.reason)
            return e
        }
        fail("expected FullUiException($reason)")
        throw AssertionError()
    }

    @Test
    fun `home parses and sends auth header on the right path`() {
        server.enqueue(json(fixture("home.json")))
        val home = runBlocking { client.home() }
        assertEquals("NarsFlix", home.serverName)
        assertEquals(5, home.rows.size)
        val req = server.takeRequest()
        assertEquals("GET", req.method)
        assertEquals("/FullUI/Home", req.path)
        assertEquals("MediaBrowser Token=\"tok\"", req.getHeader("Authorization"))
    }

    @Test
    fun `404 means plugin absent`() {
        server.enqueue(MockResponse().setResponseCode(404))
        val e = expectFailure(FailureReason.NOT_FOUND) { client.home() }
        assertEquals(404, e.httpCode)
    }

    @Test
    fun `401 and 403 are classified`() {
        server.enqueue(MockResponse().setResponseCode(401))
        expectFailure(FailureReason.UNAUTHORIZED) { client.home() }
        server.enqueue(MockResponse().setResponseCode(403))
        expectFailure(FailureReason.FORBIDDEN) { client.home() }
    }

    @Test
    fun `500 is a transient server error but 501 means absent`() {
        server.enqueue(MockResponse().setResponseCode(500))
        expectFailure(FailureReason.SERVER_ERROR) { client.home() }
        server.enqueue(MockResponse().setResponseCode(501))
        expectFailure(FailureReason.NOT_FOUND) { client.home() }
    }

    @Test
    fun `html with 200 is a bad response not a crash`() {
        server.enqueue(
            MockResponse().setResponseCode(200).setHeader("Content-Type", "text/html").setBody("<html>Jellyfin SPA</html>"),
        )
        expectFailure(FailureReason.BAD_RESPONSE) { client.home() }
    }

    @Test
    fun `invalid json with json content type is a bad response`() {
        server.enqueue(json("""{"rows": "not-a-list"}"""))
        expectFailure(FailureReason.BAD_RESPONSE) { client.home() }
    }

    @Test
    fun `read timeout is reported as TIMEOUT`() {
        server.enqueue(json(fixture("home.json")).setSocketPolicy(SocketPolicy.NO_RESPONSE))
        expectFailure(FailureReason.TIMEOUT) { client.home(timeoutMs = 300) }
    }

    @Test
    fun `connection refused is reported as NETWORK`() {
        val dead = MockWebServer().also { it.start() }
        val url = dead.url("/").toString()
        dead.shutdown()
        val c = FullUiClient(OkHttpClient(), baseUrl = { url })
        expectFailure(FailureReason.NETWORK) { c.home() }
    }

    @Test
    fun `no server url fails fast`() {
        val c = FullUiClient(OkHttpClient(), baseUrl = { null })
        expectFailure(FailureReason.NO_SERVER) { c.home() }
    }

    @Test
    fun `rate posts the contract body with a dashed guid`() {
        server.enqueue(MockResponse().setResponseCode(204))
        runBlocking { client.rate("11111111111111111111111111111111", 2) }
        val req = server.takeRequest()
        assertEquals("POST", req.method)
        assertEquals("/FullUI/Rate", req.path)
        assertTrue(req.getHeader("Content-Type")!!.startsWith("application/json"))
        assertEquals("""{"itemId":"11111111-1111-1111-1111-111111111111","rating":2}""", req.body.readUtf8())
    }

    @Test
    fun `my list and vote post to their routes`() {
        server.enqueue(MockResponse().setResponseCode(204))
        server.enqueue(MockResponse().setResponseCode(200).setHeader("Content-Type", "application/json").setBody("{}"))
        runBlocking {
            client.setMyList("22222222-2222-2222-2222-222222222222", true)
            client.vote(VoteRequest(tmdbId = 550, mediaType = "movie", vote = -1, title = "X"))
        }
        val a = server.takeRequest()
        assertEquals("/FullUI/MyList", a.path)
        assertEquals("""{"itemId":"22222222-2222-2222-2222-222222222222","add":true}""", a.body.readUtf8())
        val b = server.takeRequest()
        assertEquals("/FullUI/Vote", b.path)
        val body = b.body.readUtf8()
        assertTrue(body, body.contains("\"vote\":-1") && body.contains("\"tmdbId\":550"))
    }

    @Test
    fun `search encodes the query`() {
        server.enqueue(json("""{"mode":"semantic","items":[{"id":"a","name":"A"}]}"""))
        val res = runBlocking { client.search("big bunny & friends") }
        assertEquals("semantic", res.mode)
        val req = server.takeRequest()
        assertEquals("/FullUI/Search?q=big%20bunny%20%26%20friends", req.path)
    }

    @Test
    fun `my server, coming soon, notifications and item hit their routes`() {
        server.enqueue(json("""{"continueWatching":[],"myList":[],"wanted":[]}"""))
        server.enqueue(json("""{"cards":[]}"""))
        server.enqueue(json("""{"items":[]}"""))
        server.enqueue(json("""{"id":"abc","name":"N"}"""))
        server.enqueue(MockResponse().setResponseCode(204))
        runBlocking {
            client.myServer()
            client.comingSoon()
            client.notifications()
            assertEquals("N", client.item("abc").name)
            client.markNotificationsRead(listOf("6f1c2d3e-0000-0000-0000-000000000001"))
        }
        assertEquals("/FullUI/MyServer", server.takeRequest().path)
        assertEquals("/FullUI/ComingSoon", server.takeRequest().path)
        assertEquals("/FullUI/Notifications", server.takeRequest().path)
        assertEquals("/FullUI/Item/abc", server.takeRequest().path)
        val read = server.takeRequest()
        assertEquals("/FullUI/Notifications/Read", read.path)
        assertNotNull(read.body.readUtf8())
    }

    @Test
    fun `base url with a path prefix is respected`() {
        val c = FullUiClient(OkHttpClient(), baseUrl = { server.url("/jellyfin/").toString().trimEnd('/') })
        server.enqueue(json("""{"serverName":"S","accentColor":"","rows":[]}"""))
        runBlocking { c.home() }
        assertEquals("/jellyfin/FullUI/Home", server.takeRequest().path)
    }
}
