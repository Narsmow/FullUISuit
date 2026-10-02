# FullUI admin guide

For the person who runs the Jellyfin server. You have already run the one-file installer ([INSTALL.md](INSTALL.md)). This guide covers the settings, the two admin pages, looking after the data, and what to do when something is wrong. Everything is written for a non-programmer. The people who watch have their own guide: [USER_GUIDE.md](USER_GUIDE.md).

**Where things are** (you must be signed in as a Jellyfin administrator):

| What | Where in Jellyfin |
|---|---|
| Settings | Dashboard > Plugins > **FullUI** |
| Most Requested | Dashboard side menu > **FullUI Requests** (page title "Most Requested") |
| Health | Dashboard side menu > **FullUI Health** |
| Scheduled jobs | Dashboard > Scheduled Tasks > category **FullUI** |

**Supported Jellyfin:** 10.11.6 and later in the 10.11 line. Older 10.11.x (0 to 5) and other major versions (10.10, 10.12) are not supported.

---

## 1. The Settings page

Open Dashboard > Plugins > FullUI. At the very top a coloured note says whether the FullUI screen is switched on ("a tick" means FullUI is hooked into the Jellyfin web page; a warning triangle explains in plain English what is missing). Change what you like and press **Save** at the bottom ("Saved." appears). Settings take effect within a short time; users may need to reload their page.

### TMDB

TMDB (The Movie Database) is a free movie and TV database. FullUI uses it for **Coming Soon**, **trailers** for titles that have none, and **I want this / Remind me** details. **Without a key, those features are simply empty; everything else still works.**

How to get a key (about three minutes):

1. Go to <https://www.themoviedb.org> and create a free account.
2. Open <https://www.themoviedb.org/settings/api> and request an API key (choose the personal / non-commercial option and fill in the short form).
3. Copy the **API Key** (a long string of letters and numbers). The longer "API Read Access Token" works too.
4. Paste it into **TMDB API key or v4 read token** on the Settings page, press **Test connection**, and when it shows a green tick, press **Save**.

- **Show key** reveals what you pasted. The key stays on your server and is never sent to anyone's browser.
- **Language** (for example `en-US`) is the language TMDB uses for titles and descriptions of Coming Soon items.
- **Region** (for example `US`) is the country TMDB uses to choose release dates and age ratings; it is tried first, then the US.
- A red "rejected" message means the key was refused: copy it again, without spaces.

TMDB's required notice, which FullUI shows on its Coming Soon screens and admin pages: *This product uses the TMDB API but is not endorsed or certified by TMDB.* Please do not remove it.

### Branding

- **Server name**: shown top-left on the home screen, in the **My (name)** tab and in row titles such as "Top 10 Movies on (name) This Week" and "Popular on (name)".
- **Accent colour**: a web colour written as `#` followed by 3, 4, 6 or 8 letters or digits (0 to 9, a to f). The default is **`#e5383b`** (a red). It colours buttons, progress bars and highlights. If you leave out the `#` it is added for you; anything that is not a valid colour is refused when you press Save ("Nothing was saved") and, if one ever got through, the screen falls back to the default red rather than becoming unreadable.

### AI search (Ollama), optional

Ollama is a free program that runs small AI models on your own computer; nothing is sent to the internet. If you switch it on, FullUI uses it to (1) let people search by *meaning* ("a feel-good sports film"), (2) improve how similar titles are compared, and (3) write friendlier genre row names. The installer can install Ollama and the two small models for you (**nomic-embed-text** for meaning and **llama3.2:3b** for wording).

**Be honest with yourself before enabling it:**

- It uses your server's **processor and memory**, mainly when it builds its index overnight (the "Build AI index" job runs at 04:00) and when someone searches. On a small PC, a NAS or a machine that also transcodes video, it can **make Jellyfin and everything else noticeably slower** while it works.
- The models are a **download of several GB** and must fit in memory. On a computer without a capable graphics card they run on the CPU and are slow. Check Ollama's own documentation for the current memory needs of the models before enabling.
- Ollama only works when it runs on a machine FullUI can reach (the default address is `http://localhost:11434`, meaning the same computer).
- **If it is slow or off, nothing breaks.** Search falls back to normal keyword search, row names fall back to "(Genre) Picks for You", and a short time-out and back-off stop a sleeping Ollama from slowing every search.
- If you are unsure, leave it off. You lose very little.

Fields: **Enable AI search and row titles**; **Ollama URL**; **Embedding model**; **Chat model** (leave the defaults unless you know why); **Test connection** shows a tick or a plain-English reason. A new embedding model is picked up by the next index build.

### Playlists, requests and users

- **Also write "FullUI:" playlists for clients that cannot show the FullUI home screen**: for apps that never load FullUI (the official Android TV / Fire TV app, Swiftfin), FullUI can write each person's recommendations as playlists named "FullUI: (row)" once a day (05:00). Off by default. Only playlists with that name prefix are touched.
- **Notify users when a title they requested is added**: sends the bell message "(Title) is now available".
- **Play trailers automatically on the FullUI home screen**: switch off to stop all trailer playback and avoid loading the YouTube player at all (see "Privacy" below).
- **Top 10 window (days)**: how many days of plays count. 7 gives "This Week", 1 gives "Today", other numbers give "in the Last N Days". Valid range 1 to 90.
- **Excluded user ids**: a comma-separated list of people whose plays should not count towards Top 10, Trending and the "people with similar taste" suggestions (see section 7). A user id is the long code after `userId=` in your browser's address bar when you open a user in Dashboard > Users.

### Switches that are not on the Settings page

Four on/off options exist in FullUI's configuration but **do not have a box on the Settings page yet**:

| Option | Default | What it does |
|---|---|---|
| `PlayerAssistEnabled` | on | The skip-intro / next-episode buttons. Set to off to remove them for everyone. |
| `ExcludeKidsFromSharedSignals` | on | Leaves children's plays out of Top 10, Trending and similar-taste suggestions (section 7). |
| `CollectInteractionMetrics` | on | Records which rows are shown/clicked and search words, for the usage statistics and per-person row ordering. Off stops recording and ignores what is sent. |
| `ShowRecommendedNotInLibrary` | on | The "Recommended for you (not in library)" list next to Coming Soon. |

To change one: **stop Jellyfin**, open `<Jellyfin config folder>/plugins/configurations/Jellyfin.Plugin.FullUI.xml` in a plain text editor (the file name is the one named in the uninstall section of [INSTALL.md](INSTALL.md)), change `true` to `false` on the matching line (for example `<PlayerAssistEnabled>false</PlayerAssistEnabled>`), save, and start Jellyfin. Saving the Settings page afterwards keeps these values. If you are not comfortable editing the file, ask a helper. Take a copy of the file first.

---

## 2. Most Requested

Dashboard > **FullUI Requests**. Every title anyone pressed **I want this** on from Coming Soon (or the recommended list) is listed once, with the number of votes and **the names of the people who asked**. Titles are listed by TMDB id; unknown ones show as "TMDB 1234".

- **Most votes / Most recent** sorts the list; **Grid / List** changes the layout (both choices are remembered in your browser).
- Each title has an **Open on TMDB** link, a **status** drop-down (**Requested**, **Getting it**, **Added**), an optional **Note**, and **Save**.
- **Download CSV** gives a spreadsheet with a header row and accents that Excel reads correctly.
- **Refresh now** rebuilds the library snapshot, Coming Soon lists, the AI index (if enabled) and the row names in the background. Use it after adding lots of titles or entering the TMDB key.

What it does **not** do: FullUI never downloads anything. You decide what to obtain and add it to your library yourself. When a requested title appears in your library (checked within about 30 seconds of the library changing, and every 6 hours), its status becomes **Added** automatically and everyone who asked gets "(Title) is now available" in their bell (if the notification setting is on). The status you set by hand is **your own tracking aid**: the watchers' screens do not display "Getting it". The page's intro sentence about "everyone can see progress" is more optimistic than the screens; tell your users to expect only the arrival notification.

---

## 3. Health

Dashboard > **FullUI Health** is the first place to look when anything seems wrong. It runs a check each time you open it. A coloured banner at the top summarises: *"Everything looks fine."*, *"Mostly fine, but a few things deserve a look"* (yellow), or *"Needs a look: ..."* (red).

Buttons: **Check again**, **Rebuild now** (same as Refresh now above) and **Export my data** (downloads a backup file, see section 4).

### What each light means

Each line has a coloured badge: **green = OK**, **yellow = Check**, **red = Problem**, **grey = Not in use**.

**Connections**

| Line | Light and wording | What it means and what to do |
|---|---|---|
| FullUI home screen | green *Working* | The FullUI screen is hooked into the Jellyfin web page. |
| | red *Not active* | FullUI could not hook in. Almost always the **File Transformation** plugin: open Dashboard > Plugins and check it is installed and **Active**; restart Jellyfin; wait a few minutes (FullUI retries for about 15 minutes after start); then press Check again. Re-running the installer fixes missing plugins. After a Jellyfin update File Transformation sometimes needs its own update. |
| TMDB | grey *Not set up* | No key entered. Coming Soon and trailers need one (section 1). |
| | red *Key rejected* | TMDB refused the key. Paste it again and press Test connection. |
| | green *Set up* | A key is saved. It does not prove the key works; press **Test connection** on the Settings page. |
| AI search (Ollama) | grey *Off* | Normal. |
| | green *On* | Switched on. Use Test connection on the Settings page to be sure it is reachable. |
| | yellow *Needs attention* | Switched on, but no address is set. |

**Background jobs** (also visible under Dashboard > Scheduled Tasks, category FullUI)

| Job | When it runs |
|---|---|
| Rebuild FullUI recommendations | daily 04:00 |
| Discover upcoming titles (Coming Soon and trailers) | daily 03:00 |
| Build AI index | daily 04:00 |
| Sync requested titles | every 6 hours and shortly after the library changes |
| Send reminders and tidy up | daily 06:00 |
| Write recommendation playlists | daily 05:00 (only if the playlists setting is on) |

Lights: green *Worked*; grey *Has not run yet* (normal on a fresh install; press Rebuild now or wait for the night); yellow *Finished, but something went wrong*, *Was stopped before it finished* or *Has not run for a few days*; red *Failed*. For red or yellow: read the message on the line, press **Rebuild now**, check again in a minute; if it persists open Dashboard > Logs and look for lines starting "FullUI:", and keep the log for whoever helps you. A job that "has not run for a few days" usually means Jellyfin or the computer was off at night: run it from Scheduled Tasks.

**Stored data**: the number of library titles FullUI can see, the size of its three data files (`store.json`, `embeddings.json`, `events.jsonl`) and counts of ratings, My List entries, votes, reminders and notifications. "Library titles seen: 0" means FullUI cannot see your library yet (Jellyfin is still scanning, or nothing is added).

**Recent problems**: the last 50 friendly error messages since the server last started (scrubbed of keys and web addresses). Empty is good.

**Version**: FullUI's version, "works with Jellyfin 10.11.6 and later", and its stored-data format number.

---

## 4. Usage statistics (Metrics)

If `CollectInteractionMetrics` is on (the default) the watchers' browsers report which rows were shown, which cards were opened, played or had their trailer watched, and what was searched. FullUI uses it to reorder each person's rows by what *they* click (with a cautious default so one click does not reshuffle everything) and gives you **totals only**: per row type, how often it was shown, clicked and played, the take rate (clicks and plays divided by times shown), rows that were shown but never clicked, the most popular searches with how many different people searched each, and events per day (last 30 days by default, up to 90).

**There is no Metrics page in the Dashboard yet.** The numbers are available at `GET /FullUI/Admin/Metrics?days=30` for an administrator (an API key from Dashboard > Advanced > API Keys, or a helper using the browser developer tools). Ask a helper to read it for you; it is a small block of text. Raw per-person events are stored in `<Jellyfin data folder>/fullui/events.jsonl` (rotated at 10 MB, kept at most 90 days) and are never shown on any FullUI screen.

---

## 5. Backup, export, import and purge

FullUI keeps its own data in `<Jellyfin data folder>/fullui/` (on Windows usually `C:\ProgramData\Jellyfin\Server\fullui`): `store.json` (ratings, My List, votes, reminders, notifications, play summaries and titles hidden from Continue Watching), `embeddings.json` (AI index, can be rebuilt), `events.jsonl` (usage log). Plus the settings file `plugins/configurations/Jellyfin.Plugin.FullUI.xml` in the config folder. If a data file is damaged FullUI keeps it as `.bad` and starts empty rather than crashing; when FullUI upgrades its stored format it keeps `store.json.v1.bak` first.

### Export (the Export my data button)

On the Health page press **Export my data**. Your browser saves `fullui-export.json`, a readable text file with, for every person: **ratings, My List, "I want this / Not for me" votes, reminders, notifications, first-run picks, and titles hidden from Continue Watching**, plus request statuses and notes. It does **not** contain play history, the AI index or usage events (play history is Jellyfin's own, and FullUI re-reads it).

**Use it** before you update FullUI, before you reinstall Jellyfin, before you experiment, and now and then as a safety copy. It is not a full-server backup: also back up Jellyfin itself (see Jellyfin's documentation on backups).

### Import (restore)

There is **no Import button** yet; it is available to an administrator through the API. The safe way is two steps:

1. **Dry run (changes nothing):** send the file to `POST /FullUI/Admin/Import`. It answers with how many items it *would* import and a list of what it will skip.
2. **Apply:** the same call with `?dryRun=false`.

Things to know: the file must be a FullUI export of the same format; only **people and titles that exist on this server with the same ids** are restored. On a freshly rebuilt Jellyfin, user ids are new, so nothing will match. Restoring on the same server (or after restoring Jellyfin's own data) works. Unknown users, titles and invalid values are skipped and reported, never half-applied. Importing the same file twice changes nothing. Files over 20 MB are refused. A helper with a terminal can do it with, for example, `curl -X POST -H "Content-Type: application/json" -H "Authorization: MediaBrowser Token=\"YOUR_API_KEY\"" --data-binary @fullui-export.json "http://localhost:8096/FullUI/Admin/Import"`.

### Purge (delete data)

Also API only, and deliberately in **two steps** so nothing is deleted by accident. `POST /FullUI/Admin/Purge` with `{"userId": "<user id>"}` or `{"userId": "all"}` first deletes nothing and returns a short summary and a one-time confirmation code, valid for 5 minutes. Send the same request again with `"confirm": "<the code>"` to delete.

- **One person**: removes their play summaries, ratings, My List, votes, notifications, reminders, Coming Soon list, first-run picks, hidden titles and usage events. Use for "please delete my data" or a user who has left.
- **All**: also clears request statuses, trailer ids, row names, the AI index and the usage log. Use before uninstalling when you want nothing left behind. (Jellyfin's own watch history and users are never touched.)

Make an export first.

---

## 6. Adding users and kids

Create people in Jellyfin as usual (Dashboard > Users). FullUI needs nothing else: a new person gets a plain "popular on your server" home at first, and FullUI reads their existing Jellyfin watch history once so they are not starting from zero. Their rows get personal as they watch, rate and use My List (and, in newer versions, "Pick your favourites").

Give each person their **own** Jellyfin account. FullUI has no in-account profiles (see section 11), so two people sharing one login share one taste profile.

### How "kids are left out of the shared charts" works, and where it can be wrong

You do not mark anyone as a child. FullUI guesses from what Jellyfin lets them see. A person counts as restricted ("a kid") when the library contains **at least 5 titles with a mature rating** (R, NC-17, X, TV-MA, 18, 18+, 16, FSK-16, FSK-18, MA15+, R18+, R-18 or NC-16) **and that person can see 5% or fewer of them**. That is normally the result of a Jellyfin **parental rating limit** or of being kept out of the libraries holding such content. Such a person's plays are then **left out of Top 10, Trending, Everyone's Watching and the "people with similar taste" suggestions** shown to the adults, and the kid's own home is built normally from their own taste.

Limits you should know:

- It is a guess from the library, not a flag on the account. If your library has fewer than 5 titles carrying those ratings (for example no ratings in the metadata) **nobody is ever detected**.
- A grown-up whose access is limited to one library, or a parent with a low limit for their own reasons, is treated as a kid. A child whose limit still lets them see most R-rated titles is not.
- The New & Popular page uses a simpler test: anyone with **any** parental limit set in Jellyfin is skipped.
- It only affects the *shared* charts and similar-taste suggestions. It does not decide what a child may see; that is Jellyfin's parental control, which FullUI always respects (a child never sees titles Jellyfin hides from them, and Coming Soon titles are checked against the child's limit using TMDB age ratings; a title with no age rating from TMDB is hidden from a limited user).
- For anyone you want left out of the charts regardless, add their user id to **Excluded user ids** on the Settings page (this also removes them from similar-taste suggestions). To turn the automatic test off completely, set `ExcludeKidsFromSharedSignals` to false (section 1).

---

## 7. Library tips: how to make artwork, logos, trailers and cast look good

FullUI shows what Jellyfin knows. Good Jellyfin metadata gives a good screen.

- **Name files the way Jellyfin expects.** Movies: `Movies/Movie Name (2019)/Movie Name (2019).mkv`. Shows: `Shows/Show Name (2015)/Season 01/Show Name - S01E01.mkv`. See Jellyfin's documentation on "movies" and "TV shows" naming. Wrong names lead to wrong or missing information.
- **Turn on the TMDB metadata provider** for your Movies and Shows libraries (Dashboard > Libraries > the library > "Metadata downloaders") and let a library scan finish. This gives each title a **TMDB id**, which FullUI uses to merge duplicate editions into one card, to hide Coming Soon titles you already own and to look up trailers. Titles without one are still shown but lose these benefits.
- **Backdrops (wide pictures) make the hero.** FullUI prefers a title that has both a backdrop and a trailer for the hero banner. Titles without a backdrop use the poster.
- **Logos** (the title's name as a picture) are used on the hero and cards when present. Jellyfin's image providers (TMDB, Fanart.tv) supply them; otherwise the title is written in text. Enable image fetching in the library settings.
- **Cast and directors** come from Jellyfin's metadata. FullUI uses them to compare titles and for people search.
- **Age ratings** (Official rating) need to be filled in for the kids test, parental filtering and the maturity badge to work.
- **Trailers.** FullUI plays **YouTube** trailers only. It uses a YouTube link stored on the title in Jellyfin (the TMDB provider adds them) and otherwise looks one up on TMDB itself for up to 200 titles per nightly run, when you have a TMDB key. **A trailer file next to the movie (a local trailer) is not played by FullUI**; it stays available in Jellyfin's own page. No trailer means the hero and card simply show the picture.
- **Genres**: titles with no genre are never dropped, but they carry less taste information.
- After big changes, press **Rebuild now** on the Health page so FullUI re-reads the library.

---

## 8. Friends and family outside your home

FullUI is part of the normal Jellyfin web page, so it works wherever Jellyfin does, including behind a reverse proxy and under a sub-address such as `/jellyfin`. Opening Jellyfin to the internet is a Jellyfin topic. Use Jellyfin's own documentation:

- Remote access and HTTPS / reverse proxy: <https://jellyfin.org/docs/> (look for "Networking" and "Reverse proxy").
- Limiting video quality for remote viewers (bitrate limits) is a per-user setting in Jellyfin; see the Jellyfin documentation for "Playback" and "Streaming" settings.

Things specific to FullUI: remote viewers' **browsers** load trailers from YouTube and Coming Soon posters from TMDB's image server, so those visitors need ordinary internet access to those sites (for trailers, turn them off in Settings if you prefer). The "classic view" button is a safe fallback for any visitor who has trouble.

---

## 9. Updating and uninstalling

- **Update FullUI:** run the newest installer again (it only changes what is out of date), or use Dashboard > Plugins > Catalog. Make an export first (section 5). Details: [INSTALL.md](INSTALL.md). After updating, open **FullUI Health** and check it is green.
- **Update Jellyfin:** stay inside 10.11.x (10.11.6 or later). After any Jellyfin update, open FullUI Health. If "FullUI home screen" is red, update the File Transformation plugin and restart Jellyfin.
- **Uninstall:** run the installer with `-Uninstall` (see INSTALL.md). Your settings, ratings and requests are kept unless you delete them. To erase everything, do a full **Purge** first (section 5), or follow the file-deleting steps in INSTALL.md.

---

## 10. Troubleshooting: the top 10

Start every time with **FullUI Health**, then try these.

1. **The new look does not show; it looks like plain Jellyfin.**
   Health says what is wrong. Usually: File Transformation is missing or not Active (Dashboard > Plugins), Jellyfin has not been restarted since installing, or the browser is showing an old copy: press **Ctrl+F5** (on a phone app, clear the app's cache). Remember the official Android TV / Fire TV app and Swiftfin never show FullUI. If someone has pressed "Use classic view", press **Switch to the new view** on the banner.
2. **Rows look wrong, generic or the same for everyone.**
   A brand-new account has "Popular on (name)" only; the rows personalise with use. Check the person watches while signed in as themselves, and that nothing is wrong on Health. Press **Rebuild now**. With only one or two users, the "popular in your household" and "similar taste" parts have little to work with.
3. **Some rows are missing.**
   By design: a row appears only when enough fitting titles exist (at least 3 to 5). A small library, or a library where titles lack genres, shows fewer rows. A finished movie or series never returns as a suggestion, and titles a person rejected are gone.
4. **Coming Soon is empty.**
   (a) No TMDB key (Health says *Not set up*). (b) Key rejected. (c) The nightly job has not run yet: press **Rebuild now** and check in a minute. (d) The person's parental limit hides what TMDB offers. (e) Everything upcoming that fits them is already in your library.
5. **Trailers do not play.**
   Trailers switched off in Settings; the title has no YouTube trailer (and no TMDB key to find one); the visitor's device is set to "reduce motion"; the browser or an ad blocker blocks YouTube; or the visitor is offline from YouTube. Try another title. A local trailer file is not used.
6. **Search finds nothing.**
   Check the title exists and is visible to that person. Wait for the library scan to finish. If you enabled AI search and it is slow or down, search should fall back to keyword search; check the Ollama line on Health. Searching a title that is only "Coming Soon" will not find it.
7. **Skip intro / next episode buttons never appear.**
   They need Jellyfin to hold "media segments" for the show, and they stay hidden if Jellyfin's own skip button is showing. See [USER_GUIDE.md](USER_GUIDE.md) section 11. Also check `PlayerAssistEnabled` has not been switched off.
8. **Installer problems.**
   The window explains what happened and is always safe to run again. A log file `FullUI-install-<date-time>.log` sits next to the installer. The 10 most common messages are listed in INSTALL.md.
9. **Settings page will not load or save, or Health says "You need to be signed in as an administrator".**
   Sign in again with an administrator account. If the pages do not appear in the Dashboard at all, restart Jellyfin and check Dashboard > Plugins shows FullUI as Active (not "Restart required" or "Malfunctioned"; the latter usually means a Jellyfin version outside 10.11.6 and later).
10. **A scheduled job shows red *Failed* or yellow.**
    Open Dashboard > Logs, find lines starting "FullUI:", press **Rebuild now**, and if it keeps failing send the log to whoever helps you. Most failures are TMDB or Ollama being unreachable, and the next run recovers by itself.

---

## 11. What FullUI cannot do

Taken honestly from the project plan. These are limits of being a Jellyfin plugin, not oversights:

- **No profiles inside one account.** Jellyfin has one profile per user. FullUI personalises per Jellyfin user and there is no "Who's watching?" picker. Create one Jellyfin user per person.
- **It cannot replace the video player.** FullUI draws a Skip Intro / Skip Recap / Skip Credits button and a next-episode countdown *on top of* Jellyfin's player and does nothing else to it. Playback, subtitles, audio and quality are Jellyfin's.
- **No phone push notifications.** Notifications are the bell inside the FullUI screen, seen when the person opens it. FullUI cannot buzz a phone.
- **No automatic downloading.** "I want this" collects requests for *you*; you decide what to add. FullUI never fetches, buys or downloads media, and it does not talk to download managers.
- **It does not appear in Jellyfin's native apps.** The official Android TV / Fire TV app and Swiftfin show Jellyfin's own screen (the optional "FullUI:" playlists are a small substitute). Phones, tablets and LG/Samsung TVs that use the Jellyfin web app do get FullUI.
- **Fire TV is paused / experimental.** The repository contains a separate Fire TV app (a fork of the open-source Wholphin), but its development is on hold and it is not part of the supported install. Do not rely on it.
- **The Dashboard pages are English only.** The watcher screens have English plus machine-assisted Spanish, French and German tables that follow each person's Jellyfin language; the wording of those translations has not been reviewed by a native speaker.
- **No Metrics, Import or Purge buttons yet** (API only, see section 5) and a few settings only in the settings file (section 1).
- **It is not a rating of quality.** Match % ranks titles for a person; it is not a star score and it hides itself when it does not know the person yet.
