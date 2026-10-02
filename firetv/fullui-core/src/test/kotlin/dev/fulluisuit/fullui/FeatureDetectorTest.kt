package dev.fulluisuit.fullui

import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

class FeatureDetectorTest {
    private var now = 1_000L
    private val detector = FeatureDetector(clock = { now }, transientRetryMs = 60_000)
    private val key = FeatureDetector.key("server", "user")
    private val ok = HomeResponse(serverName = "S")

    @Test
    fun `available is cached and the probe runs once`() =
        runBlocking {
            val calls = AtomicInteger()
            val probe = suspend { calls.incrementAndGet(); ok }
            val a = detector.detect(key, probe)
            val b = detector.detect(key, probe)
            assertTrue(a.usable)
            assertTrue(b.usable)
            assertEquals(1, calls.get())
        }

    @Test
    fun `prefetched home is handed out once`() =
        runBlocking {
            detector.detect(key) { ok }
            assertEquals(ok, detector.takePrefetchedHome(key))
            assertNull(detector.takePrefetchedHome(key))
        }

    @Test
    fun `404 is permanent for the session`() =
        runBlocking {
            val calls = AtomicInteger()
            val probe: suspend () -> HomeResponse = {
                calls.incrementAndGet()
                throw FullUiException(FailureReason.NOT_FOUND, 404)
            }
            val a = detector.detect(key, probe) as Availability.Unavailable
            assertEquals(FailureReason.NOT_FOUND, a.reason)
            assertTrue(a.permanent)
            now += 24 * 3_600_000L
            detector.detect(key, probe)
            assertEquals(1, calls.get())
        }

    @Test
    fun `401 403 and bad responses are permanent`() =
        runBlocking {
            for (r in listOf(FailureReason.UNAUTHORIZED, FailureReason.FORBIDDEN, FailureReason.BAD_RESPONSE)) {
                val d = FeatureDetector(clock = { now })
                val res = d.detect("k") { throw FullUiException(r) } as Availability.Unavailable
                assertTrue("$r", res.permanent)
            }
        }

    @Test
    fun `timeouts are retried after the retry window`() =
        runBlocking {
            val calls = AtomicInteger()
            val probe: suspend () -> HomeResponse = {
                if (calls.incrementAndGet() == 1) throw FullUiException(FailureReason.TIMEOUT) else ok
            }
            val first = detector.detect(key, probe) as Availability.Unavailable
            assertFalse(first.permanent)
            // inside the window: cached failure, no new probe
            now += 30_000
            assertFalse(detector.detect(key, probe).usable)
            assertEquals(1, calls.get())
            // after the window: probe again and recover
            now += 31_000
            assertTrue(detector.detect(key, probe).usable)
            assertEquals(2, calls.get())
        }

    @Test
    fun `unexpected exceptions become a transient network failure`() =
        runBlocking {
            val res = detector.detect(key) { throw IllegalStateException("boom") } as Availability.Unavailable
            assertEquals(FailureReason.NETWORK, res.reason)
            assertFalse(res.permanent)
        }

    @Test
    fun `keys are independent per server and user`() =
        runBlocking {
            detector.detect("a|1") { throw FullUiException(FailureReason.NOT_FOUND) }
            assertTrue(detector.detect("a|2") { ok }.usable)
            assertNotNull(detector.peek("a|1"))
            assertNull(detector.peek("b|1"))
        }

    @Test
    fun `concurrent detects share one probe`() =
        runBlocking {
            val calls = AtomicInteger()
            val results =
                (1..8)
                    .map {
                        async {
                            detector.detect(key) {
                                calls.incrementAndGet()
                                delay(50)
                                ok
                            }
                        }
                    }.awaitAll()
            assertTrue(results.all { it.usable })
            assertEquals(1, calls.get())
        }

    @Test
    fun `markUnavailable flips permanent failures only`() =
        runBlocking {
            detector.detect(key) { ok }
            assertFalse(detector.markUnavailable(key, FullUiException(FailureReason.TIMEOUT)))
            assertTrue(detector.peek(key)!!.usable)
            assertTrue(detector.markUnavailable(key, FullUiException(FailureReason.NOT_FOUND, 404)))
            assertFalse(detector.peek(key)!!.usable)
            // and the stock UI stays for the session
            now += 10 * 3_600_000L
            assertFalse(detector.peek(key)!!.usable)
        }

    @Test
    fun `reset forgets state`() =
        runBlocking {
            detector.detect(key) { throw FullUiException(FailureReason.NOT_FOUND) }
            detector.reset(key)
            assertNull(detector.peek(key))
            assertTrue(detector.detect(key) { ok }.usable)
            detector.reset()
            assertNull(detector.peek(key))
        }
}
