# Contract fixtures

Real serialized responses of the FullUI server, one file per client-facing response. The web mocks and the TypeScript types must be built from these files, never hand-written.

They are produced by `server/Jellyfin.Plugin.FullUI.ApiTests/ContractFixtureTests.cs`, which runs the real controllers and services over a fixed household ("Home Cinema", 34 titles, 4 users, clock fixed at 2026-06-01 12:00 UTC) and serializes through `SafeApi.Json`, the same serializer options as production. The test fails when a committed file differs from what the code produces.

Update after an intentional server change:

```
cd server
FULLUI_UPDATE_FIXTURES=1 dotnet test Jellyfin.Plugin.FullUI.ApiTests --filter ContractFixtureTests
```

Review the diff and commit it with the change.

| File | Route |
|---|---|
| `status.json` | GET `Status` |
| `home.json` | GET `Home` (viewer with history: continue with `seriesLabel`/`minutesLeft`, toppicks with `matchPercent`/`reason`, because, top10 with `rank`, trending, mylist, comingsoon with `comingSoon[]`, genre, newseasons, recent, again) |
| `my-server.json` | GET `MyServer` |
| `item.json` | GET `Item/{id}` |
| `item-details-series.json`, `item-details-movie.json` | GET `Item/{id}/Details` |
| `coming-soon.json` | GET `ComingSoon` (`cards` upcoming, `recommended`, `reminded` flags) |
| `new-popular.json` | GET `NewPopular` |
| `reminders.json` | GET `Reminders` |
| `onboarding.json`, `onboarding-not-eligible.json` | GET `Onboarding` (new user / household viewer) |
| `search-keyword.json`, `search-semantic.json` | GET `Search?q=` (both modes). The response has no `groups` today; people and genre groups are served by `Search/Suggest` |
| `search-suggest.json`, `search-suggest-people.json` | GET `Search/Suggest?q=` (typo-tolerant titles / people) |
| `notifications.json` | GET `Notifications` |
| `events-request.json`, `events-response.json` | POST `Events` |
| `admin-metrics.json`, `admin-health.json` | GET `Admin/Metrics`, GET `Admin/Health` |
| `error-problem-4xx.json`, `error-problem-5xx.json` | RFC 7807 bodies (Home/Rate/MyList/Item/MyServer routes) |
| `error-message-4xx.json`, `error-message-5xx.json` | `{ "message" }` bodies (all other routes) |

Notes
- `admin-health.json` pins `pluginVersion` and the file sizes (they change with every release and disk flush); everything else is untouched.
- Status 401/404 bodies are not fixtures: an empty body or an ASP.NET problem document with a varying trace id. Clients must key on the HTTP status.
- Properties whose value is `null` are sent as `null` (never omitted), except `recommended` in `coming-soon.json`, which is omitted when the admin switched it off.
