# FullUI API contract (all routes under `/FullUI`, JSON camelCase, auth = normal Jellyfin token)

DTOs: `server/Jellyfin.Plugin.FullUI/Api/Dtos.cs`. Storage model: `Data/Models.cs` (PluginStore). Library snapshot: `Library/CatalogItem.cs` (ICatalog).
User id inside controllers: `User.FindFirst("Jellyfin-UserId")` (a Guid). Admin endpoints: `[Authorize(Policy = "RequiresElevation")]`. Never take a user id from the client.

**JSON casing.** Jellyfin's own MVC formatter is PascalCase, so every FullUI action serializes explicitly through `SafeApi.Json` (camelCase) - including the Admin routes, the `Tmdb/Test` and `Ollama/Test` buttons and all error bodies. A test renders every action through an MVC pipeline configured like Jellyfin's and fails on any capitalised key. Request bodies are read case-insensitively. Guids in bodies are written in the default dashed form.

| Route | Purpose |
|---|---|
| GET `Home` | `HomeResponse` (rows in display order; `comingsoon` row carries `comingSoon[]`, others `items[]`). Row ids are unique (clients key lists on them). **5xx** (problem body) when the home cannot be built; an empty `rows` array with 200 means a genuinely empty library, never a failure |
| POST `Rate` `{itemId, rating}` | rating -1/0/1/2 (0 clears). 2 (love) also sets Jellyfin favorite. Invalidates user's home cache. 404 when the user cannot see the item |
| POST `MyList` `{itemId, add}` | add/remove from My List. 404 when the user cannot see the item |
| GET `Item/{id}` | `ItemCard` for one title (More Info), including resume `progress`. 404 when missing or not visible |
| GET `MyServer` | `{ continueWatching: ItemCard[], myList: ItemCard[], wanted: ComingSoonCard[] }` (5xx on failure, like `Home`) |
| GET `ComingSoon` | `{ cards: ComingSoonCard[] }` for the current user (empty when TMDB key unset). Same cards as the Home row: titles already in the library are dropped, "not for me" titles stay hidden |
| POST `Vote` `{tmdbId, mediaType, vote, title,...}` | vote 1 = I want this, -1 = not for me, 0 = clear. At most 500 votes per user (400 `{message}` beyond that; changing or clearing is always allowed) |
| GET `Search?q=` | `SearchResponse` `{mode, items}` (mode `semantic` when Ollama is enabled and reachable, else `keyword`). Cards carry badges and trailer keys like Home cards. With Ollama on, titles that have no usable vector yet are still found by name |
| GET `Notifications` / POST `Notifications/Read` | in-UI bell. `{items: NotificationDto[]}` / `MarkReadRequest {ids?: guid[]}`. **A missing body, `ids: null` or `ids: []` marks ALL of the caller's notifications read**; a non-empty list marks only those. Notifications older than 90 days (and beyond the newest 200 per user) are pruned |
| GET `Status` | `{serverName, accentColor, tmdbConfigured, ollamaEnabled, trailersEnabled, webInjected}` for a **signed-in** user (`[Authorize]`; it is NOT public, so clients must not call it on the login page). `accentColor` is always a valid `#hex` (invalid admin input falls back to `#e50914`). Never contains the TMDB key |
| POST `Tmdb/Test`, POST `Ollama/Test` | admin settings page buttons; body `{ok, message}` (200 even when the test fails; `ok=false` carries the reason) |
| GET `Admin/Requests?sort=votes\|recent` | admin: aggregated requests `[{tmdbId, mediaType, title, posterPath, releaseDate, wantCount, voters[], status, note, tmdbUrl, lastVoteAt}]` |
| POST `Admin/Requests/Status` `{tmdbId, mediaType, status, note}` | status: Requested, Getting it, Added |
| GET `Admin/Requests.csv` | CSV export, UTF-8 **with byte order mark** (Excel shows accents correctly) |
| POST `Admin/Rebuild` | admin: rebuild caches now (202, runs in the background) |
| GET `Admin/Injection` | admin: `{registered, attempts, message}` - whether the reskin is hooked into the Jellyfin web page (via the File Transformation plugin). The settings page shows `message` in plain English |
| GET `web/fullui.js`, `web/fullui.css` | anonymous static bundle. `Cache-Control: no-cache` + `ETag`: browsers revalidate and get 304 when unchanged |

**Errors.** 4xx validation errors from FullUI endpoints are `{ "message": "..." }`; unexpected failures are 500 with the same shape (`SafeApi`) or, on the Home/Rate/MyList/Item/MyServer routes, an RFC 7807 problem (`{type?, title, status, detail}`, `application/problem+json`). Neither ever contains exception text, paths, URLs or keys; details go to the server log. Clients should key on the HTTP status and show their own friendly text.

**ItemCard** extras: `imageTag` (optional string) is a short token that changes when the item's primary image changes; clients add `&tag={imageTag}` to `/Items/{id}/Images/...` URLs so images can be cached for a long time. `badges`, `trailerKey`, `progress`, `rank` as before.

**Top 10 titles** follow the `TopTenWindowDays` setting: 7 days = "Top 10 Movies on {Server} This Week", 1 = "... Today", otherwise "... in the Last N Days".

**Home composition notes.** Continue Watching also lists a series whose latest episode was finished when Jellyfin's Next Up has another episode ready (progress `null`). On first start the plugin imports existing Jellyfin watch history (played, resume points, favorites -> Love) once per user, so Home is not cold. The per-user "visible titles" list (the privacy filter behind every row, search, rating and My List) is cached for 2 minutes; permission or parental-rating changes in Jellyfin take at most that long to apply. Fails closed: if the list cannot be read, nothing is shown.

Images (client side): `/Items/{id}/Images/Primary|Backdrop|Logo?maxWidth=...`; TMDB posters `https://image.tmdb.org/t/p/w342{path}`.
Trailers: `trailerKey` is a YouTube video id.
Rows order (server decides): continue, toppicks, top10(movies), collection x0-2 (`collection-{id}`, "Next in {BoxSet}"), because x3, trending, comingsoon, mylist, genre x3, top10(shows), newseasons, recent, hidden, again.

**Recommendation notes.** Every row passes one eligibility rule: titles the user thumbed down, hid, finished (except in Watch Again), dropped (a show they started and abandoned) or cannot see never appear. A series counts as finished only when the user has watched every episode; one finished episode no longer marks the whole show. `newseasons` ("New Episodes") lists followed shows with a genuinely new season or new episodes since the user last caught up. Recommendation rows (toppicks, because, genre, hidden, collection) never share a title; Trending, charts, Continue, My List, Recently Added and Watch Again are exempt. Minimum row size shrinks from 5 to 3 for small libraries. Duplicate editions (same TMDB id) collapse to one card. Cards carry `matchPercent` (1-99, null on cold start), `reason` (plain English), and for Continue Watching `seriesLabel` ("S2:E5") and `minutesLeft`. Users with a restrictive parental cap (heuristic: they see none of the library's mature-rated titles) are left out of Top 10, Trending and collaborative filtering when `ExcludeKidsFromSharedSignals` is on.
Vote privacy: users only ever see their own votes; only admin endpoints expose totals/voters.

## Server notes

- **Hooking into the web page** is done by a hosted service after Jellyfin starts (not while services are registered: File Transformation is not constructed yet at that point). It retries a few times over about 15 minutes and logs each outcome (`FullUI: web interface hooked in ...` / `... NOT active ...`). The injected tags use page-relative URLs (`../FullUI/web/fullui.js`), so a Jellyfin Base URL or reverse-proxy sub-path works.
- **Scheduled tasks** are found by Jellyfin's assembly scan; they are intentionally not also registered in DI.
- **Jellyfin.Controller** stays at `10.11.*` (floating). Pinning to `10.11.0` does not compile: `IUserManager.Users/UsersIds` (10.11.0) became `GetUsers()/GetUsersIds()` in later 10.11.x and the plugin uses the newer methods. A plugin built against a late 10.11.x may therefore fail on an early 10.11.x server; set `targetAbi` in the manifest accordingly and verify on the oldest server you intend to support.
- **Storage**: `fullui/store.json` (small) and `fullui/embeddings.json` (AI vectors, loaded on first use). Saves are debounced (5 s after the first unsaved change), written atomically, and never block readers while serializing. A corrupt file is kept as `.bad` and the plugin starts empty.
