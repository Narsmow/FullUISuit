# FullUI for Fire TV: license notice and change log

This directory is a **derivative work of [Wholphin](https://github.com/damontecres/Wholphin)**
(Copyright the Wholphin authors, https://github.com/damontecres/Wholphin), an Android TV client
for Jellyfin written in Kotlin and Jetpack Compose.

* Wholphin is licensed under the **GNU General Public License, version 2 (GPL-2.0)**. The full
  license text is kept unchanged in [`LICENSE`](LICENSE).
* This derivative is therefore **also distributed under GPL-2.0**. Anyone who receives the
  APK is entitled to the complete corresponding source, which is this directory plus the rest of the
  repository it lives in (https://github.com/narsmow/FullUISuit).
* All upstream copyright and attribution notices in the source files are kept.
* Upstream snapshot this fork started from: `damontecres/Wholphin` default branch, commit
  `b0d270380ddea2022e8ca18ecbf448ac6c5692ba`.
* "Wholphin" and its logo belong to their authors. This fork is not affiliated with or endorsed
  by the Wholphin project; please do not report problems with this build upstream.

## What changed relative to upstream

Kotlin package names (`com.github.damontecres.wholphin...`) are intentionally **kept** so that
upstream changes can still be merged; only user-visible identity changed.

### Renames (identity)

| What | Upstream | FullUI |
|---|---|---|
| `applicationId` | `com.github.damontecres.wholphin` | `dev.fulluisuit.tv` (debug builds: `dev.fulluisuit.tv.debug`) |
| App name / launcher label (`app_name`, `app_name_long`) | Wholphin | FullUI |
| Crash dialog title and text (`WholphinApplication.kt`) | "Wholphin ..." | "FullUI ..." |
| Update toast string (`updated_toast`) | "Wholphin updated to %s" | "FullUI updated to %s" |
| Release asset base name used by the in-app updater (`UpdateChecker.ASSET_NAME`) | `Wholphin` | `FullUI` |
| Built APK file name (`app/build.gradle.kts`) | `Wholphin-<flavor>-...apk` | `FullUI-<flavor>-...apk` |
| Default update URL (`AppPreference.UpdateUrl`) | `https://api.github.com/repos/damontecres/Wholphin/releases/latest` | `https://api.github.com/repos/narsmow/FullUISuit/releases/tags/firetv-latest` |
| Release-notes lookup (`UpdateChecker.getRelease`) | `.../damontecres/Wholphin/releases/tags/vX.Y.Z` | `.../narsmow/FullUISuit/releases/tags/firetv-vX.Y.Z` |
| Version name/code | derived from `git describe` / tag count | `-PfulluiVersionName` / `-PfulluiVersionCode` (or env `FULLUI_VERSION_NAME` / `FULLUI_VERSION_CODE`), default `0.1.0` / `1` |
| Update-checker unit test fixtures (`TestUpdateChecker`, `release_develop.json`) | `Wholphin-release*.apk` | `FullUI-release*.apk` |

Not changed (still Wholphin's): launcher icon / banner artwork, internal class and theme names
(`WholphinApplication`, `Theme.Wholphin`, ...), the `wholphin://` intent scheme and the
`com.github.damontecres.wholphin.PLAYBACK` intent action, Room database / DataStore file names.
Because `applicationId` differs, none of these collide with an installed Wholphin.

### Build

* `app/build.gradle.kts`: release builds fall back to the Android **debug** signing key when
  no CI keystore is configured, so the APK is always installable (see `BUILDING.md`).
* New module `:fullui-core` (pure JVM) and a JVM-only build in `jvm-checks/` that can run its tests
  without the Android SDK. Version catalog gained `kotlinx-coroutines-core` and `okhttp-mockwebserver`.
* Upstream's `.github/` directory was removed from this copy (it only works at a repository
  root); the FullUI workflow is `.github/workflows/firetv.yml` at the monorepo root.

### Features added

All new code lives in the pure-JVM module `fullui-core/` and in `app/src/main/java/dev/fulluisuit/fullui/`,
plus small hooks in upstream files (listed in `BUILDING.md`, "What changed in upstream files"):

* `FullUiClient` / `FullUiService`: client for `/FullUI/Home`, `Rate`, `MyList`, `ComingSoon`, `Vote`,
  `MyServer`, `Notifications`, `Search`, `Item/{id}`, using Wholphin's authenticated OkHttp client and
  current server URL; models mirror `server/Jellyfin.Plugin.FullUI/Api/Dtos.cs`.
* One-time-per-session feature detection with automatic fallback to the stock Wholphin UI
  (`FeatureDetector`), and a Settings switch (Interface, "FullUI interface") to use the stock UI.
* Netflix-style top navigation bar (Home, Shows, Movies, My `<Server>`, Search) replacing the drawer,
  hero with muted YouTube trailer (WebView IFrame player, backdrop with slow zoom as fallback),
  focus-expanding cards with badges, Top 10 numbering, three-level thumbs, My List, Coming Soon cards
  with "I want this" / "Not for me", in-UI notifications.
* Playback, authentication, user/profile switching, details pages and settings are Wholphin's own code.
