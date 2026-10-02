# Install FullUI (for everyone, no technical knowledge needed)

FullUI gives your Jellyfin a streaming-style look, with personalised rows and a "Coming Soon" list.
You install it by running **one file**. It talks to your Jellyfin for you, installs everything,
restarts Jellyfin, and checks that it worked. If something goes wrong it tells you in plain English
and it is always safe to just run the file again.

After installing, read the [user guide](USER_GUIDE.md) to see what the screens do and the [admin guide](ADMIN_GUIDE.md) to set up TMDB,
check the Health page and look after the data.

## Before you start (1 minute)

- You use **Windows 10 or 11**, Linux or macOS. (Older Windows needs Windows PowerShell 5.1 and .NET 4.5 installed first; the installer
  cannot do that for you.)
- Jellyfin is **running** on your PC or server, and you can open it in your browser.
- Its version is **10.11.6 or newer** (any later 10.11.x is fine; 10.11.0 to 10.11.5 are too old, and 10.10 or 10.12 are not supported). The Jellyfin Dashboard shows the version; update at jellyfin.org/downloads if needed.
- You know the **username and password of a Jellyfin administrator** (the account you use to change settings).
- The computer running Jellyfin is **connected to the internet** (Jellyfin downloads the plugins itself).
- Best: run the installer **on the same computer as Jellyfin**. It also works from another computer on your network; you just type Jellyfin's address.

## The 3 steps

### Windows

1. **Download** the file `FullUI-Installer.cmd` from the Releases page:
   <https://github.com/Narsmow/FullUISuit/releases/latest> (look under "Assets", click the file name).
   Your browser may say the file "is not commonly downloaded" - choose **Keep**.
2. **Double-click** `FullUI-Installer.cmd` (in your Downloads folder).
   - If Windows shows a blue box "Windows protected your PC": click **More info**, then **Run anyway**.
   - A black window opens. That is normal. Leave it open.
3. **Answer the questions** in the black window (see "What you will be asked" below). When it says
   **"All done! FullUI is installed."**, press any key to close it and open your Jellyfin site.

### Linux / macOS

1. Download `FullUI-Installer.sh` from the same Releases page.
2. Open a terminal in the folder and run: `bash FullUI-Installer.sh`
   (it uses PowerShell if you have it, otherwise Python 3, which almost every Linux has. It does not install PowerShell or Python for you.)
3. Answer the questions, exactly as on Windows.

## What the installer downloads and changes (nothing hidden)

- **FullUI** comes from this project's GitHub (`raw.githubusercontent.com/Narsmow/FullUISuit`, with the release page as a backup).
- **File Transformation** is a helper plugin by a third party (IAmParadox27). It is not part of FullUI. The installer adds *his*
  plugin list (`https://www.iamparadox.dev/jellyfin/plugins/manifest.json`) to Jellyfin's plugin repositories **permanently**
  (you can remove it in Dashboard > Plugins > Repositories). Jellyfin only checks that list's downloads with MD5 checksums.
  If you already have File Transformation, the installer keeps your copy (it asks before upgrading it).
- **Ollama** (optional AI search, only if you answer yes or use `-InstallOllama`) is also third-party software. The installer
  downloads and runs *Ollama's own installer* from ollama.com: on Windows via `winget`, or by downloading `OllamaSetup.exe`
  (which is only run if it carries a valid digital signature); on Linux it runs Ollama's official `install.sh`.
  Skip it if you are not comfortable with that; everything else works without it.
- It signs in with the admin account you type, uses that sign-in only for this run, and signs out again.

**Check your download (optional).** Each release lists `SHA256SUMS`. Linux/macOS: `sha256sum -c SHA256SUMS --ignore-missing`.
Windows (PowerShell): `Get-FileHash .\FullUI-Installer.cmd -Algorithm SHA256` and compare with the line in `SHA256SUMS`.

## What you will be asked (screenshots in words)

The window prints numbered steps, **[1/9]** up to **[9/9]**. Green `OK` lines mean good news.

| Step | What you see | What to do |
|---|---|---|
| 1 | `Looking for your Jellyfin server...` then `OK Found 'My Server' at http://localhost:8096` | Nothing. If it cannot find it, it asks "Jellyfin address": type what you type in your browser, for example `192.168.1.20` or `192.168.1.20:8096`. |
| 2 | `Version 10.11.x - supported.` | Nothing. If your version is older than 10.11.6 (or is not 10.11) it explains and asks if you want to continue anyway. Say **n**, update Jellyfin, and run the file again, unless a helper told you to say **y**. |
| 3 | `Admin username:` then `Password (typing is hidden):` | Type your admin username, press Enter, type the password (nothing appears while you type, that is normal), press Enter. You get 3 tries. The password is never saved or written to the log. |
| 4 | `Adding the plugin download sources` | Nothing. |
| 5-6 | `Installing File Transformation ...`, `Installing FullUI ...` | Wait about a minute. |
| 7 | `Jellyfin will restart now ... Continue? (Y/n)` | Press **Enter**. Your site is offline for about a minute. Don't do this while someone is watching. |
| 8 | `File Transformation and FullUI are both Active.` | Nothing. |
| 9 | `What should your site be called?` | Type a name (for example `MowFlix`) or press Enter to keep `FullUI`. |
| 9 | Explanation of **TMDB key** | Optional but recommended: it powers "Coming Soon" and trailers. Go to themoviedb.org, make a free account, open Settings > API, copy the "API Key". Paste it (hidden) and press Enter. If it is wrong the installer says so and asks again. Press Enter on an empty line to skip; you can add it later. |
| 9 | `Install Ollama and the two small AI models now? (y/N)` | Optional smarter search. Press Enter for **No** (you can run the installer again later). **y** downloads a few GB and only works if you run the installer on the Jellyfin computer. |
| end | Green box `All done! FullUI is installed.` plus your site address, and the Fire TV steps | Open the address. Press **Ctrl + F5** once in your browser to refresh. Log in as usual. Then open **Dashboard > FullUI Health**: it should say "Everything looks fine." |

## Fire TV / Fire Stick

The Fire TV app is a separate download and does **not** need any computer tools. **It is paused and experimental:** the supported way to
use FullUI is through the Jellyfin web page and the apps that wrap it. Try the Fire TV app only if you are curious.

1. On the Fire TV install the free **Downloader** app from the Amazon Appstore.
2. Fire TV **Settings > My Fire TV > Developer Options > Install unknown apps** and allow **Downloader**.
3. Open Downloader and type exactly this address, then press Go:
   `https://github.com/Narsmow/FullUISuit/releases/download/firetv-latest/FullUI-release.apk`
   (that is the universal build; the same page also offers `FullUI-release-armeabi-v7a.apk` and `-arm64-v8a.apk`).
4. Choose **Install**, then open **FullUI**, type your Jellyfin address, username and password.

If the download says "not found", the Fire TV app has not been published yet - it is still coming.
(The installer tells you the same.) Phones, tablets, computers and LG/Samsung TVs get the new look
automatically through the normal Jellyfin web page or app; only the official *Android TV / Fire TV* app and
Swiftfin do not show it.

## Troubleshooting - the top 10

Every problem prints a message in this form: **What happened / What to try / safe to run again**. A detailed
technical log file `FullUI-install-<date-time>.log` is saved next to the installer (passwords and keys are removed from it).
Send that file to whoever helps you.

1. **"I could not find a Jellyfin server."** Jellyfin is not running, or it is on a different computer. Open Jellyfin in your browser to check, then run again and type the address when asked.
2. **"The username or password was not accepted."** Try signing in to Jellyfin in your browser with the same details. Passwords are case-sensitive. Be careful with accounts that have no password set: type nothing and press Enter.
3. **"... is a normal user, not an administrator."** Use the account you use for Dashboard and settings. Create an administrator in Dashboard > Users if needed.
4. **"Jellyfin is version 10.x ... too old" / "... is not 10.11.x".** FullUI needs Jellyfin 10.11.6 or newer. Update Jellyfin (jellyfin.org/downloads) and run the file again. Continuing anyway usually ends with Jellyfin refusing the plugin ("No version of FullUI is built for ...") or the plugin not loading.
5. **"This computer cannot download the ... plugin list."** No internet, a firewall/proxy/VPN is blocking GitHub or iamparadox.dev, or the site is briefly down. Fix the connection and run again.
6. **"Jellyfin's plugin catalog does not list ..."** The *Jellyfin server* itself cannot reach the internet (the installer reached it from your PC). Check the server's internet access, wait a minute, run again.
7. **"Installing ... hit an error on the server side."** Jellyfin could not download or unpack the plugin: full disk, antivirus holding the file, or blocked download. Look at Jellyfin Dashboard > Logs for the reason. On Linux check the plugin folder is writable by the jellyfin user.
8. **"Jellyfin did not come back within 300 seconds."** Jellyfin cannot restart itself in some setups. Start it by hand (Windows: tray app or the "Jellyfin Server" service; Linux: `sudo systemctl restart jellyfin`) and run the file again.
9. **"A plugin did not become active (... Malfunctioned / NotSupported)."** The plugin was built for a different Jellyfin version. Check Dashboard > Logs and update Jellyfin to 10.11.6 or newer.
10. **"The site looks like normal Jellyfin" / "The Jellyfin web page does NOT load FullUI yet."** Restart Jellyfin once more, wait a minute, then press **Ctrl + F5** in the browser (a stale copy is cached; on a phone app clear the app cache). If the check still fails, File Transformation is not working with your Jellyfin version - run the installer again and send the log.

Other things worth knowing:

- Windows **Smart App Control / SmartScreen / antivirus** may block the file. Choose "Run anyway", or right-click the file, **Properties**, tick **Unblock**.
  If a company policy blocks scripts, the window says "The installer could not start" and changes nothing: use "Install by hand" below.
- **HTTPS with a self-made certificate** (port 8920): the installer warns and asks before continuing without checking the certificate
  (for that one server only). In scripts (`-Unattended`) you must say so explicitly with `-Insecure`.
- **Jellyfin under a base path** (Dashboard > Networking > "Base URL", for example `/jellyfin`): type the address with it
  (`myserver:8096/jellyfin`). The final self-test fetches the FullUI script through the address written in the page, and warns if the
  browser would get a 404 there.
- An address that **redirects** (for example `http://` to `https://`) is followed; the installer then uses the final address.
- The **Jellyfin server itself is contacted directly**, bypassing any system proxy; other downloads use the system proxy settings.
- **Ctrl+C** or closing the window at any time is safe. Run the file again to continue.
- **A company or school proxy** is used automatically through Windows settings.

## Update

Run the same installer file again (download the newest from the Releases page first for new installer fixes).
It only changes what is out of date, restarts Jellyfin only if something new was installed, and keeps your settings.
To only update (it asks for the password as usual and stays quiet when nothing is new): `FullUI-Installer.cmd -Update`.
For a script without any questions add `-Unattended` and the account (see "For scripts" below).

Jellyfin can also update FullUI by itself: Dashboard > Plugins > Catalog.

## Uninstall

Run the installer with `-Uninstall`:

- Windows (Command Prompt in the folder with the file): `FullUI-Installer.cmd -Uninstall`
  (Jellyfin keeps older, superseded versions of a plugin on disk after updates; the uninstall removes every version it finds.)
- Linux/macOS: `bash FullUI-Installer.sh -Uninstall`

It asks to confirm, removes FullUI, asks whether to also remove **File Transformation** (say **No** if you use other
plugins such as Home Screen Sections), and offers to restart Jellyfin. Your FullUI settings, ratings and requests are
**kept**, so installing again later restores everything. To erase them completely, either use the delete-all step in the
[admin guide](ADMIN_GUIDE.md) before uninstalling, or stop Jellyfin and delete the whole folder
`<Jellyfin data folder>/fullui/` (it holds `store.json`, `embeddings.json` and `events.jsonl`) and
`<Jellyfin config folder>/plugins/configurations/Jellyfin.Plugin.FullUI.xml`
(on Windows the data folder is usually `C:\ProgramData\Jellyfin\Server`). Or remove the plugin by hand in
Dashboard > Plugins > FullUI > Uninstall.

## Install by hand (if the installer cannot run)

1. Jellyfin Dashboard > Plugins > **Repositories** > **+**: add `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
   (File Transformation, third party) and `https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json` (FullUI).
2. Dashboard > Plugins > **Catalog**: install **File Transformation**, then **FullUI**.
3. Restart Jellyfin (Dashboard > Restart), then press **Ctrl + F5** in the browser.
4. Dashboard > Plugins > FullUI: set the site name and your TMDB key.

## For scripts and helpers (advanced)

```
FullUI-Installer.cmd -Unattended -Server 192.168.1.20:8096 -Username admin -Password "..." -TmdbKey "..." -ServerName "MowFlix"
bash FullUI-Installer.sh -Unattended -Server host:8096 -ApiKey "<key from Dashboard > Advanced > API Keys>"
```

Other switches: `-Update`, `-Uninstall`, `-RemoveFileTransformation`, `-InstallOllama`, `-SkipOllama`, `-AllowOtherVersion`,
`-Insecure` (skip certificate check), `-FireTvIp 192.168.1.30` (install the APK with `adb` if it is on the PC),
`-RestartTimeoutSec 300`, `-LogDir <folder>`. **Prefer the environment variables** `FULLUI_PASSWORD`, `FULLUI_APIKEY`,
`FULLUI_TMDB_KEY` over `-Password` / `-ApiKey` / `-TmdbKey`: command lines show up in shell history and process lists.
Example (Windows): `set FULLUI_PASSWORD=...` then `FullUI-Installer.cmd -Unattended -Server host:8096 -Username admin`.
Exit codes: 0 success, 1 failed, 2 stopped/cancelled.
Extra switches given to the `.cmd` work as long as special characters (`& ^ %`) are quoted; if in doubt use `install.ps1` directly.

### How the plugin repository works (for maintainers)

Pushing a tag such as `v1.2.3` runs `.github/workflows/release.yml`. It first runs every test (web, the .NET test projects,
and the installer tests on Linux and on Windows under Windows PowerShell 5.1 and PowerShell 7); only then does it build the plugin and
publish `FullUI_1.2.3.0.zip`, a regenerated `manifest.json`, `FullUI-Installer.cmd`, `FullUI-Installer.sh`, the loose
installer files and `SHA256SUMS` to the GitHub Release, and commit the new `manifest.json` to `main` (rebasing onto the newest
`main` and retrying; if it cannot push, the run fails loudly). A prerelease tag such as `v1.2.3-rc1` only makes a GitHub
prerelease and never touches `main`; its plugin version (`1.2.2.9001`) sorts below the final `1.2.3.0`.
The **Fire TV app is not built here**: `.github/workflows/firetv.yml` builds, signs and publishes it as
`releases/download/firetv-latest/FullUI-release.apk`, which is the address the installer and this guide show.
The plugin repository URL that Jellyfin uses is `https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json`; the
fallback (used when that list is unreachable, empty or out of date) is
`https://github.com/Narsmow/FullUISuit/releases/latest/download/manifest.json`. The installer only accepts a list that really
offers FullUI with at least one version, and tries the second address otherwise.

The plugin is built against Jellyfin 10.11.6 (the oldest supported version) and its manifest entries say `targetAbi 10.11.6.0`;
`tools/abi-matrix.sh` (run by CI and by the release workflow before publishing) proves it works on 10.11.6 up to the newest 10.11.x.

The installer talks to the plugin by its GUID and configuration property names; `installer/tests/test_consistency.py` fails the
build if these drift from `server/Jellyfin.Plugin.FullUI/Plugin.cs` and `Configuration/PluginConfiguration.cs`.
