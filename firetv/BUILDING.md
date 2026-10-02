# Building, signing, sideloading and updating FullUI for Fire TV

`firetv/` is a fork of [Wholphin](https://github.com/damontecres/Wholphin) (GPL-2.0, see
[`FULLUI_NOTICE.md`](FULLUI_NOTICE.md)) that renders the streaming-style FullUI experience from the
server plugin's API (`GET /FullUI/Home` etc., see `docs/api-contract.md`). Playback, login, profile
and settings flows are Wholphin's, unchanged.

Application id `dev.fulluisuit.tv` (debug builds `dev.fulluisuit.tv.debug`), app label "FullUI", so it
installs next to a stock Wholphin.

## Verification status (read this first)

What was actually run when this was written, in a sandbox with Maven Central and the Gradle plugin portal
reachable but **`dl.google.com` (Google Maven, Android SDK downloads) blocked**:

| Check | Result |
|---|---|
| `./gradlew -p jvm-checks :fullui-core:test` (API client against MockWebServer, JSON fixtures, feature detection, row/badge mapping, trailer page) | **Ran, 51 tests pass** |
| `jvm-checks/ui-typecheck` (`compileKotlin` of all FullUI Android UI code against Compose Multiplatform + the real Jellyfin SDK/OkHttp/Coil + hand-written stubs for tv-material3, Hilt, lifecycle and the Wholphin classes it calls) | **Ran, compiles with no errors.** This is a type check, not a build: it cannot see problems in the stubs, resources, manifest, KSP/Hilt, R8 or on a device |
| `./gradlew :app:assembleDebug` (the real Android build) | **Could not run.** AGP 9.2.1, AndroidX and the Android SDK come from `dl.google.com`, which the sandbox proxy blocks (`Plugin [id: 'com.android.application', version: '9.2.1'] was not found`) |
| Wholphin's own unit tests, the APK itself, Hilt/KSP/R8 steps, any run on a Fire TV | **Not run** |

So the Android-specific parts (Compose UI, WebView trailers, focus behavior, Hilt wiring, proto change,
settings entry) are unverified until CI (`.github/workflows/firetv.yml`) or a developer machine builds
them. Treat the first CI run as the real compile check.

## Prerequisites (full build)

* JDK 21 (CI uses Zulu 21; AGP 9 needs 17+).
* Android SDK: platform 37 (compileSdk 37), build-tools 36.0.0, NDK 29.0.14206865 (same as upstream's
  CI). With `ANDROID_HOME` set and licenses accepted Gradle downloads missing platforms itself.
* Network access to Google Maven, Maven Central and the Gradle plugin portal.
* Optional: Wholphin's prebuilt ffmpeg / AV1 / libmpv "extensions" from GitHub Packages. Without them
  (the default here) the build logs `libMPV was NOT found, using stub library` and plays with
  ExoPlayer only, without the extra ffmpeg audio/AV1 decoders. Provide a GitHub token with
  `read:packages` as `-PWholphinExtensionsUsername=<user> -PWholphinExtensionsPassword=<token>` (CI:
  secrets `EXTENSIONS_USERNAME` / `EXTENSIONS_PASSWORD`) to get the full upstream feature set.

## Build

```bash
cd firetv
# version is taken from properties (CI derives it from the tag firetv-vX.Y.Z)
./gradlew :app:assembleDefaultRelease -PfulluiVersionName=0.1.0 -PfulluiVersionCode=100
ls app/build/outputs/apk/default/release/
#  FullUI-default-release-0.1.0-100.apk              universal
#  FullUI-default-release-0.1.0-100-armeabi-v7a.apk  32-bit Fire TV Sticks
#  FullUI-default-release-0.1.0-100-arm64-v8a.apk    Fire TV Stick 4K Max, Cube, ...
#  FullUI-default-release-0.1.0-100-x86_64.apk
```

Use the **`default`** flavor. Upstream's `firetv` and `appstore` flavors switch the self-updater off
(they target app stores); FullUI is sideloaded and wants the updater.

`./gradlew :app:assembleDebug` gives a `dev.fulluisuit.tv.debug` build (separate app, debug-signed).

### JVM-only checks (no Android SDK needed)

```bash
cd firetv
./gradlew -p jvm-checks :fullui-core:test                       # unit tests
(cd jvm-checks/ui-typecheck && ../../gradlew -p . compileKotlin) # type check of the Compose UI code
```

`jvm-checks/` only needs a JDK and Maven Central / the plugin portal.

## Signing

Release builds are signed in this order (see `app/build.gradle.kts`):

1. **CI keystore**: when `CI=true` and `KEY_ALIAS` is set, from the environment variables
   `SIGNING_KEY` (the keystore, base64), `KEY_ALIAS`, `KEY_PASSWORD`, `KEY_STORE_PASSWORD`.
2. Upstream's `local.properties` entry `release.signing.config=<name>`.
3. **Fallback: the Android debug key**, so a fork or local build is always installable.
   A debug-signed APK cannot update (or be updated by) a release-signed install, Android will refuse
   with a signature mismatch; you would have to uninstall first (this deletes the app's settings).

Create a release key once and keep it safe (losing it means every user must uninstall to update):

```bash
keytool -genkeypair -v -keystore fullui-release.jks -alias fullui -keyalg RSA -keysize 4096 -validity 36500
base64 -w0 fullui-release.jks > fullui-release.jks.b64
gh secret set SIGNING_KEY       --repo narsmow/FullUISuit < fullui-release.jks.b64
gh secret set KEY_ALIAS         --repo narsmow/FullUISuit --body fullui
gh secret set KEY_PASSWORD      --repo narsmow/FullUISuit
gh secret set KEY_STORE_PASSWORD --repo narsmow/FullUISuit
```

Local signed build: `CI=true KEY_ALIAS=fullui KEY_PASSWORD=... KEY_STORE_PASSWORD=... SIGNING_KEY=$(base64 -w0 fullui-release.jks) ./gradlew :app:assembleDefaultRelease`.

## CI and releases

`.github/workflows/firetv.yml`:

* `jvm-tests`: `:fullui-core:test` on every change under `firetv/`.
* `apk`: `assembleDefaultRelease`, `apksigner verify`, uploads `FullUI-apk-signed` (or
  `FullUI-apk-debugsigned` when no keystore secrets exist) as a workflow artifact. Wholphin's own unit
  tests run too but are not blocking.
* `release`: only on a tag `firetv-vX.Y.Z` **and only if signed with the real keystore**. Publishes
  release `firetv-vX.Y.Z` (title `vX.Y.Z`) with `FullUI-release*.apk` + `SHA256SUMS.txt`, and refreshes
  the rolling release `firetv-latest` that the in-app updater reads.

```bash
git tag firetv-v0.1.0 && git push origin firetv-v0.1.0
```

versionCode is `major*10000 + minor*100 + patch` (monotonic for tagged releases).

## Sideloading on a Fire TV

Find the right APK: Fire TV Stick (2nd/3rd gen, Lite, 4K) usually `armeabi-v7a`, Fire TV Stick 4K Max
(2nd gen), Cube: `arm64-v8a`. Unsure: use the universal `FullUI-release.apk`, or ask the device:
`adb shell getprop ro.product.cpu.abi`.

**Downloader app (no PC):**
1. Settings, My Fire TV, Developer options, *Install unknown apps*: allow **Downloader**.
2. Open Downloader and enter the release asset URL, e.g.
   `https://github.com/narsmow/FullUISuit/releases/download/firetv-v0.1.0/FullUI-release-armeabi-v7a.apk`
   (or a short link you made for it), then Install.

**adb (from a PC on the same network):**
1. Settings, My Fire TV, Developer options: turn on *ADB debugging* (and note the IP under About, Network).
2. ```bash
   adb connect <fire-tv-ip>:5555        # accept the prompt on the TV
   adb install -r FullUI-release-armeabi-v7a.apk
   adb shell monkey -p dev.fulluisuit.tv -c android.intent.category.LAUNCHER 1   # optional: launch
   ```

First start: pick/enter your Jellyfin server and sign in as in Wholphin. If the FullUI plugin is on
the server and reachable, the streaming-style home appears; otherwise you silently get the stock UI.

## Updating

* **In-app (default):** Settings, Updates: *Automatically check for updates* is on by default; when a newer release exists a
  toast appears and *Check for updates* offers to download and install it. It reads
  `https://api.github.com/repos/narsmow/FullUISuit/releases/tags/firetv-latest` (editable under
  *Update URL*), picks `FullUI-release-<abi>.apk` / `FullUI-release.apk` from the assets and hands it to the
  Android installer. Requires the APK to be signed with the same key as the installed one, and
  "Install unknown apps" for FullUI itself on first use.
* **Manual:** `adb install -r <new apk>` (keeps data) or re-run Downloader with the new URL.
* To turn the updater off, untick *Automatically check for updates*, or build the `appstore`/`firetv` flavor.

## Using it

* **Top bar:** Home, Shows, Movies, My `<Server>` (Continue Watching, My List, wanted titles), Search,
  plus notifications, settings and profile switch. Back from a tab returns to Home.
* **Cards:** focus expands the card (after ~0.8 s the trailer plays muted). OK opens details (stock
  Wholphin page with Play), **long-press OK** toggles My List, **Down** moves into the action row: Play,
  My List, thumbs down / up / love (three levels, pressing the active one clears it), More Info.
* **Hero:** Play and More Info; the trailer starts after ~2 s, mute button appears while it plays. If
  the YouTube embed fails (offline, embedding disabled, no WebView) the backdrop with a slow zoom stays.
* **Coming Soon row:** OK or *I want this* registers interest, *Not for me* is a negative signal. Votes
  are private to the user.
* **Switch back to stock UI:** Settings, Interface, *FullUI interface* off (applies immediately).
  Automatic fallback: the plugin is probed once per session (per server and user): 404/405/501, 401, 403 or
  a non-JSON answer keep the stock UI for the session; timeouts/5xx/offline retry after 5 minutes.
  If a later call returns 404/401/403 the app flips to the stock UI by itself.

## What changed in upstream files (for merging Wholphin updates)

| File | Change |
|---|---|
| `app/build.gradle.kts` | applicationId, version props, debug-sign fallback, APK name, `:fullui-core` dependency |
| `settings.gradle.kts`, `gradle/libs.versions.toml` | `:fullui-core` module, coroutines/mockwebserver catalog entries |
| `app/proguard-rules.pro` | keep the WebView JS bridge |
| `app/src/main/proto/WholphinDataStore.proto` | `bool full_ui_disabled = 16` in `InterfacePreferences` |
| `preferences/AppPreference.kt` | `FullUiEnabled` switch, update URL default |
| `ui/nav/ApplicationContent.kt` | the drawer is wrapped in `FullUiHost { NavDrawer(...) }` |
| `services/UpdateChecker.kt`, `WholphinApplication.kt`, `strings.xml` | renames (see `FULLUI_NOTICE.md`) |
| tests `TestUpdateChecker`, `release_develop.json` | asset names `FullUI-*` |

New code: `fullui-core/` (pure JVM) and `app/src/main/java/dev/fulluisuit/fullui/` (service, view model,
Compose UI).

## Known gaps / assumptions not yet verified on a device

* Everything under "Verification status" marked not run.
* YouTube IFrame in a WebView on Fire OS: autoplay-muted is expected to work; videos with embedding
  disabled fail (code 101/150/153) and fall back to the backdrop. Per-card trailers are disabled on
  low-RAM devices and when animations are turned off in system settings.
* The server payload carries series-level cards; Play on a series resolves Next Up through the
  Jellyfin API (falls back to the first episode).
* Launcher icon/banner are still Wholphin's artwork.
