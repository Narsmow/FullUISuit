---
name: jellyfin-plugin-dev
description: How to build, install, debug and extend the FullUI Jellyfin 10.11 plugin (C# server plugin plus the TypeScript web bundle injected into jellyfin-web). Use whenever working in server/ or web/, touching the plugin settings page, the /FullUI REST API, File Transformation injection, TMDB or Ollama settings, plugin versioning, or when a change must be verified against a running Jellyfin. Also use when the user mentions Jellyfin plugins, jellyfin-web, Fire TV/Android/iOS client limits, or CI for this repo, even if they don't name the skill.
---

# FullUI Jellyfin plugin development

## Why this matters
Plugins only change **jellyfin-web**. Native clients (official Android TV/Fire TV app, Swiftfin) never load it, so anything that must show there has to go through the REST API plus our own Fire TV app (`firetv/`) or server-written playlists. Check which client a feature targets before building it.

## Layout
- `server/Jellyfin.Plugin.FullUI/` C# plugin (net9.0, `Jellyfin.Controller` 10.11.*).
  - `Plugin.cs` plugin id `7c3f2d9a-5b1e-4a86-9d0c-2f8e6b4a1c57` (also used in `settings.html` and `WebInjection.cs`; keep all three identical).
  - `Configuration/PluginConfiguration.cs` stored settings (TMDB key, Ollama, branding). Adding a setting means: property here, field in `settings.html` load + save, and `Status` endpoint only if clients need it.
  - `Api/FullUIController.cs` REST under `/FullUI/*`. Admin-only endpoints use `[Authorize(Policy = "RequiresElevation")]`; per-user endpoints use `[Authorize]` and must derive the user from the auth token, never from a client-supplied id.
  - `WebInjection.cs` registers an `index.html` transformation with the File Transformation plugin by reflection.
- `web/` Vite + TypeScript. Builds one IIFE `dist/fullui.js` plus `dist/fullui.css`, which CI copies into `server/.../Web/` as embedded resources served at `/FullUI/web/fullui.{js,css}`.

## Secrets rule
The TMDB key lives only in `PluginConfiguration` on the server. Never return it from any endpoint or put it in the bundle. Clients call TMDB through the plugin API. `Status` exposes only `tmdbConfigured: bool`.

## Build and test
- Web (works anywhere with Node): `cd web && npm ci && npm run typecheck && npm test && npm run build`.
- Plugin: `dotnet build server/Jellyfin.Plugin.FullUI -c Release`. The Claude cloud sandbox cannot download the .NET SDK (builds.dotnet.microsoft.com is blocked), so C# is verified by GitHub Actions (`.github/workflows/ci.yml`). Say plainly when C# was not compiled locally, and read the CI result instead of assuming.
- Bundle order matters: web first, then copy into `Web/`, then `dotnet build` (csproj embeds `Web/fullui.*` only if they exist).

## Installing on a real server (Windows/Linux, no Docker)
1. Install **File Transformation** (IAmParadox27, manifest `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`). It supports one Jellyfin version at a time, so match it to the server's exact 10.11.x.
2. Put `Jellyfin.Plugin.FullUI.dll` in `<data>/plugins/FullUI/`, restart Jellyfin.
3. Dashboard → Plugins → FullUI: paste the TMDB key, click **Test connection**.
4. Hard-refresh the browser (service worker/cache otherwise serves old `index.html`).

## Known uncertainties (verify, don't assume)
- The File Transformation call in `WebInjection.TryRegister` (type `Jellyfin.Plugin.FileTransformation.PluginInterface`, method `RegisterTransformation`, JSON payload keys, and the `{ "contents": ... }` callback shape) was written from that plugin's README and has not been run. If the banner never shows, inspect that plugin's current README/source first.
- Whether `Newtonsoft.Json` types are shared with File Transformation's load context. If registration silently returns false, log the exception instead of swallowing it.

## jellyfin-web quirks
- It is a single-page app: scripts run once, pages swap via `viewshow`/hashchange. Mount code must be idempotent (see `mountBanner`) and must not assume a fresh DOM.
- `window.ApiClient` appears after startup; poll for it, then call `ApiClient.getUrl(...)` and send the token as `Authorization: MediaBrowser Token="..."`.
- The official Android and iOS "Mobile" apps and the webOS/Tizen apps wrap jellyfin-web, so the web bundle reaches them; TV remotes need spatial D-pad navigation.

## Definition of done for a change
Web tests pass and bundle builds, C# changes reviewed against the CI build, no secret leaks (grep for `TmdbApiKey` in responses), and any new endpoint has the correct authorization attribute.
