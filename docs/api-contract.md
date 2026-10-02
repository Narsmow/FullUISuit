# FullUI API contract (all routes under `/FullUI`, JSON camelCase, auth = normal Jellyfin token)

DTOs: `server/Jellyfin.Plugin.FullUI/Api/Dtos.cs`. Storage model: `Data/Models.cs` (PluginStore). Library snapshot: `Library/CatalogItem.cs` (ICatalog).
User id inside controllers: `User.FindFirst("Jellyfin-UserId")` (a Guid). Admin endpoints: `[Authorize(Policy = "RequiresElevation")]`. Never take a user id from the client.

| Route | Purpose |
|---|---|
| GET `Home` | `HomeResponse` (rows in display order; `comingsoon` row carries `comingSoon[]`, others `items[]`) |
| POST `Rate` `{itemId, rating}` | rating -1/0/1/2 (0 clears). 2 (love) also sets Jellyfin favorite. Invalidates user's home cache |
| POST `MyList` `{itemId, add}` | add/remove from My List |
| GET `Item/{id}` | `ItemCard` for one title (More Info) |
| GET `MyServer` | `{ continueWatching: ItemCard[], myList: ItemCard[], wanted: ComingSoonCard[] }` |
| GET `ComingSoon` | `{ cards: ComingSoonCard[] }` for the current user (empty when TMDB key unset) |
| POST `Vote` `{tmdbId, mediaType, vote, title,...}` | vote 1 = I want this, -1 = not for me, 0 = clear |
| GET `Search?q=` | `SearchResponse` (mode `semantic` when Ollama enabled and reachable, else `keyword`) |
| GET `Notifications` / POST `Notifications/Read` | in-UI bell. `{items: NotificationDto[]}` / `MarkReadRequest` |
| GET `Status` | public-ish branding/status (already exists) |
| POST `Tmdb/Test`, POST `Ollama/Test` | admin settings page buttons |
| GET `Admin/Requests?sort=votes\|recent` | admin: aggregated requests `{tmdbId, mediaType, title, posterPath, releaseDate, wantCount, voters[], status, note, tmdbUrl, lastVoteAt}` |
| POST `Admin/Requests/Status` `{tmdbId, mediaType, status, note}` | status: Requested, Getting it, Added |
| GET `Admin/Requests.csv` | CSV export |
| POST `Admin/Rebuild` | admin: rebuild caches now |

Images (client side): `/Items/{id}/Images/Primary|Backdrop|Logo?maxWidth=...`; TMDB posters `https://image.tmdb.org/t/p/w342{path}`.
Trailers: `trailerKey` is a YouTube video id.
Rows order (server decides): continue, toppicks, top10(movies), because x3, trending, comingsoon, mylist, genre x3, top10(shows), newseasons, recent, hidden, again.
Vote privacy: users only ever see their own votes; only admin endpoints expose totals/voters.
