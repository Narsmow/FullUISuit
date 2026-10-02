// Mock Jellyfin (jellyfin-web-like page) + mock FullUI API implementing docs/api-contract.md.
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { ComingSoonCard, HomeRow, ItemCard, NotificationDto } from '../src/types';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, '..', 'dist');

/** Item ids on the wire are Jellyfin Guids (32 hex digits); tests refer to items by a short key. */
export const G = (key: string): string => {
  const hex = Array.from(key).map((c) => c.charCodeAt(0).toString(16)).join('');
  return ('0'.repeat(32) + hex).slice(-32);
};

const item = (id: string, name: string, o: Partial<ItemCard> = {}): ItemCard => ({
  id,
  name,
  type: 'Movie',
  year: 2021,
  rating: 7.8,
  rated: 'PG-13',
  overview: `Synopsis for ${name}. A long enough description to need clamping, with several sentences. It keeps going for a while so that two lines are definitely not enough room to show everything.`,
  genres: ['Drama', 'Sci-Fi'],
  runtimeMinutes: 112,
  badges: [],
  trailerKey: 'dQw4w9WgXcQ',
  tmdbId: 100,
  progress: null,
  hasBackdrop: true,
  hasLogo: false,
  myRating: 0,
  inMyList: false,
  rank: null,
  ...o,
});

export const ITEMS: ItemCard[] = [
  item(G('a1'), 'Hero <b>Movie</b>', { badges: ['New'], hasLogo: false }),
  item(G('a2'), 'Second Movie', { trailerKey: 'abcdefghijk' }),
  item(G('a3'), 'Third Movie', { trailerKey: null }),
  item(G('s1'), 'Show One', { type: 'Series', badges: ['New Season'], runtimeMinutes: null }),
  item(G('s2'), 'Show Two', { type: 'Series' }),
  item(G('s3'), 'Show Three', { type: 'Series', hasBackdrop: false }),
  item(G('c1'), 'Continue Me', { type: 'Series', progress: 0.4 }),
  item(G('d1'), 'Dune Part Two', { badges: ['Top Rated'] }),
];

const SOON: ComingSoonCard[] = [
  { tmdbId: 501, mediaType: 'movie', title: 'Future Movie', overview: 'Soon', posterPath: '/f.jpg', backdropPath: '/fb.jpg', releaseDate: '2027-03-05', trailerKey: null, myVote: 0 },
  { tmdbId: 502, mediaType: 'tv', title: 'Future Show', overview: 'Soon', posterPath: '/g.jpg', backdropPath: null, releaseDate: '2027-04-01', trailerKey: null, myVote: 0 },
];

export interface Call {
  method: string;
  path: string;
  body?: unknown;
  auth: string | undefined;
  user: string | undefined;
}

export const GUID_RE = /^[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}$/i;

/** Two accounts so tests can switch users. Token -> user. */
export const USERS: Record<string, { id: string; name: string; admin: boolean }> = {
  tok: { id: G('u1'), name: 'Sam', admin: true },
  tok2: { id: G('u2'), name: 'Alex', admin: false },
};

export class Mock {
  server!: Server;
  port = 0;
  calls: Call[] = [];
  /** Requests for images (Items/.. and Users/..), by path. */
  images: string[] = [];
  failHome = false;
  homeStatus = 500;
  /** Return an empty `rows` array with HTTP 200 (what the server does when a Home build fails). */
  emptyHome = false;
  failSearch = false;
  failNotifications = false;
  /** Fields merged into the Status response (e.g. trailersEnabled:false). */
  statusExtra: Record<string, unknown> = {};
  ollamaEnabled = false;
  accent = '#e50914';
  /** Serve everything under this path prefix (e.g. '/jellyfin'), like a reverse proxy with a base URL. */
  base = '';
  /** Add the experimental-layout (MUI) chrome to the served page. */
  experimental = false;
  /** Initial token in the served page; null = signed out (login page). */
  token: string | null = 'tok';
  notes: NotificationDto[] = [];
  ratings = new Map<string, number>();
  list = new Set<string>();
  votes = new Map<number, number>();
  /** Artificial delay (ms) on Rate, to test rapid clicks. */
  rateDelay = 0;

  reset(): void {
    this.calls = [];
    this.images = [];
    this.failHome = false;
    this.homeStatus = 500;
    this.emptyHome = false;
    this.failSearch = false;
    this.failNotifications = false;
    this.statusExtra = {};
    this.ollamaEnabled = false;
    this.accent = '#e50914';
    this.base = '';
    this.experimental = false;
    this.token = 'tok';
    this.rateDelay = 0;
    this.ratings.clear();
    this.list.clear();
    this.votes.clear();
    this.notes = [
      { id: '11111111-1111-1111-1111-111111111111', text: 'Future Movie is now on MowFlix', at: new Date(Date.now() - 3600_000).toISOString(), read: false, itemId: G('a2') },
      { id: '22222222-2222-2222-2222-222222222222', text: 'New season of Show One', at: new Date(Date.now() - 86400_000).toISOString(), read: false, itemId: G('s1') },
      { id: '33333333-3333-3333-3333-333333333333', text: 'Welcome!', at: new Date(Date.now() - 5 * 86400_000).toISOString(), read: true, itemId: null },
    ];
  }

  private view(i: ItemCard, extra: Partial<ItemCard> = {}): ItemCard {
    return { ...i, myRating: this.ratings.get(i.id) ?? 0, inMyList: this.list.has(i.id), ...extra };
  }
  private soon(): ComingSoonCard[] {
    return SOON.map((c) => ({ ...c, myVote: this.votes.get(c.tmdbId) ?? 0 }));
  }
  home(user = 'Sam') {
    const v = (id: string, extra: Partial<ItemCard> = {}) => this.view(ITEMS.find((i) => i.id === G(id))!, extra);
    if (this.emptyHome) return { serverName: 'MowFlix', accentColor: this.accent, rows: [] as HomeRow[] };
    const rows: HomeRow[] = [
      { id: 'continue', title: 'Continue Watching', type: 'continue', items: [v('c1')] },
      { id: 'toppicks', title: `Top Picks for ${user}`, type: 'toppicks', items: [v('a1'), v('a2'), v('s1'), v('a3'), v('s2'), v('d1')] },
      { id: 'top10-movies', title: 'Top 10 Movies Today', type: 'top10', items: [v('a1', { rank: 1 }), v('a2', { rank: 2 }), v('a3', { rank: 3 })] },
      { id: 'trending', title: 'Trending Now', type: 'trending', items: [v('s1'), v('s2'), v('s3'), v('a2')] },
      { id: 'comingsoon', title: 'Coming Soon', type: 'comingsoon', items: [], comingSoon: this.soon() },
      { id: 'empty', title: 'Empty', type: 'recent', items: [] },
    ];
    return { serverName: 'MowFlix', accentColor: this.accent, rows };
  }

  async start(): Promise<void> {
    this.reset();
    this.server = createServer((req, res) => void this.handle(req, res));
    await new Promise<void>((r) => this.server.listen(0, '127.0.0.1', r));
    this.port = (this.server.address() as { port: number }).port;
  }
  stop(): Promise<void> {
    return new Promise((r) => this.server.close(() => r()));
  }
  get origin(): string {
    return `http://127.0.0.1:${this.port}`;
  }
  callsTo(method: string, path: string): Call[] {
    return this.calls.filter((c) => c.method === method && c.path === path);
  }

  private json(res: ServerResponse, code: number, body: unknown): void {
    res.writeHead(code, { 'content-type': code >= 400 ? 'application/problem+json' : 'application/json' });
    res.end(JSON.stringify(body));
  }
  /** Jellyfin returns 204 No Content for actions without a body. */
  private noContent(res: ServerResponse): void {
    res.writeHead(204);
    res.end();
  }
  /** ASP.NET ProblemDetails (framework-generated errors, e.g. 500). */
  private problem(res: ServerResponse, status: number): void {
    this.json(res, status, { type: 'https://tools.ietf.org/html/rfc9110', title: status === 401 ? 'Unauthorized' : 'An error occurred', status });
  }
  /** The plugin's own `{ message }` error shape (400/404). */
  private message(res: ServerResponse, status: number, message: string): void {
    this.json(res, status, { message });
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const url = new URL(req.url || '/', this.origin);
    let p = url.pathname;
    if (this.base) {
      if (p !== this.base && !p.startsWith(this.base + '/')) {
        res.writeHead(404); // a root-absolute URL that ignores the base path is a bug: make it visible
        res.end();
        return;
      }
      p = p.slice(this.base.length) || '/';
    }
    if (p === '/' || p === '/index.html') {
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end(page({ base: this.base, token: this.token, experimental: this.experimental }));
      return;
    }
    if (p === '/fullui.js' || p === '/fullui.css') {
      res.writeHead(200, { 'content-type': p.endsWith('js') ? 'text/javascript' : 'text/css' });
      res.end(readFileSync(join(dist, p)));
      return;
    }
    if (p.startsWith('/Items/') || p.startsWith('/Users/')) {
      this.images.push(p + url.search);
      const hue = [...p].reduce((a, c) => a + c.charCodeAt(0), 0) % 360;
      res.writeHead(200, { 'content-type': 'image/svg+xml' });
      res.end(`<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="hsl(${hue},45%,35%)"/></svg>`);
      return;
    }
    if (!p.startsWith('/FullUI/')) {
      res.writeHead(404);
      res.end();
      return;
    }
    let body: unknown;
    if (req.method === 'POST') {
      const chunks: Buffer[] = [];
      for await (const c of req) chunks.push(c as Buffer);
      const txt = Buffer.concat(chunks).toString();
      body = txt ? JSON.parse(txt) : undefined;
    }
    const route = p.slice('/FullUI/'.length);
    const auth = req.headers['authorization'] as string | undefined;
    const tokMatch = /^MediaBrowser Token="([^"]*)"$/.exec(auth || '');
    const user = tokMatch ? USERS[tokMatch[1]] : undefined;
    this.calls.push({ method: req.method || 'GET', path: route + (route === 'Search' ? '?q=' + (url.searchParams.get('q') || '') : ''), body, auth, user: user?.id });
    if (!user) return this.problem(res, 401);

    const b = (body || {}) as Record<string, unknown>;
    const needGuid = (v: unknown): boolean => {
      if (typeof v === 'string' && GUID_RE.test(v)) return true;
      this.message(res, 400, 'The value is not a valid Guid.');
      return false;
    };
    switch (`${req.method} ${route}`) {
      case 'GET Status':
        return this.json(res, 200, { serverName: 'MowFlix', accentColor: this.accent, tmdbConfigured: true, ollamaEnabled: this.ollamaEnabled, ...this.statusExtra });
      case 'GET Home':
        if (this.failHome) return this.problem(res, this.homeStatus);
        return this.json(res, 200, this.home(user.name));
      case 'POST Rate':
        if (!needGuid(b.itemId)) return;
        if (this.rateDelay) await new Promise((r) => setTimeout(r, this.rateDelay));
        this.ratings.set(String(b.itemId), Number(b.rating));
        return this.noContent(res);
      case 'POST MyList':
        if (!needGuid(b.itemId)) return;
        if (b.add) this.list.add(String(b.itemId));
        else this.list.delete(String(b.itemId));
        return this.noContent(res);
      case 'POST Vote':
        this.votes.set(Number(b.tmdbId), Number(b.vote));
        return this.noContent(res);
      case 'GET MyServer':
        return this.json(res, 200, {
          continueWatching: [this.view(ITEMS.find((i) => i.id === G('c1'))!)],
          myList: ITEMS.filter((i) => this.list.has(i.id)).map((i) => this.view(i)),
          wanted: this.soon().filter((c) => c.myVote === 1),
        });
      case 'GET Search': {
        if (this.failSearch) return this.problem(res, 500);
        const q = (url.searchParams.get('q') || '').toLowerCase();
        return this.json(res, 200, { mode: this.ollamaEnabled ? 'semantic' : 'keyword', items: ITEMS.filter((i) => i.name.toLowerCase().includes(q)).map((i) => this.view(i)) });
      }
      case 'GET Notifications':
        if (this.failNotifications) return this.problem(res, 500);
        // the second account has no notifications
        return this.json(res, 200, { items: user.id === USERS.tok.id ? this.notes : [] });
      case 'POST Notifications/Read': {
        const ids = b.ids as string[] | null | undefined;
        if (Array.isArray(ids) && ids.some((i) => !GUID_RE.test(i))) return this.message(res, 400, 'The value is not a valid Guid.');
        // null or an empty list both mean "all" (docs/api-contract.md)
        this.notes = this.notes.map((n) => (!ids || ids.length === 0 || ids.includes(n.id) ? { ...n, read: true } : n));
        return this.noContent(res);
      }
      default:
        return this.message(res, 404, 'Not found');
    }
  }
}

interface PageOpts {
  base: string;
  token: string | null;
  experimental: boolean;
}

/** Minimal stand-in for jellyfin-web: native chrome, hash routing, viewshow events, ApiClient. */
function page(o: PageOpts): string {
  const user = o.token ? USERS[o.token] : undefined;
  return `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Jellyfin (mock)</title><link rel="stylesheet" href="${o.base}/fullui.css"></head>
<body style="margin:0;background:#101010;color:#ddd;font-family:sans-serif">
<div class="skinHeader" id="nativeHeader" style="padding:10px;background:#202020">Native header <a href="#/home">home</a> <a href="#/dashboard">dashboard</a></div>
<div class="mainDrawer" id="nativeDrawer">Native drawer</div>
${o.experimental ? '<header class="MuiAppBar-root" id="muiAppBar" style="padding:10px;background:#303030">Experimental app bar</header><div class="MuiDrawer-root" id="muiDrawer">Experimental drawer</div>' : ''}
<div class="mainAnimatedPages" style="padding:20px">
  <div id="nativeHome" class="page">Native home page</div>
  <div id="nativeDetails" class="page" hidden>Native details page <button class="btnPlay" id="nativePlay" onclick="window.__played=(window.__played||0)+1">Play</button></div>
  <div id="nativeDash" class="page" hidden>Native dashboard</div>
  <div id="nativeLogin" class="page" hidden>Native login</div>
</div>
<script>
window.__logout = 0;
window.__token = ${JSON.stringify(o.token)};
window.__users = ${JSON.stringify(USERS)};
var BASE = ${JSON.stringify(o.base)};
// Same contract as jellyfin-apiclient 1.11: an empty name throws.
window.ApiClient = {
  serverAddress: function () { return location.origin + BASE; },
  getUrl: function (name) {
    if (!name) throw new Error('Url name cannot be empty');
    return this.serverAddress() + (name.charAt(0) === '/' ? '' : '/') + name;
  },
  accessToken: function () { return window.__token; },
  getCurrentUserId: function () { var u = window.__users[window.__token]; return u ? u.id : null; },
  getCurrentUser: function () { var u = window.__users[window.__token]; return Promise.resolve({ Name: u ? u.name : '', Policy: { IsAdministrator: !!(u && u.admin) } }); },
  logout: function () { window.__logout++; window.__token = null; return Promise.resolve(); }
};
// Dashboard.logout() is what jellyfin-web uses: clears the session and navigates to the login page.
window.Dashboard = { logout: function () { window.__logout++; window.__token = null; location.hash = '#/login'; return Promise.resolve(); } };
function route() {
  var h = location.hash;
  document.getElementById('nativeHome').hidden = !/^#\\/(home|$)/.test(h) && h !== '';
  document.getElementById('nativeDetails').hidden = h.indexOf('#/details') !== 0;
  document.getElementById('nativeDash').hidden = h.indexOf('#/dashboard') !== 0;
  document.getElementById('nativeLogin').hidden = h.indexOf('#/login') !== 0;
  document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true }));
}
window.addEventListener('hashchange', route);
if (!location.hash) location.hash = ${JSON.stringify(user ? '#/home' : '#/login')};
</script>
<script src="${o.base}/fullui.js"></script>
<script>route();</script>
</body></html>`;
}

/** Stand-in for https://www.youtube.com/iframe_api (the sandbox cannot reach YouTube). */
export const YT_STUB = `
window.__yt = Object.assign({ created: 0, alive: 0, muted: [], failNext: false, frames: [] }, window.__yt || {});
window.YT = { Player: function (el, opts) {
  // The plugin creates the <iframe> itself and attaches the API to it (like the real YT.Player).
  if (el.tagName !== 'IFRAME') throw new Error('expected the plugin-created iframe');
  var m = /\\/embed\\/([^?]+)/.exec(el.getAttribute('src') || '');
  window.__yt.frames.push({
    src: el.getAttribute('src'), referrerpolicy: el.getAttribute('referrerpolicy'), allow: el.getAttribute('allow')
  });
  el.classList.add('yt-stub');
  el.setAttribute('data-video', m ? m[1] : '');
  window.__yt.created++; window.__yt.alive++;
  var self = this;
  this.mute = function () { window.__yt.muted.push(true); };
  this.unMute = function () { window.__yt.muted.push(false); };
  this.playVideo = function () {};
  this.destroy = function () { el.remove(); window.__yt.alive--; };
  setTimeout(function () {
    if (window.__yt.failNext) { window.__yt.failNext = false; opts.events.onError({ data: 150 }); return; }
    opts.events.onReady({ target: self });
    opts.events.onStateChange({ data: 1 });
  }, 30);
} };
setTimeout(function () { window.onYouTubeIframeAPIReady && window.onYouTubeIframeAPIReady(); }, 0);
`;
