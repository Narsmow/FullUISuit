# FullUISuit

Netflix-style UI plus personalized rows and suggestions for a personal Jellyfin 10.11 server.

- `server/` C# Jellyfin plugin (settings page incl. TMDB API key, REST API, web injection)
- `web/` TypeScript bundle injected into jellyfin-web (desktop, Android/iOS apps, webOS/Tizen)
- `firetv/` Wholphin fork, sideloaded APK for Fire TV (built and published by `.github/workflows/firetv.yml`)

## Install

**Users (Windows or Linux Jellyfin 10.11.6 or newer, no Docker, no coding):** download one file from the
[latest release](https://github.com/Narsmow/FullUISuit/releases/latest) and run it.

- Windows 10/11: `FullUI-Installer.cmd`, double-click it.
- Linux/macOS: `FullUI-Installer.sh`, run `bash FullUI-Installer.sh`.

It signs in to your running Jellyfin (admin account), adds the plugin repositories, installs File Transformation and FullUI,
restarts Jellyfin, checks everything is active, and optionally sets up TMDB and Ollama. File Transformation is a third-party
plugin (its plugin list is added to Jellyfin), and Ollama, if you choose it, is Ollama's own installer; docs/INSTALL.md says exactly
what is downloaded. Step-by-step guide,
troubleshooting, Fire TV sideload, update and uninstall: [docs/INSTALL.md](docs/INSTALL.md).
Source and tests for the installer: `installer/` (`bash installer/tests/run-tests.sh`).

Manual plugin repository URL (Dashboard > Plugins > Repositories):
`https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json`

## Install (dev, manual)
1. Install the **File Transformation** plugin (IAmParadox27) on Jellyfin.
2. Build: `cd web && npm ci && npm run build`, copy `dist/*` to `server/Jellyfin.Plugin.FullUI/Web/`, then `dotnet build -c Release server/Jellyfin.Plugin.FullUI`.
3. Copy `Jellyfin.Plugin.FullUI.dll` into `<jellyfin data>/plugins/FullUI/` and restart.
4. Dashboard → Plugins → FullUI → paste your TMDB API key and click Test connection.
