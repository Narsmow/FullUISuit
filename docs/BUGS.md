# FullUI bug list (consolidated review)

Source: four read-only file-by-file reviews (server C#, web bundle, Fire TV app, installer/CI). Duplicates found by several reviewers are merged into one entry. Ordered by importance: fix from the top.

Severity: **CRITICAL** = feature totally broken / security hole / data loss. **HIGH** = likely to fail or misbehave in real use. **MEDIUM** = edge case or robustness gap. **LOW** = polish.
Status column: `open` until fixed; "unverified" = reviewer could not confirm without a live Jellyfin / Android device.

Installer/CI review: **pending** (section at the bottom is filled in when it reports).

## CRITICAL

| ID | Area | Where | Problem | Fix | Status |
|---|---|---|---|---|---|
| B-01 | web | `web/src/api.ts:40` (also `nav.ts:200`, `rows.ts:50`, `card.ts:227,248`) | `serverBase()` calls `ApiClient.getUrl('')`. The real jellyfin-apiclient throws "Url name cannot be empty". Every FullUI request fails, so the reskin never renders on a real server (always falls back to classic view + banner). Test mocks accepted `''`, hiding it. | Use `serverAddress()` / `getUrl('/')` and strip the trailing slash. Make mocks throw on an empty name. | open |
| B-02 | server + web + firetv | `Api/DiscoveryController.cs`, `Api/AdminController.cs`, `Api/FullUIController.cs` (`TestResult`) | Only `HomeController` serializes camelCase. Jellyfin's MVC formatter is PascalCase, so Search, Notifications, ComingSoon, Admin Requests and the Tmdb/Ollama Test endpoints return `Items`/`Mode`/`Cards`/`Ok`/`Message`. Search is always empty, the bell never shows anything, "Test connection" shows "✗ undefined", the Most Requested page is blank, and the Fire TV client (case-sensitive) gets empty results. Not run on a live server. | One shared `SafeApi` helper returning `JsonResult` with camelCase options for every action; add a serialization test asserting lowercase keys. | open |
| B-03 | firetv (security) | `ui/Components.kt:360,378`, `ImageUrls.kt:36-45`, `MainActivity.kt:257`, `services/hilt/AppModule.kt:99-122` | Coming Soon posters load from `image.tmdb.org` through Coil using the authenticated OkHttp client, which adds `Authorization: MediaBrowser Token=...` to every request. The Jellyfin access token is sent to TMDB's CDN. | Only add the auth header when the request host equals the current server's host (or give Coil a host-filtering client). | open |
| B-04 | firetv | `fullui-core/.../FullUiClient.kt:159-171`, `FullUiService.kt:49-55`, `ui/FullUiShell.kt:75-77` | The feature probe's body read runs on the Compose main dispatcher, raising NetworkOnMainThreadException, which is caught and recorded as "unavailable". FullUI silently never activates; stock UI always shows. JVM tests missed it (`runBlocking`). | Do the call and body read in `withContext(Dispatchers.IO)`. | open |

## HIGH

| ID | Area | Where | Problem | Fix | Status |
|---|---|---|---|---|---|
| B-05 | server | `Data/PluginStore.cs:53,68` | Saves rewrite the whole store, including AI embeddings, inside the global lock (5,000 titles x 768 dims = 44 MB JSON, ~1 s flush, 1.8 s load); every Read blocks. The debounce timer is reset on each Write, so it never fires under continuous writes (data lost on crash). Serialization is outside the `try`, so a throw on the timer thread is an unhandled exception that can kill the Jellyfin process. `Write` after `Dispose` throws. | Move embeddings to a separate lazily loaded file; snapshot under the lock and serialize outside it; arm the timer only if not pending; wrap all of `Flush` in try/catch. | open |
| B-06 | server | `WebInjection.cs:13-49`, `PluginServiceRegistrator.cs:10` (+ web H5/M11) | Injected tag uses root-absolute `/FullUI/web/...` (404 under a Jellyfin base URL / reverse-proxy subpath). `TryRegister` runs once inside `RegisterServices`, swallows all exceptions with no logging, never retries (File Transformation may not be loaded yet). `JObject` identity across plugin load contexts is a risk; missing Newtonsoft.Json can throw at JIT outside the try. Result: no UI and no way for a non-coder to see why. | Relative or BaseUrl-aware URLs; retry from a hosted service after startup; log the outcome; build the payload from the reflected parameter type; show injection status on the settings page. | open (unverified) |
| B-07 | server | `Events/EventTracker.cs:144`, `Library/JellyfinCatalog.cs:103,138` | Every `ItemAdded` (each episode during a scan) invalidates the catalog and clears all home caches; the next request runs a full-library + all-episodes query inside the lock; `Invalidate()` takes the same lock so the scanner thread blocks. Big scans stall the server. | Time-debounced staleness (reload at most every 30-60 s); lock-free flag; build the snapshot outside the lock; skip virtual episodes. | open |
| B-08 | web (privacy) | `web/src/main.ts:238-245`, `nav.ts:189-194,20` | Home cache (60 s), notifications and avatar/admin state are not keyed to the user/token. If user A signs out and B signs in within 60 s on the same browser/TV, B sees A's rows, ratings, My List and possibly the Dashboard link. | Key caches by userId+token; drop on change and on logout. | open |
| B-09 | web | `web/src/nav.ts:181-184` | Sign out calls only `ApiClient.logout()` (clears the token, no navigation). The user stays on the overlay with a null token; polls 401 every minute; nothing leads to the login page. | Call `Dashboard.logout()` (fallback: `logout()` + `location.hash='#/login'`), stop nav, release overlay. | open |
| B-10 | web + firetv | `web/src/trailer.ts:112-144`; `fullui-core/TrailerHtml.kt:12`, `ui/TrailerPlayer.kt` | jellyfin-web sets `referrer: no-referrer`; YouTube embeds without a referrer/origin commonly fail (errors 150/152/153), so the headline trailer feature may never play (graceful fallback to backdrop). Fire TV page also lacks `origin`/`widget_referrer`. | Build the iframe yourself with `referrerpolicy="strict-origin-when-cross-origin"` + `enablejsapi=1` (web); distinct https origin + `origin` playerVar (Fire TV); test on real devices. | open (unverified) |
| B-11 | firetv | `app/build.gradle.kts`, `services/AppUpgradeHandler.kt:148-440` | FullUI versions start at 0.1.0 but upstream migrations run for `previous <= 1.0.6-7`, so every FullUI update re-runs all migrations and resets subtitle style, playback overrides, auto sign-in, MPV, live-TV prefs. | Start FullUI at >= 1.1.0 or add a fork baseline guard. | open |
| B-12 | firetv | `FullUiViewModel.kt:332-336,253,272,303`, `FullUiClient.kt:186-194` | Any failed action (Rate/MyList 404 for a removed item, Vote 400, 408/429) marks the plugin "permanently unavailable" for the session and drops the whole app to the stock UI. | Only Home/probe failures may flip availability; actions show a toast. Treat 408/429 as transient. | open |
| B-13 | CI | `.github/workflows/release.yml:62-75` | The `v*` workflow builds the Fire TV APK with no Android SDK, no secrets and no version, picks the alphabetically first APK (likely the wrong flavor/ABI), debug-signed, attached as `FullUI-FireTV.apk`. Users who install it later get a signature mismatch on update. | Remove that job (use `firetv.yml`) or make it call the same build; fail rather than fall back to debug signing. | open |
| B-14 | firetv | `ui/HomeScreens.kt:97-108`, `ui/Components.kt:99,178,345` | Infinite-transition and animated Dp values are read in composition, so the hero and every row/card recompose each frame (60 fps). Jank on Fire Stick hardware. | Read values inside `graphicsLayer {}` / `Modifier.layout {}` lambdas. | open |
| B-15 | server | `Tasks/MaterializePlaylistsTask.cs`, `Discovery/IHomeRowsProvider.cs` | Nothing implements or registers `IHomeRowsProvider`, so the "Write FullUI: playlists" setting silently does nothing (the fallback for native clients does not exist). | Implement it over `HomeService.GetHome` and register it, or hide the setting. | open |
| B-16 | server + web | `Services/HomeService.cs:61-70`, `web/src/pages.ts:285-306` | A failed Home build returns HTTP 200 with `rows: []`, indistinguishable from an empty library; no fallback to the classic home. | Return 5xx on failure, or treat empty rows on the home route as failure on the client. | open |

## MEDIUM

| ID | Area | Where | Problem | Fix |
|---|---|---|---|---|
| B-17 | server | `Ai/NlSearch.cs:40,61`, `Ai/EmbeddingIndexer.cs` | (a) With Ollama on, only embedded titles are returned, so newly added titles are not found even by exact name; (b) changing the embedding model leaves old-dimension vectors that are never re-embedded (cosine 0); (c) every search waits up to 45 s on Ollama with no circuit breaker. | Merge keyword hits; store model+text hash per vector; short timeout + 60 s back-off. |
| B-18 | server | `Discovery/ComingSoonService.cs:205,244`, `TmdbClient.GetAsync` | A TMDB failure is recorded as "no trailer" in a never-expiring static `NoTrailer` map (one outage permanently loses trailers for up to 200 titles); the HttpClient has the default 100 s timeout and no failure budget, so a nightly run or the Test button can hang for hours/100 s. | Distinguish "none" from "failed"; TTL on `NoTrailer`; 15 s timeout; stop after N consecutive failures. |
| B-19 | server | `Discovery/TmdbClient.cs:283` | `_cache` of `JsonDocument` is never evicted (grows with every title/seed). | Cap and evict, or cache parsed results only. |
| B-20 | server | `Events/EventTracker.cs`, `Recs/RecEngine.cs:497` | No backfill of existing Jellyfin watch data: new installs start cold (empty Continue Watching despite resume points). A series whose last-watched episode was completed vanishes from Continue Watching; no next-episode logic. | One-time seed from `IUserDataManager` (played, position, favorite); add next-up. |
| B-21 | server | `Discovery/ComingSoonRanker.cs:60` | `SelectSeeds` is O(titles x signals) per user while holding the global store lock; can stall the API during the nightly run. | Group the user's signals once; compute outside the lock on a snapshot. |
| B-22 | server | `Services/HomeService.cs:215` vs `Api/DiscoveryController.cs:48` | Home's Coming Soon row does not drop titles already in the library and hides "not for me" entries; the endpoint does the opposite. | Share one function. |
| B-23 | server | `Discovery/VoteService.cs:41`, `Api/DiscoveryController.cs` | Unbounded votes per user (any tmdbId/title), notifications never pruned, data of deleted users never purged. | Cap votes per user; prune notifications by age; purge unknown user ids. |
| B-24 | server | `Library/JellyfinCatalog.cs` (`VisibleTo`) | All privacy filtering depends on `GetItemList(new InternalItemsQuery(user))` enforcing library access and parental limits; cached 10 min (permission changes lag). Fails closed on exceptions. | Verify on a live server with a restricted user; shorten the TTL. |
| B-25 | web | `web/src/rows.ts:74-77`, `card.ts:184-189` | Play buttons only open the native details page (same as More Info). | Relabel or start playback via the native page/SDK. |
| B-26 | web | `web/src/style.css:12-48` | The "native restyle" is global (forces dark backgrounds, `!important` submit styling) so light themes, login and dashboard pages become unreadable; in the experimental layout the MUI AppBar is not hidden (unverified). | Scope to `.fui-root` / `html.fui-owns`; hide `.MuiAppBar-root`. |
| B-27 | web | `web/src/main.ts:345-348,224-236` | Overlay released immediately on hashchange; flash of the classic home before the target view loads (unverified). | Keep until the target `viewshow`, or fade out. |
| B-28 | web | `web/src/main.ts:345` | `#/home?fui=...` tab URLs make the hidden native home reload (5-10 requests) on every tab switch. | Use a hash path the native router does not treat as home (test). |
| B-29 | web + firetv | `web/src/nav.ts:175-185`, `util.ts filterRowsByType`; `firetv ui/FullUiShell.kt:101-119`, `FullUiViewModel.kt:352-366` | Shows/Movies are filtered recommendation rows, not library browsing. Libraries, Music, Live TV, Playlists, Favorites, a second movie library are unreachable without switching to the classic view. | Add a "Libraries / Browse" entry linking the stock destinations. |
| B-30 | web | `web/src/nav.ts:233-235`, `main.ts:316` | `nav.start()` refetches notifications on every sync (2-3 GETs per navigation), no sequence guard (stale reply can resurrect the unread badge), polls while hidden. | Fetch only on first start; sequence guard; pause when `document.hidden`. |
| B-31 | web | `web/src/trailer.ts:75-90`, `rows.ts:96-114` | A card trailer displacing the hero trailer never calls the hero's `onFail`; the hero keeps `trailer-on`, a dead mute button, and never resumes. | Per-owner `onStop` callback; pause the hero offscreen/hidden. |
| B-32 | web | `web/src/card.ts:250-256`, `rows.ts:10-11` | Accessibility: interactive buttons nested inside `role="button"` cards; `role="list"` with button children; ~800 tab stops. | Non-role card container with the art as the single button; roving tabindex. |
| B-33 | web | `web/src/main.ts:328-331`, `FullUIController.cs:70-71` | `api.status()` runs at boot even on the login page with `Token="null"`; 401 every login-page load; the accent/name is never applied. | Skip without a session, call after login; or make Status anonymous. |
| B-34 | firetv | `FullUiService.kt:92`, `ui/FullUiShell.kt:72-77` | An 8 s probe timeout (cold server Home build) sticks the stock UI for the session; `retry()` is never called; the docs' "retry after 5 minutes" does not happen. | Call `retry()` when the setting is toggled/on resume; longer timeout. |
| B-35 | firetv | `ui/HomeScreens.kt:285-291` | Hero Play steals focus on every return from details/playback or tab change; the list jumps to the top. | Request initial focus once only. |
| B-36 | firetv + server | `ui/HomeScreens.kt:340`, `ui/Components.kt:130,140`, `RecEngine.cs:405` | Duplicate row ids ("Sci-Fi"/"Sci Fi") or duplicate card ids crash Compose (`Key was already used`). | `distinctBy` on rows and cards in the mapping; make genre row ids unique. |
| B-37 | firetv | `ui/HomeScreens.kt:122-143`, `ui/Components.kt:187-231,352-396`, `FullUiTheme.kt:77` | Up to two WebViews at once; a new WebView per focused card (30-60 MB, stalls) on Fire Sticks; no circuit breaker after repeated failures. | One reusable WebView; stop the hero trailer while a row has focus; gate on `memoryClass`; disable trailers after N failures. |
| B-38 | firetv | `ui/TrailerPlayer.kt:96-148` | WebView not paused with the activity; no navigation allow-list or file/content access lock-down while `FullUiBridge` stays exposed; initial `muted` applied before page load. | Lifecycle observer; `allowFileAccess=false`, `allowContentAccess=false`, navigation allow-list; set mute in the page. |
| B-39 | firetv | `services/UpdateChecker.kt`, `firetv.yml:154-209` | The updater installs the downloaded APK with no checksum check (SHA256SUMS.txt is published); `release.downloadUrl!!` NPEs if no asset matches. | Verify SHA-256; handle null. |
| B-40 | CI | `.github/workflows/firetv.yml:203-209,31-33` | `firetv-latest` rolling release race: out-of-order runs point it at an older version; delete-then-create leaves a 404 window. | Non-cancelling concurrency group; compare versions; edit assets instead of delete/create. |
| B-41 | firetv | `WholphinDataStore.proto:208-209` | `full_ui_disabled = 16` may collide with a future upstream field. | Use a high field number (e.g. 1000). |
| B-42 | firetv | `AndroidManifest.xml:62-69`, `services/IntentService.kt:206` | Keeps `wholphin://` scheme and `...wholphin.PLAYBACK` action; collides with a stock Wholphin install; `FULLUI_NOTICE.md` claims otherwise. | Rename scheme/action or fix the docs. |

## LOW

| ID | Area | Where | Problem |
|---|---|---|---|
| B-43 | server | `Tasks/*`, `CoreServicesRegistrator.cs:24` | Only `RebuildRecsTask` is registered in DI; the other four tasks rely on Jellyfin's assembly scan. Possible duplicate or missing tasks (check Dashboard > Scheduled Tasks). unverified |
| B-44 | server | `Api/AdminController.cs:66`, `requests.html` | CSV export has no UTF-8 BOM (Excel mangles non-ASCII). |
| B-45 | server | `Configuration/settings.html` | No `TopTenWindowDays` field; Top 10 row title hard-codes "This Week"; load/save promises have no error handler (spinner can stick). |
| B-46 | server | `Discovery/TmdbClient` | v3 key travels in the query string; HttpClientFactory Debug logging could expose it. `Ollama/Test` takes an arbitrary URL (admin-only, acceptable). |
| B-47 | server | `Api/DiscoveryController.cs ToCard` vs `CardMapper` | Search cards have no badges / cached trailer key; `GetItem` has no progress for titles not in a Home row. |
| B-48 | server | misc | `ParseYouTubeKey` accepts `evilyoutu.be` (harmless); no cache headers/ETag on `fullui.js/.css`; `Jellyfin.Controller 10.11.*` floats; replayed movie reappears in Continue Watching; `HomeService.Compose` copies all signals per cache miss. |
| B-49 | server | `docs/api-contract.md` | Error bodies differ (`ProblemDetails` vs `{message}`); `Notifications/Read` with `ids: []` means "all"; Status documented as public but is `[Authorize]`. |
| B-50 | web | `nav.ts:167` | Escape always returns focus to the avatar, even when the bell menu opened. |
| B-51 | web | `spatial.ts:237-246` | Escape in the search box navigates back instead of clearing; `history.back()` can leave the app. |
| B-52 | web | `store.ts:81,98` | A second thumb/My List click during an in-flight request is dropped. |
| B-53 | web | `main.ts:268` | `history.replaceState(null, ...)` overwrites React Router state. |
| B-54 | web | `nav.ts:49-60,174-185,94` | Menu roles without arrow-key handling; "Mark all read" inside `role=menu`; the notification rebuild drops keyboard focus. |
| B-55 | web | `nav.ts:231` | Unvalidated accent color (a typo like `e50914` makes UI invisible). Validate `^#[0-9a-f]{3,8}$` on client and server. |
| B-56 | web | `util.ts:300-302` | Image URLs have no `tag`, so no long-lived caching. |
| B-57 | web | `trailer.ts:19-50` | Loads the YouTube script for every user with no admin switch; a second `<script>` after the 8 s timeout; buffering state not handled. |
| B-58 | web | `main.ts:381-383` | Boot poll never capped. |
| B-59 | web | `main.ts:224-236` | No guard against the bundle running twice (two overlays). |
| B-60 | web | `pages.ts:333` | Search placeholder promises AI in keyword mode. |
| B-61 | web | `*.ts`, `style.css` | Modern-only APIs (`replaceChildren`, `inset`, `aspect-ratio`...) break old webOS 4/5 and Tizen 5.x; no `env(safe-area-inset-*)`. |
| B-62 | web | `main.ts:239` | 60 s Home cache stale after playback or My List change. |
| B-63 | web | `card.ts:315-352` | Hover/trailer timers not cancelled on page disposal; detached card can swallow a Back press. |
| B-64 | firetv | `FullUiClient.kt:171` | `body.string()` outside the try, so an IOException escapes as a raw exception; `Call.await` can leak a response on cancel. |
| B-65 | firetv | `FeatureDetector` | Prefetched Home payload never expires. |
| B-66 | firetv | `FullUiViewModel.kt` | Rapid Rate/MyList/Vote calls can complete out of order; `toggleMyList` reloads Home after 500 ms and replaces the hero under focus. |
| B-67 | firetv | `FullUiHomeShell` | Back on the Home tab exits the app (stock moves to the drawer first). |
| B-68 | firetv | `HomeContent` | `focusRestorer()` sends Down from the hero to the last row; notifications dialog does not restore focus (unverified). |
| B-69 | firetv | `ImageUrls.kt` | No `tag` param; portrait posters cropped into 16:9 when there is no backdrop; `quality=90` at 640 px is more than needed. |
| B-70 | firetv | manifest | Cleartext and user CAs allowed globally (acceptable for LAN, upstream's choice; also applies to the WebView). |
| B-71 | firetv + CI | `versionCode` formula, `firetv.yml` | `versionCode = major*10000+minor*100+patch` collides at patch > 99; secrets visible to every step of the `apk` job; runs with secrets on same-repo PRs; a tag on any commit publishes a release. |
| B-72 | firetv | docs | Fork has no top-level LICENSE; release notes do not link source (GPL). New files lack headers. |
| B-73 | firetv | UI | 8-9 sp text is too small on a 1080p TV. |
| B-74 | firetv | `playbackFor` | `getEpisodes(limit=1)` may return a special/virtual episode. |

## Test-quality gaps (fix alongside the bugs above)

- No JSON-casing test (hid B-02); mocks too lenient: `getUrl('')` accepted (hid B-01), camelCase Search/Notifications, non-Guid ids accepted, 200 `{}` instead of 204, wrong error shape.
- No reflection test that every endpoint carries `[Authorize]`; no DI-resolution test (a working smoke test exists in the reviewer's scratchpad, add it to the repo); no `PluginStore` persistence/corruption/race tests; no `EventTracker`, `JellyfinCatalog`, `WebInjection.Transform`, controllers (Rate/MyList/Item for invisible items), `MaterializePlaylistsTask`, `SafeApi` tests; `ComingSoonRow` path untested because `Plugin.Instance` is null in `HomeServiceTests`.
- Weak tests: `ThumbsDown_LowersSimilarTitles` contains a tautology; `Rows_AreInContractOrder` filters on a meaningless id prefix; `UnknownUser_GetsPopularityOnlyAndNothingHidden` does not check what its name says; `FakeFactory` ignores the client name; `components.test.ts` never builds `.fui-info` (info-panel XSS surface untested); the test named "a failing Shows page" actually tests MyServer; no Sign out / user switch / base-path / experimental-layout / pre-login e2e cases; `web/e2e-artifacts/*.png` are tracked and dirty the tree on every run.
- JVM tests for Fire TV run off the main thread so they cannot see B-04.

## Installer / CI review

Pending: to be added when that review reports.
