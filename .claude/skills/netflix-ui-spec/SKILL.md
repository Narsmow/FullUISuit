---
name: netflix-ui-spec
description: The visual and interaction spec for FullUI's Netflix-style (2025+ redesign) interface, shared by the web reskin and the Fire TV app. Use whenever building or styling the top nav, hero/billboard, rows, cards, expand-on-focus detail, badges, My List / "My <Server>" tab, Coming Soon cards, profile picker, trailer autoplay, or any CSS/Compose UI for this project, and whenever the user asks to make something look or behave more like Netflix.
---

# FullUI interface spec (Netflix 2025+ style)

Source: press coverage of Netflix's 2025 TV/web redesign (the user's screenshot never arrived and whats-on-netflix.com is blocked from the sandbox). Treat details below as the working spec, and update this file when the user supplies the screenshot or corrects something. Use our own name, logo and wordmark: never Netflix's logo, font or trademarks.

## Structure
- **Top nav, no sidebar**: Home · Shows · Movies · My <Server> · Search (+ notification bell, profile avatar). Search and My List live in the top bar.
- **Hero/billboard** at the top of Home: backdrop, title logo or text, 2-3 line synopsis, Play and More Info buttons, muted trailer autoplay after a short delay (YouTube only) with a mute toggle.
- **Rows**: title above a horizontally scrolling strip of 16:9 rounded cards. Personalized order per user (see plan: Top Picks, Because You Watched X, Top 10, Trending, Recently Added, genre rows, Coming Soon).
- **My <Server>** tab (replaces "My List"): Continue Watching, My List, "I want this" reminders.

## Card behavior
- Default: rounded corners (~8-12px), artwork only, optional badge ribbon.
- **Hover (web) or D-pad focus (TV)**: card expands in place and neighbors shift; after ~800 ms the trailer plays muted. Expanded card shows title logo, badges, year · rating · runtime, 2-line synopsis, buttons: Play, + My List, thumbs (3 levels: not for me / like / love), More Info.
- Touch: tap expands; long-press opens details. Reduce motion when `prefers-reduced-motion`.

## Badges (computed server-side, shown pre-click)
`#N in Shows/Movies` (Top 10 numbered row also), `New Season`, `New Episodes`, `Recently Added`, `Top Rated`, `Now on <Server>` (a requested title arrived).

## Coming Soon cards
Poster, release date, trailer on focus, two buttons: **I want this** / **Not for me** (not-for-me is a negative recommendation signal). Votes are private to each user.

## Visual tokens (start here, tune against the real screenshot)
- Background `#141414`, surface `#181818`, text `#fff`/`#b3b3b3`, accent `--fullui-accent` (default `#e50914`, configurable in plugin Settings).
- System sans-serif stack; headings 600-700 weight. Row title ~20px, card gap 8px, page gutter ~4% of width.

## Platform notes
- Web/mobile: CSS in `web/src/style.css`, responsive grid below ~700px.
- webOS/Tizen and Fire TV: focus ring + scale transform (no hover), spatial navigation, 10-foot sizing (min ~24px body text), safe-area padding ~48px.
- Keep the same data contract on every client: rows come from `GET /FullUI/Home`, so web and Fire TV render the same content and badges.

## Checklist before calling UI work done
Nav has no sidebar; hero shows a trailer or falls back to a backdrop; focus/hover expansion works with keyboard/D-pad; badges render; works at phone width; no Netflix trademarks used.
