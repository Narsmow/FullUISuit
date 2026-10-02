# Install FullUI (for everyone, no technical knowledge needed)

FullUI gives your Jellyfin a Netflix-style look, with personalised rows and a "Coming Soon" page.
You install it by running **one file**. It talks to your Jellyfin for you, installs everything,
restarts Jellyfin, and checks that it worked. If something goes wrong it tells you in plain English
and it is always safe to just run the file again.

## Before you start (1 minute)

- Jellyfin is **running** on your PC or server, and you can open it in your browser.
- Its version is **10.11.x** (Jellyfin web page, bottom of the menu: Dashboard shows the version).
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
   (it uses PowerShell if you have it, otherwise Python 3, which almost every Linux has. It never installs anything by itself.)
3. Answer the questions, exactly as on Windows.

## What you will be asked (screenshots in words)

The window prints numbered steps, **[1/9]** up to **[9/9]**. Green `OK` lines mean good news.

| Step | What you see | What to do |
|---|---|---|
| 1 | `Looking for your Jellyfin server...` then `OK Found 'My Server' at http://localhost:8096` | Nothing. If it cannot find it, it asks "Jellyfin address": type what you type in your browser, for example `192.168.1.20` or `192.168.1.20:8096`. |
| 2 | `Version 10.11.x - supported.` | Nothing. If your version is different it explains and asks if you want to continue anyway. Say **n** unless a helper told you to say **y**. |
| 3 | `Admin username:` then `Password (typing is hidden):` | Type your admin username, press Enter, type the password (nothing appears while you type, that is normal), press Enter. You get 3 tries. The password is never saved or written to the log. |
| 4 | `Adding the plugin download sources` | Nothing. |
| 5-6 | `Installing File Transformation ...`, `Installing FullUI ...` | Wait about a minute. |
| 7 | `Jellyfin will restart now ... Continue? (Y/n)` | Press **Enter**. Your site is offline for about a minute. Don't do this while someone is watching. |
| 8 | `File Transformation and FullUI are both Active.` | Nothing. |
| 9 | `What should your site be called?` | Type a name (for example `MowFlix`) or press Enter to keep `FullUI`. |
| 9 | Explanation of **TMDB key** | Optional but recommended: it powers "Coming Soon" and trailers. Go to themoviedb.org, make a free account, open Settings > API, copy the "API Key". Paste it (hidden) and press Enter. If it is wrong the installer says so and asks again. Press Enter on an empty line to skip; you can add it later. |
| 9 | `Install Ollama and the two small AI models now? (y/N)` | Optional smarter search. Press Enter for **No** (you can run the installer again later). **y** downloads a few GB and only works if you run the installer on the Jellyfin computer. |
| end | Green box `All done! FullUI is installed.` plus your site address, and the Fire TV steps | Open the address. Press **Ctrl + F5** once in your browser to refresh. Log in as usual. |

## Fire TV / Fire Stick

The Fire TV app is a separate download and does **not** need any computer tools.

1. On the Fire TV install the free **Downloader** app from the Amazon Appstore.
2. Fire TV **Settings > My Fire TV > Developer Options > Install unknown apps** and allow **Downloader**.
3. Open Downloader and type exactly this address, then press Go:
   `https://github.com/Narsmow/FullUISuit/releases/latest/download/FullUI-FireTV.apk`
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
4. **"Jellyfin is version 10.x ... FullUI is built for 10.11.x."** Update Jellyfin to 10.11 (jellyfin.org/downloads), or ask for a FullUI build for your version. Continuing anyway usually ends with the plugin not loading.
5. **"This computer cannot download the ... plugin list."** No internet, a firewall/proxy/VPN is blocking GitHub or iamparadox.dev, or the site is briefly down. Fix the connection and run again.
6. **"Jellyfin's plugin catalog does not list ..."** The *Jellyfin server* itself cannot reach the internet (the installer reached it from your PC). Check the server's internet access, wait a minute, run again.
7. **"Installing ... hit an error on the server side."** Jellyfin could not download or unpack the plugin: full disk, antivirus holding the file, or blocked download. Look at Jellyfin Dashboard > Logs for the reason. On Linux check the plugin folder is writable by the jellyfin user.
8. **"Jellyfin did not come back within 300 seconds."** Jellyfin cannot restart itself in some setups. Start it by hand (Windows: tray app or the "Jellyfin Server" service; Linux: `sudo systemctl restart jellyfin`) and run the file again.
9. **"A plugin did not become active (... Malfunctioned / NotSupported)."** The plugin was built for a different Jellyfin version. Check Dashboard > Logs and update Jellyfin to 10.11.x.
10. **"The site looks like normal Jellyfin" / "The Jellyfin web page does NOT load FullUI yet."** Restart Jellyfin once more, wait a minute, then press **Ctrl + F5** in the browser (a stale copy is cached; on a phone app clear the app cache). If the check still fails, File Transformation is not working with your Jellyfin version - run the installer again and send the log.

Other things worth knowing:

- Windows **Smart App Control / SmartScreen / antivirus** may block the file. Choose "Run anyway", or right-click the file, **Properties**, tick **Unblock**.
- **HTTPS with a self-made certificate** (port 8920): the installer warns and asks before continuing without checking the certificate.
- **Ctrl+C** or closing the window at any time is safe. Run the file again to continue.
- **A company or school proxy** is used automatically through Windows settings.

## Update

Run the same installer file again (download the newest from the Releases page first for new installer fixes).
It only changes what is out of date, restarts Jellyfin only if something new was installed, and keeps your settings.
Quiet version: `Install-FullUI.cmd -Update`.

Jellyfin can also update FullUI by itself: Dashboard > Plugins > Catalog.

## Uninstall

Run the installer with `-Uninstall`:

- Windows (Command Prompt in the folder with the file): `FullUI-Installer.cmd -Uninstall`
- Linux/macOS: `bash FullUI-Installer.sh -Uninstall`

It asks to confirm, removes FullUI, asks whether to also remove **File Transformation** (say **No** if you use other
plugins such as Home Screen Sections), and offers to restart Jellyfin. Your FullUI settings, ratings and requests are
**kept**, so installing again later restores everything. To erase them completely, stop Jellyfin and delete the FullUI
files under its data folder (`plugins/configurations/FullUI*.xml` and `data/plugins/FullUI`). Or remove the plugin by
hand in Dashboard > Plugins > FullUI > Uninstall.

## For scripts and helpers (advanced)

```
FullUI-Installer.cmd -Unattended -Server 192.168.1.20:8096 -Username admin -Password "..." -TmdbKey "..." -ServerName "MowFlix"
bash FullUI-Installer.sh -Unattended -Server host:8096 -ApiKey "<key from Dashboard > Advanced > API Keys>"
```

Other switches: `-Update`, `-Uninstall`, `-RemoveFileTransformation`, `-InstallOllama`, `-SkipOllama`, `-AllowOtherVersion`,
`-Insecure` (skip certificate check), `-FireTvIp 192.168.1.30` (install the APK with `adb` if it is on the PC),
`-RestartTimeoutSec 300`, `-LogDir <folder>`. Passwords/keys can also come from the environment variables
`FULLUI_PASSWORD`, `FULLUI_APIKEY`, `FULLUI_TMDB_KEY`. Exit codes: 0 success, 1 failed, 2 stopped/cancelled.
Without `-Unattended`, extra switches work only if they contain no spaces (use `install.ps1` directly otherwise).

### How the plugin repository works (for maintainers)

Pushing a tag such as `v1.2.3` runs `.github/workflows/release.yml`. It builds the web bundle and the plugin, publishes
`FullUI_1.2.3.0.zip`, a regenerated `manifest.json`, `FullUI-Installer.cmd`, `FullUI-Installer.sh` (and the Fire TV APK
as `FullUI-FireTV.apk` when `firetv/` has a Gradle wrapper) to the GitHub Release, and commits the new `manifest.json` to
`main`. The plugin repository URL that Jellyfin uses is
`https://raw.githubusercontent.com/Narsmow/FullUISuit/main/manifest.json`; the fallback (used if `main` is protected and the
commit fails) is `https://github.com/Narsmow/FullUISuit/releases/latest/download/manifest.json`. The installer tries the first, then the second.
