---
name: web-e2e-testing
description: How to write and run tests for the FullUI web bundle (vitest unit tests and Playwright end-to-end tests against a mock jellyfin-web page and mock FullUI API), and how to keep mocks faithful to the real jellyfin-web, ApiClient and Jellyfin server. Use whenever you edit anything in web/, add an e2e case, see a web test pass that you doubt, or debug a bug that only appears on a real Jellyfin.
---

# Testing the web bundle

## Commands (from `web/`)
`npm run typecheck && npm test && npm run build && npm run e2e`. Chromium is pre-installed (`/opt/pw-browsers`); never run `playwright install`. The e2e suite serves a mock jellyfin-web page and a mock API from `web/e2e/mock-server.ts`.

## The lesson that shaped this skill
Two critical bugs shipped with fully green tests because the mocks were more forgiving than reality:
- Real `ApiClient.getUrl('')` (jellyfin-apiclient 1.11) throws "Url name cannot be empty"; the mock returned a string, so every request in production failed.
- Real Jellyfin serializes plugin responses PascalCase unless the action sets camelCase; the mock returned camelCase for everything.
A mock that cannot fail is a false guarantee. When you add or change a mock behaviour, ask "what does the real thing do that would break my code?" and make the mock do that.

## Fidelity rules for the mocks
- `ApiClient`: `getUrl` throws on an empty name; `serverAddress()`, `accessToken()`, `getCurrentUserId()` behave as in jellyfin-apiclient; `logout()` clears the token and does NOT navigate; `Dashboard.logout()` does.
- jellyfin-web router: `/home` re-renders even when only the query changes; other routes return early when the pathname is unchanged; `viewshow` fires per view.
- Server: item ids are Guids (reject others); 401/500 return ProblemDetails, 400/404 return `{message}`; empty POST responses are 204; unauthenticated calls return 401; the site may be served under a base path (`/jellyfin`) and the mock must 404 URLs that ignore it.
- Pre-login pages send no FullUI request at all (assert zero requests while signed out).

## What every user-visible failure must do
Show plain-English text with a Try again button and a "Use classic view" path; never a blank screen, status code or stack text. Each failure state has an e2e case.

## Security checks in tests
Server strings (titles, overviews, notes, usernames) are untrusted: use `textContent`/DOM APIs, never `innerHTML`. The XSS unit test must actually build the expanded `.fui-info` panel (it once did not). Access tokens never appear in URLs, logs or localStorage.

## Still needs a real jellyfin-web
Play button selectors (`.btnPlay`/`.btnResume`), overlay hide/show timing, real YouTube playback, and the `#/login` fallback cannot be proven by the mocks. List such items under "needs a real Jellyfin" in `docs/BUGS.md` instead of claiming them fixed.
