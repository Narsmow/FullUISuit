---
name: netflix-ui-spec
description: The visual and interaction spec for FullUI's Netflix-style (2025+ redesign) interface, shared by the web reskin and the Fire TV app. Use whenever building or styling the top nav, hero/billboard, rows, cards, expand-on-focus detail, badges, My List / "My <Server>" tab, Coming Soon cards, profile picker, trailer autoplay, or any CSS/Compose UI for this project, and whenever the user asks to make something look or behave more like Netflix.
---

# FullUI interface spec (Netflix 2025+ style)

Source: press coverage of Netflix's 2025 TV/web redesign (the user's screenshot never arrived and whats-on-netflix.com is blocked from the sandbox). Treat details below as the working spec, and update this file when the user supplies the screenshot or corrects something. Use our own name, logo and wordmark: never Netflix's logo, font or trademarks, and no 'Netflix' in any public text (plugin description, manifest, README): say "a streaming-style interface". This folder keeps its historical name.

## Structure
- **Top nav, no sidebar**: Home · Shows · Movies · My <Server> · Search (+ notification bell, profile avatar). Search and My List live in the top bar.
- **Hero/billboard** at the top of Home: backdrop, title logo or text, 2-3 line synopsis, Play and More Info buttons, muted trailer autoplay after a short delay (YouTube only) with a mute toggle.
- **Rows**: title above a horizontally scrolling strip of 16:9 rounded cards. Personalized order per user (see plan: Top Picks, Because You Watched X, Top 10, Trending, Recently Added, genre rows, Coming Soon).
- **My <Server>** tab (replaces "My List"): Continue Watching, My List, "I want this" titles.
- **New & Popular** page (wave 2): Coming Soon grouped by release date with a per-title **Remind me**, Everyone's Watching, Top 10 Movies, Top 10 Shows. Server: `GET NewPopular`, `POST Remind`; kids and `ExcludedUserIds` are skipped in the charts.

## Card behavior
- Default: rounded corners (~8-12px), artwork only, optional badge ribbon.
- **Hover (web) or D-pad focus (TV)**: card expands in place and neighbors shift; after ~800 ms the trailer plays muted. Expanded card shows title logo, badges, year · rating · runtime, 2-line synopsis, buttons: Play, + My List, thumbs (3 levels: not for me / like / love), More Info.
- Touch: tap expands; long-press opens details. Reduce motion when `prefers-reduced-motion`.

## Badges (computed server-side, shown pre-click)
`#N in Shows/Movies` (Top 10 numbered row also), `New Season`, `New Episodes`, `Recently Added`, `Top Rated`, `Now on <Server>` (a requested title arrived).

## Coming Soon cards
Poster, release date, trailer on focus, two buttons: **I want this** / **Not for me** (not-for-me is a negative recommendation signal). Votes are private to each user.

## Visual tokens (start here, tune against the real screenshot)
- Background `#141414`, surface `#181818`, text `#fff`/`#b3b3b3`, accent `--fullui-accent` (default **`#e5383b`**, deliberately not the exact Netflix red; configurable in plugin Settings, validated as `#` + 3, 4, 6 or 8 hex digits, invalid falls back to the default).
- System sans-serif stack; headings 600-700 weight. Row title ~20px, card gap 8px, page gutter ~4% of width.

## Match % and the reason line (explanations)
- Every card may carry `matchPercent` (1 to 99) and `reason`. Show them in the hero, the expanded card and the detail modal as a green "97% Match" followed by the reason ("Because you watched X", "Popular in your household", "Top rated in Drama", "New season of X", "Next in <Collection>", "Something different today").
- `matchPercent` is a **percentile of the ranker score among the titles the user could still watch**, not a probability. It is `null` on cold start or for tiny libraries: then draw nothing (never invent a number). Do not display it for charts without a reason either; render whichever of the two exists.
- Continue Watching cards show `seriesLabel` ("S2:E5") and `minutesLeft` ("1h 12m left") next to the progress bar; chart cards show a "#N today" caption.

## Detail modal (wave 2)
- Opened by More Info, a card click or a notification; drawn over Home (not a route change that reloads Home), deep-linkable, closes on Escape/close and returns focus to the card.
- Content comes from `GET Item/{id}/Details`: ItemCard (with match % and reason), tagline, seasons with episodes (per-episode progress, watched mark, `nextUp`), cast and crew, trailers, "More Like This" (engine neighbours). Play/Resume goes through the same safe hand-off as the card Play button (open the native details page and press its own Play).
- Until the route exists the client falls back to the native details page (`#/details?id=`).

## Onboarding and Continue Watching
- First-run "pick favourites": modal on the first Home visit when `GET Onboarding` says `eligible`; skippable, re-openable from the profile menu (`force=true`). No avatar choice (Jellyfin does that; it would edge toward profiles).
- Continue Watching cards get "Remove from row" (`POST ContinueWatching/Hide`): hides only from that row for that user, never deletes Jellyfin history.

## Player assist rules (skip intro / next episode)
These are hard rules; the feature must never be able to break playback.
- Runs only on the native video route; only while `Status.playerAssistEnabled !== false`; auto-disables for the session after 3 caught errors; removed on route change, pagehide and video end.
- Read-only: it observes the `<video>`; the only native state it changes is `currentTime = segment.end` on an explicit click of our button, plus one click on the native `.btnNextTrack` (never `btnNextChapter`). If no native next control exists, no "Next episode" button is shown.
- Segments come from Jellyfin's Media Segments API (Intro, Recap, Outro, Preview). If Jellyfin's own skip button or "up next" box is visible, ours stays hidden.
- Overlay is a separate fixed container, `pointer-events:none` except its own buttons, below the native controls; zero DOM changes to native elements; everything in try/catch; countdown is 10 s and starts in the last 20 s.
- Must be covered by fault-injection e2e (missing video, missing segments, throwing handlers) proving the native player is untouched.

## Hero rules
Pause when the tab is hidden or the hero is scrolled out of view, explicit pause always wins (WCAG 2.2.2), replay after the end, trailers off for `prefers-reduced-motion` and when the admin switch `trailersEnabled` is false.

## Platform notes
- Web/mobile: CSS in `web/src/style.css`, responsive grid below ~700px.
- webOS/Tizen and Fire TV: focus ring + scale transform (no hover), spatial navigation, 10-foot sizing (min ~24px body text), safe-area padding ~48px.
- Keep the same data contract on every client: rows come from `GET /FullUI/Home`, so web and Fire TV render the same content and badges.

## Checklist before calling UI work done
Nav has no sidebar; hero shows a trailer or falls back to a backdrop; focus/hover expansion works with keyboard/D-pad; badges render; Match % and reason appear only when the server sent them; works at phone width; every user-visible string comes from `web/src/i18n.ts`; the TMDB sentence is shown wherever TMDB data is; no Netflix trademarks used.
