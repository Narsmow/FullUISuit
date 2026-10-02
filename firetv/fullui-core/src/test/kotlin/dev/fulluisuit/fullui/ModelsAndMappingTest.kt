package dev.fulluisuit.fullui

import kotlinx.serialization.encodeToString
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ModelsAndMappingTest {
    private fun fixture(name: String): String = javaClass.classLoader!!.getResource(name)!!.readText()

    private val home: HomeResponse by lazy { FullUiJson.decodeFromString<HomeResponse>(fixture("home.json")) }

    // ---- JSON parsing ----------------------------------------------------------------------

    @Test
    fun `parses home response with camelCase, nulls and unknown fields`() {
        assertEquals("NarsFlix", home.serverName)
        assertEquals("#e50914", home.accentColor)
        assertEquals(
            listOf("continue", "toppicks", "top10-movies", "empty", "comingsoon"),
            home.rows.map { it.id },
        )
        val bunny = home.rows[1].items.single()
        assertEquals("22222222-2222-2222-2222-222222222222", bunny.id)
        assertEquals("aqz-KE-bpKQ", bunny.trailerKey)
        assertEquals(2, bunny.myRating)
        assertTrue(bunny.inMyList)
        assertEquals(7.5f, bunny.rating!!, 0.001f)
        assertEquals(listOf("Recently Added", "#2 in Movies", "Top Rated"), bunny.badges)
        assertNull(bunny.rank)
    }

    @Test
    fun `missing optional fields fall back to defaults`() {
        val minimal = FullUiJson.decodeFromString<ItemCard>("""{"id":"abc"}""")
        assertEquals("abc", minimal.id)
        assertEquals("", minimal.name)
        assertEquals(0, minimal.myRating)
        assertFalse(minimal.inMyList)
        assertTrue(minimal.badges.isEmpty())
        assertNull(minimal.progress)
    }

    @Test
    fun `nulls for non-null fields are coerced`() {
        val c = FullUiJson.decodeFromString<ItemCard>("""{"id":"abc","name":null,"genres":null,"myRating":null}""")
        assertEquals("", c.name)
        assertTrue(c.genres.isEmpty())
        assertEquals(0, c.myRating)
    }

    @Test
    fun `parses coming soon, search, myserver, notifications`() {
        val cs = FullUiJson.decodeFromString<ComingSoonResponse>("""{"cards":[{"tmdbId":5,"mediaType":"tv","title":"T","myVote":-1}]}""")
        assertEquals(-1, cs.cards.single().myVote)
        val s = FullUiJson.decodeFromString<SearchResponse>("""{"mode":"semantic","items":[{"id":"x","name":"N"}]}""")
        assertEquals("semantic", s.mode)
        assertEquals(1, s.items.size)
        val ms = FullUiJson.decodeFromString<MyServerResponse>("""{"continueWatching":[{"id":"a"}],"myList":[],"wanted":[{"tmdbId":1}]}""")
        assertEquals(1, ms.continueWatching.size)
        assertEquals(1, ms.wanted.size)
        val n =
            FullUiJson.decodeFromString<NotificationsResponse>(
                """{"items":[{"id":"6f1c2d3e-0000-0000-0000-000000000001","text":"Now on NarsFlix: X","at":"2026-10-01T10:00:00Z","read":false,"itemId":"abc"}]}""",
            )
        assertEquals("Now on NarsFlix: X", n.items.single().text)
        assertFalse(n.items.single().read)
    }

    @Test
    fun `request bodies serialize to the contract shape`() {
        assertEquals("""{"itemId":"i","rating":2}""", FullUiJson.encodeToString(RateRequest("i", 2)))
        assertEquals("""{"itemId":"i","add":true}""", FullUiJson.encodeToString(MyListRequest("i", true)))
        val v = FullUiJson.encodeToString(VoteRequest(tmdbId = 550, mediaType = "movie", vote = 1, title = "Fight"))
        assertTrue(v, v.contains("\"tmdbId\":550") && v.contains("\"mediaType\":\"movie\"") && v.contains("\"vote\":1"))
    }

    // ---- ids -------------------------------------------------------------------------------

    @Test
    fun `ids parse in both formats`() {
        val a = Ids.parse("11111111111111111111111111111111")
        val b = Ids.parse("11111111-1111-1111-1111-111111111111")
        assertNotNull(a)
        assertEquals(a, b)
        assertNull(Ids.parse("nope"))
        assertEquals("11111111-1111-1111-1111-111111111111", Ids.dashed("11111111111111111111111111111111"))
        assertEquals("11111111111111111111111111111111", Ids.undashed("11111111-1111-1111-1111-111111111111"))
    }

    // ---- row mapping -----------------------------------------------------------------------

    @Test
    fun `empty rows are dropped and order is kept`() {
        val ui = Mapping.home(home)
        assertEquals(listOf("continue", "toppicks", "top10-movies", "comingsoon"), ui.rows.map { it.id })
        assertEquals("NarsFlix", ui.serverName)
        assertEquals(0xFFE50914.toInt(), ui.accentArgb)
    }

    @Test
    fun `row kinds map from wire types`() {
        val kinds = Mapping.home(home).rows.map { it.kind }
        assertEquals(listOf(RowKind.CONTINUE, RowKind.TOP_PICKS, RowKind.TOP10, RowKind.COMING_SOON), kinds)
        assertEquals(RowKind.OTHER, RowKind.fromWire("somethingnew"))
        assertEquals(RowKind.BECAUSE, RowKind.fromWire(" Because "))
        assertEquals(RowKind.OTHER, RowKind.fromWire(null))
    }

    @Test
    fun `top 10 row is numbered and sorted by rank`() {
        val top = Mapping.home(home).rows.first { it.kind == RowKind.TOP10 }
        assertTrue(top.numbered)
        assertEquals(listOf(1, 3), top.cards.map { it.rank })
        assertEquals(listOf("First", "Third"), top.cards.map { it.name })
        // Other rows are not numbered
        assertFalse(Mapping.home(home).rows.first { it.kind == RowKind.TOP_PICKS }.numbered)
    }

    @Test
    fun `continue watching shows progress`() {
        val row = Mapping.home(home).rows.first { it.kind == RowKind.CONTINUE }
        assertTrue(row.showsProgress)
        assertEquals(0.42f, row.cards.single().progress!!, 0.0001f)
        assertTrue(row.cards.single().isSeries)
    }

    @Test
    fun `badge styles and ordering`() {
        assertEquals(BadgeStyle.RANK, Mapping.badge("#3 in Shows").style)
        assertEquals(BadgeStyle.RANK, Mapping.badge("#10 in Movies").style)
        assertEquals(BadgeStyle.NEW, Mapping.badge("New Season").style)
        assertEquals(BadgeStyle.NEW, Mapping.badge("New Episodes").style)
        assertEquals(BadgeStyle.RECENT, Mapping.badge("Recently Added").style)
        assertEquals(BadgeStyle.TOP_RATED, Mapping.badge("Top Rated").style)
        assertEquals(BadgeStyle.NOW_ON, Mapping.badge("Now on NarsFlix").style)
        assertEquals(BadgeStyle.OTHER, Mapping.badge("Something else").style)

        val bunny = Mapping.home(home).rows.first { it.kind == RowKind.TOP_PICKS }.cards.single()
        // rank first, then recent, then top rated
        assertEquals(listOf("#2 in Movies", "Recently Added", "Top Rated"), bunny.badges.map { it.text })
        assertEquals(listOf("#2 in Movies"), Mapping.ribbon(bunny).map { it.text })
        assertEquals(2, Mapping.ribbon(bunny, 2).size)
    }

    @Test
    fun `card fields`() {
        val bunny = Mapping.home(home).rows.first { it.kind == RowKind.TOP_PICKS }.cards.single()
        assertEquals("2008  ·  G  ·  10m", bunny.metaLine)
        assertFalse(bunny.isSeries)
        assertEquals(2, bunny.myRating)
        assertTrue(bunny.inMyList)
        assertNull(bunny.progress)
    }

    @Test
    fun `runtime and meta line formatting`() {
        assertEquals("1h 52m", Mapping.runtime(112))
        assertEquals("2h", Mapping.runtime(120))
        assertEquals("45m", Mapping.runtime(45))
        assertEquals("", Mapping.metaLine(null, null, null))
        assertEquals("2020", Mapping.metaLine(2020, " ", 0))
    }

    @Test
    fun `rating and rank are clamped`() {
        val c = Mapping.card(ItemCard(id = "x", myRating = 9, rank = 42, progress = 7.0))
        assertEquals(2, c.myRating)
        assertNull(c.rank)
        assertEquals(1f, c.progress!!, 0.0001f)
    }

    @Test
    fun `coming soon cards map with release label and votes`() {
        val row = Mapping.home(home).rows.first { it.kind == RowKind.COMING_SOON }
        assertTrue(row.cards.isEmpty())
        assertEquals(2, row.comingSoon.size)
        assertEquals("Nov 20, 2026", row.comingSoon[0].releaseLabel)
        assertEquals(1, row.comingSoon[0].myVote)
        assertNull(row.comingSoon[1].releaseLabel)
        assertEquals("https://image.tmdb.org/t/p/w342/abc.jpg", ImageUrls.tmdbPoster(row.comingSoon[0].posterPath))
        assertNull(ImageUrls.tmdbPoster(null))
    }

    @Test
    fun `release label degrades gracefully`() {
        assertEquals("2027", Mapping.releaseLabel("2027"))
        assertEquals("Mar 2027", Mapping.releaseLabel("2027-03"))
        assertEquals("Mar 5, 2027", Mapping.releaseLabel("2027-03-05T00:00:00Z"))
        assertNull(Mapping.releaseLabel("soon"))
        assertNull(Mapping.releaseLabel(null))
    }

    @Test
    fun `accent parsing`() {
        assertEquals(0xFFE50914.toInt(), Mapping.parseAccent("#e50914"))
        assertEquals(0xFFE50914.toInt(), Mapping.parseAccent("E50914"))
        assertEquals(0x80E50914.toInt(), Mapping.parseAccent("#80e50914"))
        assertNull(Mapping.parseAccent("red"))
        assertNull(Mapping.parseAccent(""))
        assertNull(Mapping.parseAccent(null))
    }

    @Test
    fun `hero prefers top picks card with backdrop, synopsis and trailer`() {
        val ui = Mapping.home(home)
        assertEquals("Big Buck Bunny", ui.hero?.name)
    }

    @Test
    fun `hero never comes from continue watching when other rows qualify, falls back otherwise`() {
        val onlyContinue = Mapping.home(HomeResponse(rows = listOf(home.rows[0])))
        // continue-only: last resort picks it
        assertEquals("Half Watched", onlyContinue.hero?.name)
        val none = Mapping.home(HomeResponse(rows = listOf(HomeRow("g", "G", "genre", listOf(ItemCard(id = "z"))))))
        assertNull(none.hero)
    }

    // ---- optimistic updates ----------------------------------------------------------------

    @Test
    fun `rating cycle clears when pressing the active level`() {
        assertEquals(1, Mapping.nextRating(0, 1))
        assertEquals(0, Mapping.nextRating(1, 1))
        assertEquals(2, Mapping.nextRating(1, 2))
        assertEquals(-1, Mapping.nextRating(2, -1))
        assertEquals(0, Mapping.nextVote(1, 1))
        assertEquals(-1, Mapping.nextVote(1, -1))
    }

    @Test
    fun `updateCard changes every occurrence including the hero`() {
        val ui = Mapping.home(home)
        val id = ui.hero!!.id
        val updated = ui.updateCard(id) { it.copy(inMyList = !it.inMyList, myRating = 1) }
        assertFalse(updated.hero!!.inMyList)
        assertEquals(1, updated.hero!!.myRating)
        val inRow = updated.rows.first { it.kind == RowKind.TOP_PICKS }.cards.single()
        assertFalse(inRow.inMyList)
        // other cards untouched
        assertEquals(ui.rows[0], updated.rows[0])
    }

    @Test
    fun `updateVote changes only the matching coming soon card`() {
        val ui = Mapping.home(home).updateVote(1399, "tv", -1)
        val cs = ui.rows.first { it.kind == RowKind.COMING_SOON }.comingSoon
        assertEquals(1, cs[0].myVote)
        assertEquals(-1, cs[1].myVote)
    }

    // ---- image urls ------------------------------------------------------------------------

    @Test
    fun `image urls follow the contract`() {
        val urls = ImageUrls { "http://jf.local:8096/" }
        assertEquals(
            "http://jf.local:8096/Items/22222222222222222222222222222222/Images/Backdrop?maxWidth=640&quality=90",
            urls.backdrop("22222222-2222-2222-2222-222222222222", 640),
        )
        val noBackdrop = Mapping.card(ItemCard(id = "abc", hasBackdrop = false))
        assertTrue(urls.landscape(noBackdrop, 300)!!.contains("/Images/Primary"))
        assertNull(ImageUrls { null }.primary("abc", 100))
        assertEquals("https://www.youtube.com/watch?v=k", ImageUrls.youtubeWatchUrl("k"))
        assertTrue(ImageUrls.youtubeEmbedUrl("k").contains("mute=1"))
    }
}

class TabFilterAndTrailerTest {
    private val home =
        Mapping.home(
            FullUiJson.decodeFromString<HomeResponse>(
                javaClass.classLoader!!.getResource("home.json")!!.readText(),
            ),
        )

    @Test
    fun `shows tab keeps only series and tv coming soon`() {
        val shows = home.filterByType(series = true)
        assertTrue(shows.rows.flatMap { it.cards }.all { it.isSeries })
        assertEquals(listOf("continue", "comingsoon"), shows.rows.map { it.id })
        assertEquals(listOf("tv"), shows.rows.last().comingSoon.map { it.mediaType })
        assertEquals("Half Watched", shows.hero?.name)
    }

    @Test
    fun `movies tab keeps only movies and movie coming soon`() {
        val movies = home.filterByType(series = false)
        assertTrue(movies.rows.flatMap { it.cards }.none { it.isSeries })
        assertEquals(listOf("toppicks", "top10-movies", "comingsoon"), movies.rows.map { it.id })
        assertEquals(listOf("movie"), movies.rows.last().comingSoon.map { it.mediaType })
        assertEquals("Big Buck Bunny", movies.hero?.name)
    }

    @Test
    fun `trailer page only accepts plain youtube ids`() {
        assertTrue(TrailerHtml.isValidVideoId("aqz-KE-bpKQ"))
        assertFalse(TrailerHtml.isValidVideoId("x');alert(1);//"))
        assertFalse(TrailerHtml.isValidVideoId(""))
        assertFalse(TrailerHtml.isValidVideoId(null))
        assertNull(TrailerHtml.page("bad id"))
        val page = TrailerHtml.page("aqz-KE-bpKQ", muted = true)!!
        assertTrue(page.contains("videoId: 'aqz-KE-bpKQ'"))
        assertTrue(page.contains("var wantMuted = true;"))
        assertTrue(page.contains("FullUiBridge.onPlaying()"))
        assertTrue(page.contains("iframe_api"))
        assertTrue(TrailerHtml.page("aqz-KE-bpKQ", muted = false)!!.contains("var wantMuted = false;"))
    }
}

class MyServerMappingTest {
    @Test
    fun `my server maps and patches`() {
        val res =
            FullUiJson.decodeFromString<MyServerResponse>(
                """{"continueWatching":[{"id":"a","name":"A","progress":0.5}],"myList":[{"id":"a","name":"A","inMyList":true},{"id":"b","name":"B"}],"wanted":[{"tmdbId":9,"mediaType":"movie","title":"W","myVote":1}]}""",
            )
        val ui = Mapping.myServer(res)
        assertFalse(ui.isEmpty)
        assertEquals(0.5f, ui.continueWatching.single().progress!!, 0.0001f)
        val patched = ui.updateCard("a") { it.copy(myRating = 2) }
        assertEquals(2, patched.continueWatching.single().myRating)
        assertEquals(2, patched.myList.first().myRating)
        assertEquals(0, patched.myList.last().myRating)
        assertEquals(-1, ui.updateVote(9, "movie", -1).wanted.single().myVote)
        assertTrue(Mapping.myServer(MyServerResponse()).isEmpty)
    }
}
