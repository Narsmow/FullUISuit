# FullUISuit

Netflix-style UI plus personalized rows and suggestions for a personal Jellyfin 10.11 server.

- `server/` C# Jellyfin plugin (settings page incl. TMDB API key, REST API, web injection)
- `web/` TypeScript bundle injected into jellyfin-web (desktop, Android/iOS apps, webOS/Tizen)
- `firetv/` (planned) Wholphin fork, sideloaded APK for Fire TV

## Install (dev)
1. Install the **File Transformation** plugin (IAmParadox27) on Jellyfin.
2. Build: `cd web && npm ci && npm run build`, copy `dist/*` to `server/Jellyfin.Plugin.FullUI/Web/`, then `dotnet build -c Release server/Jellyfin.Plugin.FullUI`.
3. Copy `Jellyfin.Plugin.FullUI.dll` into `<jellyfin data>/plugins/FullUI/` and restart.
4. Dashboard → Plugins → FullUI → paste your TMDB API key and click Test connection.
