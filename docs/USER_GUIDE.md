# FullUI user guide

This guide is for the people who watch. It explains what you see on the FullUI screens, what each button does, and what FullUI does and does not remember about you. You do not need to know anything technical.

If you run the server, the companion document is the [admin guide](ADMIN_GUIDE.md).

> **A note about versions.** FullUI is delivered in waves. A few screens in this guide are marked **(newer version)**: the in-app title page, New & Popular, "Pick your favourites", "Remind me" and "Remove from row". If you do not see them yet, your server is still on an earlier FullUI. Everything else here works in every version. Ask whoever runs your server to update (see the admin guide).

FullUI changes how the Jellyfin **home page** looks and behaves in a web browser, in the Jellyfin phone and tablet apps, and on TVs that run the Jellyfin web app (LG and Samsung). It does **not** change the video player, the library pages, the Dashboard, or the official Android TV / Fire TV app. Those stay exactly as Jellyfin made them.

---

## 1. The top bar

Across the top you will find, from left to right:

| Item | What it does |
|---|---|
| The server name (for example "MowFlix") | Click it to go back to Home. |
| **Home** | Your personal home screen, described below. |
| **Shows** | The same rows as Home, but only TV shows. |
| **Movies** | The same rows as Home, but only movies. |
| **My MowFlix** (your server's name) | Your own things in one place: Continue Watching, My List and the Coming Soon titles you said you want. |
| **Libraries** | A small menu with links to Jellyfin's own pages: Movies library, TV Shows library, Music, Live TV and Favorites. Use this to browse the *whole* library, because Home, Shows and Movies only show suggestions. |
| **Use classic view** | Switches back to the standard Jellyfin home page (see section 12). |
| Magnifying glass | Search. |
| Bell | Notifications, for example "Dune is now available". A number on the bell means unread ones. |
| Your round profile picture | A small menu with Settings (Jellyfin's own preferences page), Dashboard (only if you are an administrator) and Sign out. |

*What you will see:* a dark page with these items in a bar at the top. When you scroll down the bar becomes solid so it stays readable.

---

## 2. Home: the big picture at the top

The large picture at the top of Home is the **hero**. It shows one title that FullUI picked for you (it prefers one that has a trailer and a wide picture), with its logo or name, a short description, and two buttons:

- **Play** starts the title. If you have already started it, it continues where you stopped.
- **More Info** opens the title's page.

After a moment a **trailer** may start playing quietly behind the text. Small buttons in the corner let you:

- **Pause** the trailer (it stays paused until you press it again),
- **Mute / Unmute** it,
- **Replay** it after it ends (the picture fades back to the still image when the trailer finishes).

The trailer pauses by itself when you scroll the hero out of sight or switch to another browser tab, and it never starts if your device is set to "reduce motion".

*If no trailer appears:* the title may simply have no trailer, your administrator may have turned trailers off, or your browser blocked the YouTube player. Nothing is wrong with your server. See "Trailers and privacy" in section 13.

---

## 3. The rows on Home

Below the hero are rows of artwork you can scroll sideways. FullUI builds these rows **for you personally**, from what you watch, rate and put on My List. Different people in the same household see different rows. Rows only appear when there are enough good titles to fill them, so a small library may show fewer rows.

| Row | What it means |
|---|---|
| **Continue Watching** | Things you started and have not finished, newest first, with a red progress bar. Series show the next episode label (for example "S2:E5"), and a movie shows the time left. A series you just finished an episode of, with another episode ready, shows "Your next episode is ready". |
| **Next in (collection)** | You watched a film that belongs to a collection (a saga or trilogy). This row shows what comes next. Up to two of these rows. |
| **Top Picks for You** | The titles FullUI thinks fit you best. One slot in this row is deliberately **"Something different today"**: a title outside your usual taste so you can discover new things. It changes daily. For someone brand new, this row is called **Popular on (server name)** instead. |
| **Top 10 Movies / Top 10 Shows on (server name) This Week** | The most-watched titles in your household over the last 7 days (the administrator can change the window). Big numbers show the rank. Other people's *names* are never shown. |
| **Because you watched (title)** | Titles similar to something you finished or loved. Up to three of these rows. |
| **Trending Now** | What people on this server have been watching over the last two weeks, with the most recent plays counting most. |
| **Coming Soon** | Titles that have not been released yet (see section 6). |
| **My List** | Everything you added with the + button. |
| **(Genre) Picks for You** | Rows built around your favourite genres, for example "Sci-Fi Picks for You". If your administrator enabled the optional AI feature, these names can be friendlier. |
| **New Episodes** | A show you follow has a new season or new episodes since you last caught up. |
| **Recently Added** | Newly added to the server. |
| **Hidden Gems** | Well-rated titles that few people on this server have watched yet. |
| **Watch Again** | Things you finished at least a month ago, ordered by how much you enjoyed them. A series only appears here when you have finished all of it. |

Some rules apply to every row, so you will not see the same thing twice or something you rejected:

- A title you gave **Not for me** (thumbs down) disappears from every row.
- A movie you finished, or a series you are completely caught up on, does not come back as a suggestion (except in Watch Again).
- A series you started and clearly gave up on is not suggested again.
- The "recommended" rows (Top Picks, Because you watched, genre rows, Hidden Gems, Next in collection) never share a title between them. Charts, Continue Watching, My List and Recently Added are allowed to overlap.
- You only ever see titles you are allowed to see in Jellyfin. Parental limits and library access set by your administrator are always respected.
- If the same film exists twice on the server (for example two editions), it shows once.

Each row title may have an **Explore all** link on the right. It opens that row as a full grid of everything in it.

**Using the arrows:** hover over a row (mouse) and use the arrow buttons at the sides, scroll sideways with a trackpad, swipe on a touch screen, or use the left and right arrow keys.

---

## 4. Cards, the Match % and the reason line

A **card** is one title's picture. Move the mouse over it (or tap it, or focus it with the keyboard) and it grows into a small panel with:

- the title logo or name,
- **Play**, **+** (My List), three thumbs buttons, and **More info**,
- a line like **"97% Match  Because you watched Arrival"**,
- badges such as "Recently Added", "Top Rated", "#3 in Movies" or "New Episodes",
- the year, age rating (in a small box) and running time,
- a short description and the genres.

After a short pause the card's **trailer** may play quietly inside it. Move the mouse away and it shrinks again.

### How "Match %" and the reason line are decided (honest version)

FullUI keeps a taste profile for you. It is built from what you have watched (finished titles count most, half-watched titles count less, abandoned ones count slightly negative, a whole series counts by how many episodes you watched), what you rated, and what is on your My List. **Older activity fades**: it counts half as much after about three months. For every title you could still watch, FullUI then gives a score from how closely it matches that profile (genres, cast, directors, studios and similar details, with rarer details counting more), plus small amounts for how well it is rated, how new it is, how popular it is in your household, and, when at least two people on the server have enough history, what people with overlapping tastes enjoyed. If your administrator turned on the optional AI feature, a comparison of what the titles are *about* is blended in too.

**Match % is not a prediction that you will like it, and it is not a star rating.** It is a **rank**: "98%" means this title scored higher than about 98 out of 100 of the other titles you could still watch. That is why the numbers are mostly high on your own home page (the best candidates are shown first) and why a single number can shift slightly after you watch something. It is shown between 1% and 99%. **If FullUI does not know your taste yet (a brand-new account) or has too few titles to compare, it shows no percentage at all** rather than make one up.

The **reason line** is plain words for why a card is there, for example "Because you watched Arrival", "Popular in your household", "Top rated in Drama", "New season of Severance", "Pick up where you left off", "Next in Star Wars" or "Something different today". It is picked by the row, or, in Top Picks, by whichever title you finished that is most similar. If no honest reason exists, no line is shown.

---

## 5. Thumbs, My List, Continue Watching

### Telling FullUI what you like

On every card (and in the hero panel) there are three small thumb buttons:

| Button | What it means | What changes |
|---|---|---|
| Thumb down, **Not for me** | You do not want this. | It disappears from every row on your Home, and similar titles are suggested less. |
| Thumb up, **I like this** | Good. | More like it. |
| Heart, **Love this** | Great. | Counts the most, and also marks it as a Favorite in Jellyfin. |

Press the same button again to take it back.

*What you will see:* the button lights up, and your Home updates shortly afterwards. If saving fails, a short message says "Couldn't save your rating. Try again."

### My List

Press **+** to add a title to your private **My List** (it becomes a tick). Press it again to remove it. My List appears as a row on Home and in full under the **My (server name)** tab. Adding to My List also tells FullUI you are interested in that kind of title.

### Continue Watching, and taking a title off the row

Continue Watching lists the things you can pick up again. A title appears here when you have watched at least a few percent and not nearly all of it, within the last 90 days.

**Removing a title from the row (newer version).** If you do not want to see something there any more, use the **Remove from row** option on its card. This only hides it from *your* Continue Watching row. It does **not** delete your Jellyfin watch history, and it does not affect other rows or other people.

---

## 6. Coming Soon, "I want this", "Remind me"

The **Coming Soon** row shows titles that are **not out yet** (a film's release date or a show's first episode or new season is today or later). It needs the administrator to have entered a free TMDB key. Without one, there is no Coming Soon row. Titles you already have on the server are not shown, and titles already out are not "coming soon". Titles are filtered by your Jellyfin parental limit, so a child account does not see adult-rated ones.

Each Coming Soon card shows the poster and the release date, and two buttons:

- **I want this**: tells your administrator you would like this title on the server. The administrator sees how many people want each title (and, on the admin page, the *names* of the people who pressed it, but not who pressed "Not for me"). You are not promised anything: FullUI cannot download anything by itself. When the title appears in the library, you get a bell notification "(Title) is now available".
- **Not for me**: hides it for you and teaches FullUI to suggest fewer like it. Nobody else sees this.

Both are private to your account. Press a button again to undo.

Under the card's row you may also see a separate **Recommended for you (not in library)** group: films that are already out but not on the server, which you can still ask for. Your administrator can turn this off.

**Remind me (newer version).** On a Coming Soon title press **Remind me**. On the release day you get a bell notification "(Title) is out today", and when it later shows up on the server you get "(Title) is now on (server name)". Each reminder rings once. Reminders are private, and you can have up to 200.

*What you will see:* the bell in the top bar gets a number. Click it for the list; click an entry to open that title, or **Mark all read**.

---

## 7. New & Popular (newer version)

A separate page for "what is happening". It has:

- **Coming Soon**, grouped by release date, with Remind me,
- **Everyone's Watching**: what people on this server have played lately,
- **Top 10 Movies** and **Top 10 Shows**.

The Everyone's Watching and Top 10 lists are based on other people's plays but never show who they are. Users with a strict parental limit (children) are left out of these charts by default (see "kids" in the admin guide). Titles you gave Not for me are removed from your own lists.

---

## 8. Search

Click the magnifying glass.

1. Type at least two letters.
2. Results appear as you type, in a grid. A line above them says **Keyword search** or, if your administrator set up the optional AI feature, **Smart search (matches meaning, not just words)**, which also understands things like "a scary movie set in space".
3. With nothing typed you see your **recent searches** (the last eight, kept in this browser only; use the small x on one, or **Clear**) and some **genre shortcuts** you can click.
4. If nothing matches, you will see a short hint and a "You might like" row so the page is never a dead end. Check your spelling or try a shorter word.
5. Where the server supports it, results can be grouped into **People**, **Genres** and **Titles**, and small spelling mistakes are forgiven.

Search only finds titles you are allowed to see.

*If search finds nothing at all, even for a title you know exists:* see "Troubleshooting" in the admin guide. The usual causes are that the library has not finished scanning, or you are searching for something that is on the "Coming Soon" list rather than the server.

---

## 9. The title page (More Info)

Press **More Info** (the "i" button or the hero button) or click a card to open the title's page.

- In a version without the in-app title page, this opens **Jellyfin's own details page** for the title, which has the real Play button, episodes, cast and so on.
- **(newer version)** FullUI opens its own title page *over* Home so you do not lose your place. It shows the picture, **Match %** and reason, the description, **Play** (or **Resume**), seasons and episodes with a progress bar for each episode and a mark for watched ones, which episode comes **next up**, the cast and crew, any trailers, and **More Like This**. Press Escape or the close button to return to Home.

Pressing **Play** on a card or in the hero opens the title page and then presses Jellyfin's own Play for you. If that does not start, simply press Play on the page that opened.

---

## 10. First run: "Pick your favourites" (newer version)

When you open Home for the very first time and FullUI knows little about you (fewer than 5 titles of history), it may show a picker: a grid of about two dozen well-rated titles, spread across genres. Tick the ones you like (and genres you enjoy), then press the confirm button.

- Every title you tick counts as a thumbs up, and the genres are remembered, so your Home is useful immediately instead of generic.
- You can **skip**; it will not ask again.
- You can re-open it later from the menu if you want to try again.

---

## 11. Skip intro, skip recap, next episode

When you watch with Jellyfin's normal video player, FullUI can add two small helpers on top of it:

- **Skip Intro / Skip Recap / Skip Credits / Skip Preview**: a button appears in the corner when the intro (or recap, credits or preview) starts. Press it to jump to the end of that part.
- **Next episode**: near the end of an episode, a small box counts down ("Next episode in 10 seconds") with **Play now** and **Cancel**.

These helpers only draw buttons. They never change how the video plays except to jump forward when *you* press a button, and if anything about them goes wrong they switch themselves off for the session and leave Jellyfin's own player alone.

**If the buttons do not appear:**

1. **Jellyfin must know where the intro is.** The buttons come from Jellyfin's "media segments" information (a Jellyfin feature from version 10.10). If your Jellyfin has no segment data for that show, there is nothing to skip to, so no button. Where segment data comes from is a Jellyfin matter; your administrator can look at the Jellyfin documentation for "media segments".
2. **Jellyfin's own skip button wins.** If Jellyfin's player is already showing its own skip button or its own "Up next" box, FullUI keeps its buttons hidden so there are never two.
3. **The next-episode box needs a next episode.** It only appears when there is one, and only in the last 20 seconds.
4. **It may be turned off.** Your administrator has an off switch for this feature.
5. **Reload the page** (hold Ctrl and press F5). After repeated errors the helper turns itself off for the rest of that browser session.
6. This only works in the Jellyfin web player (browser, and apps that wrap it). The official Android TV / Fire TV app and other native apps never show FullUI.

---

## 12. "Use classic view"

The **Use classic view** button in the top bar (and on the banner that appears if something goes wrong) switches your Home back to the standard Jellyfin home screen. Use it if:

- you prefer Jellyfin's own home,
- FullUI's home will not load (it then offers this automatically, with a "Try again" button),
- an administrator or helper asks you to compare.

The banner at the top then says "You're using the classic view" with **Switch to the new view** to come back. The choice is remembered **per browser** (it is stored in that browser, not on your account), so it does not follow you to another device.

---

## 13. Privacy in plain words

### What FullUI stores about you (on the server)

FullUI keeps its own small data files in the Jellyfin data folder on your server. For **your** account it keeps:

- **Play history, in summary form**: which title, when, how much of it you watched, and the season and episode for series. (Jellyfin keeps its own, fuller history too; FullUI also reads that once to avoid starting cold.)
- **Your thumbs / hearts** and **My List**.
- **Coming Soon votes** ("I want this", "Not for me") and **reminders**.
- **Your bell notifications**.
- **Your first-run picks** (and whether you skipped them).
- **Titles you removed from Continue Watching.**
- **Usage events, if your administrator left this on** (the default): which rows were shown to you, which cards you opened or played, whether you watched a trailer, and **what you typed in search**, each tagged with your account. They are used to sort *your own* rows by what you actually click, and to give the administrator totals. Events older than 90 days are deleted automatically, and the file is size-limited.

In your **browser only** (not on the server): your recent searches and your "classic view" choice.

### Who can see what

- **Other users never see your ratings, My List, votes, reminders, notifications or searches.** The server only ever answers with *your own* data to *you*; the user is identified from your sign-in, not from anything the page sends.
- **Shared charts** (Top 10, Trending, Everyone's Watching) count how many *different people* played a title. They show titles, never names.
- **The administrator** can:
  - see on the **Most Requested** page how many people said "I want this" for each title, **with their names**. They cannot see who pressed "Not for me" there.
  - see only **totals** on the usage statistics (for example how often a row was clicked, and the most popular searches with how many people searched them), not per-person event lists through FullUI's screens.
  - download a **backup file** that contains each person's ratings, My List, votes, reminders, notifications and first-run picks, labelled with the user name. It is meant for backups. It does not contain play history.
  - read FullUI's data files directly, because they are files on the machine the administrator runs. This includes the play history and usage events described above. As with Jellyfin itself, whoever runs the server can in principle see what is on it. If this matters to you, talk to your administrator.
- FullUI never sends your history, ratings or searches to any outside company. The only outside services are **TMDB** (the server asks TMDB for upcoming titles and trailer ids; it sends the server administrator's key and the titles being looked up, not your name) and, if the administrator enabled it, **Ollama** running on the server's own computer.

### Trailers and YouTube

Trailers play from **YouTube** (using YouTube's "no-cookie" player address, youtube-nocookie.com). When a trailer starts, **your own browser contacts Google/YouTube** to load it, just as if you had opened a YouTube video: YouTube can see your network address and which trailer, and it is covered by Google's privacy rules, not FullUI's. FullUI's server does not relay or record this.

If you do not want that, ask your administrator to turn trailers off: in Jellyfin go to **Dashboard > Plugins > FullUI (Settings)** and untick **Play trailers automatically on the FullUI home screen**, then press **Save**. After that the YouTube player is not loaded at all. (You may need to reload the page.)

Likewise, the posters on **Coming Soon** cards (titles that are not on the server) are loaded by your browser from TMDB's picture server (image.tmdb.org), so TMDB can see your network address when those pictures load. Pictures of titles that are on the server come from your own server.

### Deleting your data

Ask your administrator. They can export or delete everything FullUI holds about one person, or about everyone (see "Backup, export, import and purge" in the admin guide). Deleting does not affect your Jellyfin account or Jellyfin's own watch history.

---

## 14. Quick answers

- **My Home looks the same as before.** The new screen may not be switched on. Press Ctrl+F5 once. If it still looks like plain Jellyfin, tell your administrator (they can check the FullUI Health page).
- **I rated something by mistake.** Press the same thumb again to clear it.
- **A row I like disappeared.** Rows only appear when there are enough fitting titles. It will come back as you watch more or the library grows.
- **I see a title I rejected.** Rejections apply on the next refresh; reload the page. If it persists in a *search*, that is expected: search shows everything you may see.
- **There is no Coming Soon row.** The administrator has not entered a TMDB key, or there is nothing new that fits you right now.
- **Who sees my "I want this"?** The administrator, with your name, next to the title. Nobody else.

TMDB: This product uses the TMDB API but is not endorsed or certified by TMDB.
