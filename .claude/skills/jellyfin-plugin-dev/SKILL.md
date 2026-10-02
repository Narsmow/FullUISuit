---
name: jellyfin-plugin-dev
description: How to build, install, debug and extend the FullUI Jellyfin 10.11 plugin (C# server plugin plus the TypeScript web bundle injected into jellyfin-web). Use whenever working in server/ or web/, touching the plugin settings page, the /FullUI REST API, File Transformation injection, TMDB or Ollama settings, plugin versioning, or when a change must be verified against a running Jellyfin. Also use when the user mentions Jellyfin plugins, jellyfin-web, Fire TV/Android/iOS client limits, or CI for this repo, even if they don't name the skill.
---

# FullUI Jellyfin plugin development

## Why this matters
Plugins only change **jellyfin-web**. Native clients (official Android TV/Fire TV app, Swiftfin) never load it, so anything that must show there has to go through the REST API plus our own Fire TV app (`firetv/`) or server-written playlists. Check which client a feature targets before building it.

## Layout
- `server/Jellyfin.Plugin.FullUI/` C# plugin (net9.0, `Jellyfin.Controller` pinned to **10.11.6**, the oldest supported server; supported range 10.11.6 and later).
  - `Plugin.cs` plugin id `7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57` (also used in `settings.html` and `WebInjection.cs`; keep all three identical).
  - `Configuration/PluginConfiguration.cs` stored settings (TMDB key, Ollama, branding, default accent `#e5383b`, switches `TrailersEnabled`, `PlayerAssistEnabled`, `ExcludeKidsFromSharedSignals`, `CollectInteractionMetrics`, `ShowRecommendedNotInLibrary`). Adding a setting means: property here, field in `settings.html` load + save (four switches still have no field: add one rather than telling admins to edit the XML), and the `Status` endpoint only if clients need it. Three admin pages: `settings.html`, `requests.html` (Most Requested), `health.html`; all carry the TMDB attribution sentence.
  - `Api/FullUIController.cs` REST under `/FullUI/*`. Admin-only endpoints use `[Authorize(Policy = "RequiresElevation")]`; per-user endpoints use `[Authorize]` and must derive the user from the auth token, never from a client-supplied id.
  - `WebInjection.cs` + `WebInjectionHostedService.cs` register an `index.html` transformation with the File Transformation plugin by reflection, from a hosted service AFTER startup with retries (Jellyfin runs plugin service registrators before plugins are constructed, and File Transformation's static Instance is only set in its constructor, so registering inside `RegisterServices` silently fails). The injected URLs are page-relative so Jellyfin base paths work; the outcome is logged and shown on the settings page (`GET FullUI/Admin/Injection`).
- `web/` Vite + TypeScript. Builds one IIFE `dist/fullui.js` plus `dist/fullui.css`, which CI copies into `server/.../Web/` as embedded resources served at `/FullUI/web/fullui.{js,css}`.

## Routes and services added in the Netflix-gap work
Read `docs/api-contract.md` for the exact shapes. Where things live:
- **Health** (`Ops/HealthService.cs`, `Ops/TaskRunLog.cs`, `Ops/ErrorLog.cs`, `Api/AdminOpsController.cs`): `GET Admin/Health` for the Health page. Every scheduled task and background service records its run through `ITaskRunLog` (`TaskRunRecorderService` also listens to Jellyfin's task manager); new tasks must be added to `HealthService.KnownTasks`. `ErrorLog.Add` takes friendly, scrubbed text only (no keys, URLs or paths).
- **Backup / purge** (`Ops/DataPortability.cs`): `GET Admin/Export`, `POST Admin/Import` (dry run is the default), `POST Admin/Purge` (two-step token). Adding stored data means adding it to export, import validation and purge, and bumping `StoreMigrations.CurrentVersion` with a migration (`Data/StoreMigrations.cs`; old files are upgraded in memory and a `.v1.bak` copy is kept).
- **Metrics / events** (`Metrics/InteractionLog.cs`, `Metrics/MetricsService.cs`, `Api/MetricsController.cs`): `POST Events` (batched, validated, per-user rate limit, user from the token only; discarded when `CollectInteractionMetrics` is off), `GET Admin/Metrics` (aggregates only). Log file `fullui/events.jsonl`, separate from `store.json`. `MetricsService` is also the `IRowEngagementProvider` that orders rows per user. There is no Dashboard page for it yet.
- **Onboarding / hidden titles / search suggestions** (`Discovery/OnboardingService.cs`, `HiddenItems.cs`, `SearchSuggest.cs`, `Api/OnboardingController.cs`): `GET/POST Onboarding`, `POST ContinueWatching/Hide|Unhide`, `GET Search/Suggest`.
- **New & Popular / reminders** (`Discovery/NewPopular.cs`, `ReminderService.cs`, `Api/NewPopularController.cs`): `GET NewPopular`, `POST Remind`, `GET Reminders`; the daily task `Tasks/DailyHousekeepingTask.cs` (06:00) sends reminders and prunes old events.
- **Events/backfill** (`Events/EventTracker.cs`, `Events/PlaybackBackfill.cs`): live play signals (season and episode recorded) and the one-time import of Jellyfin's own history per user.
- **Engine** (`Recs/`): pure, no Jellyfin types, so it is tested by `Jellyfin.Plugin.FullUI.Tests` including `HouseholdSimulatorTests`. Extension points are interfaces registered in `FeatureServicesRegistrator` / `DiscoveryServicesRegistrator` (`IHiddenItems`, `IRowEngagementProvider`, `ITrendingProvider`, `ICastIndex`, `IHomeInvalidator`); the replaceable ones use `TryAdd`.
- **Items with `matchPercent` / `reason`** come from `RecEngine` through `CardMapper`; never compute them in a controller.

## Contract fixtures
`docs/contract-fixtures/*.json` are real serialized responses produced by the server code. A server test regenerates them and fails when the committed copies differ; the web mocks (`web/e2e/mock-server.ts`) and the TypeScript types in `web/src/types.ts` must be built from these files, never hand-written. When a response shape changes: change the server, run the test to regenerate the fixtures, update `docs/api-contract.md`, then update web types and mocks from the fixtures. This is what stops mocks being more lenient than the real server (the cause of B-01 and B-02).

## Secrets rule
The TMDB key lives only in `PluginConfiguration` on the server. Never return it from any endpoint or put it in the bundle. Clients call TMDB through the plugin API. `Status` exposes only `tmdbConfigured: bool`.

## JSON rule
Jellyfin's MVC formatter is PascalCase. Every action must return through `SafeApi.Json(...)` (camelCase); ApiTests asserts lowercase keys through a Jellyfin-like pipeline. Never return a raw record from an action.

## Build and test
- Web (works anywhere with Node): `cd web && npm ci && npm run typecheck && npm test && npm run build`.
- Server tests: `cd server && dotnet test` runs Tests (engine, household simulator), DiscoveryTests (TMDB, requests, reminders, Coming Soon) and ApiTests (DI-resolution smoke test, [Authorize] reflection test, PluginStore tests, JSON casing, health/backup/purge). Always run `tools/abi-matrix.sh` after touching a Jellyfin API call (see the `jellyfin-version-compat` skill).
- Plugin: `dotnet build server/Jellyfin.Plugin.FullUI -c Release`. The Claude cloud sandbox cannot download the .NET SDK (builds.dotnet.microsoft.com is blocked), so C# is verified by GitHub Actions (`.github/workflows/ci.yml`). Say plainly when C# was not compiled locally, and read the CI result instead of assuming.
- Bundle order matters: web first, then copy into `Web/`, then `dotnet build` (csproj embeds `Web/fullui.*` only if they exist).

## Installing on a real server (Windows/Linux, no Docker)
1. Install **File Transformation** (IAmParadox27, manifest `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`). It supports one Jellyfin version at a time, so match it to the server's exact 10.11.x.
2. Put `Jellyfin.Plugin.FullUI.dll` in `<data>/plugins/FullUI/`, restart Jellyfin.
3. Dashboard → Plugins → FullUI: paste the TMDB key, click **Test connection**.
4. Hard-refresh the browser (service worker/cache otherwise serves old `index.html`).

## Known uncertainties (verify, don't assume)
- The File Transformation call in `WebInjection.TryRegister` (type `Jellyfin.Plugin.FileTransformation.PluginInterface`, method `RegisterTransformation`, JSON payload keys, and the `{ "contents": ... }` callback shape) was written from that plugin's README and has not been run against a real server. If the screen never shows, read the Health page / `GET Admin/Injection` message and the server log lines `FullUI: web interface hooked in ...` / `... NOT active ...`, then inspect that plugin's current README/source.
- Whether `Newtonsoft.Json` types are shared with File Transformation's load context (the outcome is logged, not swallowed).
- Everything listed under "Needs a real Jellyfin / Windows PC / device" at the top of `docs/BUGS.md` (Media Segments data, `.btnNextTrack`, restricted users, YouTube playback, scheduled tasks showing once). Say plainly when a change touches one of these and was not proven on a real server.

## jellyfin-web quirks
- It is a single-page app: scripts run once, pages swap via `viewshow`/hashchange. Mount code must be idempotent (see `mountBanner`) and must not assume a fresh DOM.
- `window.ApiClient` appears after startup; poll for it, then call `ApiClient.getUrl(...)` and send the token as `Authorization: MediaBrowser Token="..."`.
- The official Android and iOS "Mobile" apps and the webOS/Tizen apps wrap jellyfin-web, so the web bundle reaches them; TV remotes need spatial D-pad navigation.

## Definition of done for a change
Web tests pass and bundle builds, C# changes reviewed against the CI build, no secret leaks (grep for `TmdbApiKey` in responses), any new endpoint has the correct authorization attribute and is documented in `docs/api-contract.md` with a contract fixture, new stored data is covered by export / import / purge and a store migration, new user-visible web strings go through `web/src/i18n.ts`, and the docs (`docs/USER_GUIDE.md`, `docs/ADMIN_GUIDE.md`, `docs/BUGS.md`) are updated for anything an owner would notice.

## Local .NET toolchain (verified working in the Claude cloud sandbox)
`dotnet.microsoft.com` hosts are blocked, but apt works: `apt-get update && apt-get install -y dotnet-sdk-10.0`. The .NET 10 SDK builds the `net9.0` plugin and restores `Jellyfin.Controller` from api.nuget.org, so C# can be compiled locally (`dotnet build -c Release`) and unit tests run with `dotnet test`. Use isolated clone/worktree directories when several agents build in parallel (shared `obj/` collides).
