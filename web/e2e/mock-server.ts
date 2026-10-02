// Mock Jellyfin (jellyfin-web-like page) + mock FullUI API implementing docs/api-contract.md.
import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { ComingSoonCard, HomeRow, ItemCard, NotificationDto } from '../src/types';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, '..', 'dist');

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
  item('a1', 'Hero <b>Movie</b>', { badges: ['New'], hasLogo: false }),
  item('a2', 'Second Movie', { trailerKey: 'abcdefghijk' }),
  item('a3', 'Third Movie', { trailerKey: null }),
  item('s1', 'Show One', { type: 'Series', badges: ['New Season'], runtimeMinutes: null }),
  item('s2', 'Show Two', { type: 'Series' }),
  item('s3', 'Show Three', { type: 'Series', hasBackdrop: false }),
  item('c1', 'Continue Me', { type: 'Series', progress: 0.4 }),
  item('d1', 'Dune Part Two', { badges: ['Top Rated'] }),
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
}

export class Mock {
  server!: Server;
  port = 0;
  calls: Call[] = [];
  failHome = false;
  homeStatus = 500;
  failSearch = false;
  failNotifications = false;
  notes: NotificationDto[] = [];
  ratings = new Map<string, number>();
  list = new Set<string>();
  votes = new Map<number, number>();

  reset(): void {
    this.calls = [];
    this.failHome = false;
    this.homeStatus = 500;
    this.failSearch = false;
    this.failNotifications = false;
    this.ratings.clear();
    this.list.clear();
    this.votes.clear();
    this.notes = [
      { id: '11111111-1111-1111-1111-111111111111', text: 'Future Movie is now on MowFlix', at: new Date(Date.now() - 3600_000).toISOString(), read: false, itemId: 'a2' },
      { id: '22222222-2222-2222-2222-222222222222', text: 'New season of Show One', at: new Date(Date.now() - 86400_000).toISOString(), read: false, itemId: 's1' },
      { id: '33333333-3333-3333-3333-333333333333', text: 'Welcome!', at: new Date(Date.now() - 5 * 86400_000).toISOString(), read: true, itemId: null },
    ];
  }

  private view(i: ItemCard, extra: Partial<ItemCard> = {}): ItemCard {
    return { ...i, myRating: this.ratings.get(i.id) ?? 0, inMyList: this.list.has(i.id), ...extra };
  }
  private soon(): ComingSoonCard[] {
    return SOON.map((c) => ({ ...c, myVote: this.votes.get(c.tmdbId) ?? 0 }));
  }
  home() {
    const v = (id: string, extra: Partial<ItemCard> = {}) => this.view(ITEMS.find((i) => i.id === id)!, extra);
    const rows: HomeRow[] = [
      { id: 'continue', title: 'Continue Watching', type: 'continue', items: [v('c1')] },
      { id: 'toppicks', title: 'Top Picks for Sam', type: 'toppicks', items: [v('a1'), v('a2'), v('s1'), v('a3'), v('s2'), v('d1')] },
      { id: 'top10-movies', title: 'Top 10 Movies Today', type: 'top10', items: [v('a1', { rank: 1 }), v('a2', { rank: 2 }), v('a3', { rank: 3 })] },
      { id: 'trending', title: 'Trending Now', type: 'trending', items: [v('s1'), v('s2'), v('s3'), v('a2')] },
      { id: 'comingsoon', title: 'Coming Soon', type: 'comingsoon', items: [], comingSoon: this.soon() },
      { id: 'empty', title: 'Empty', type: 'recent', items: [] },
    ];
    return { serverName: 'MowFlix', accentColor: '#e50914', rows };
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
    res.writeHead(code, { 'content-type': 'application/json' });
    res.end(JSON.stringify(body));
  }

  private async handle(req: IncomingMessage, res: ServerResponse): Promise<void> {
    const url = new URL(req.url || '/', this.origin);
    const p = url.pathname;
    if (p === '/' || p === '/index.html') {
      res.writeHead(200, { 'content-type': 'text/html' });
      res.end(PAGE);
      return;
    }
    if (p === '/fullui.js' || p === '/fullui.css') {
      res.writeHead(200, { 'content-type': p.endsWith('js') ? 'text/javascript' : 'text/css' });
      res.end(readFileSync(join(dist, p)));
      return;
    }
    if (p.startsWith('/Items/') || p.startsWith('/Users/')) {
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
    this.calls.push({ method: req.method || 'GET', path: route + (route === 'Search' ? '?q=' + (url.searchParams.get('q') || '') : ''), body, auth });
    if (auth !== 'MediaBrowser Token="tok"') return this.json(res, 401, { error: 'unauthorized' });

    const b = body as Record<string, unknown>;
    switch (`${req.method} ${route}`) {
      case 'GET Status':
        return this.json(res, 200, { serverName: 'MowFlix', accentColor: '#e50914', tmdbConfigured: true, ollamaEnabled: false });
      case 'GET Home':
        if (this.failHome) return this.json(res, this.homeStatus, { error: 'boom' });
        return this.json(res, 200, this.home());
      case 'POST Rate':
        this.ratings.set(String(b.itemId), Number(b.rating));
        return this.json(res, 200, {});
      case 'POST MyList':
        if (b.add) this.list.add(String(b.itemId));
        else this.list.delete(String(b.itemId));
        return this.json(res, 200, {});
      case 'POST Vote':
        this.votes.set(Number(b.tmdbId), Number(b.vote));
        return this.json(res, 200, {});
      case 'GET MyServer':
        return this.json(res, 200, {
          continueWatching: [this.view(ITEMS.find((i) => i.id === 'c1')!)],
          myList: ITEMS.filter((i) => this.list.has(i.id)).map((i) => this.view(i)),
          wanted: this.soon().filter((c) => c.myVote === 1),
        });
      case 'GET Search': {
        if (this.failSearch) return this.json(res, 500, {});
        const q = (url.searchParams.get('q') || '').toLowerCase();
        return this.json(res, 200, { mode: 'semantic', items: ITEMS.filter((i) => i.name.toLowerCase().includes(q)).map((i) => this.view(i)) });
      }
      case 'GET Notifications':
        if (this.failNotifications) return this.json(res, 500, {});
        return this.json(res, 200, { items: this.notes });
      case 'POST Notifications/Read': {
        const ids = b.ids as string[] | null;
        this.notes = this.notes.map((n) => (ids === null || ids === undefined || ids.includes(n.id) ? { ...n, read: true } : n));
        return this.json(res, 200, {});
      }
      default:
        return this.json(res, 404, {});
    }
  }
}

/** Minimal stand-in for jellyfin-web: native chrome, hash routing, viewshow events, ApiClient. */
const PAGE = `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Jellyfin (mock)</title><link rel="stylesheet" href="/fullui.css"></head>
<body style="margin:0;background:#101010;color:#ddd;font-family:sans-serif">
<div class="skinHeader" id="nativeHeader" style="padding:10px;background:#202020">Native header <a href="#/home">home</a> <a href="#/dashboard">dashboard</a></div>
<div class="mainDrawer" id="nativeDrawer">Native drawer</div>
<div class="mainAnimatedPages" style="padding:20px">
  <div id="nativeHome" class="page">Native home page</div>
  <div id="nativeDetails" class="page" hidden>Native details page</div>
  <div id="nativeDash" class="page" hidden>Native dashboard</div>
</div>
<script>
window.__logout = 0;
window.ApiClient = {
  getUrl: function (p) { return location.origin + '/' + p; },
  accessToken: function () { return 'tok'; },
  getCurrentUserId: function () { return 'u1'; },
  getCurrentUser: function () { return Promise.resolve({ Name: 'Sam', Policy: { IsAdministrator: true } }); },
  logout: function () { window.__logout++; return Promise.resolve(); }
};
function route() {
  var h = location.hash;
  document.getElementById('nativeHome').hidden = !/^#\\/(home|$)/.test(h) && h !== '';
  document.getElementById('nativeDetails').hidden = h.indexOf('#/details') !== 0;
  document.getElementById('nativeDash').hidden = h.indexOf('#/dashboard') !== 0;
  document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true }));
}
window.addEventListener('hashchange', route);
if (!location.hash) location.hash = '#/home';
</script>
<script src="/fullui.js"></script>
<script>route();</script>
</body></html>`;

/** Stand-in for https://www.youtube.com/iframe_api (the sandbox cannot reach YouTube). */
export const YT_STUB = `
window.__yt = Object.assign({ created: 0, alive: 0, muted: [], failNext: false }, window.__yt || {});
window.YT = { Player: function (el, opts) {
  var d = document.createElement('div');
  d.className = 'yt-stub';
  d.setAttribute('data-video', opts.videoId);
  d.style.cssText = 'width:100%;height:100%';
  el.replaceWith(d);
  window.__yt.created++; window.__yt.alive++;
  var self = this;
  this.mute = function () { window.__yt.muted.push(true); };
  this.unMute = function () { window.__yt.muted.push(false); };
  this.playVideo = function () {};
  this.destroy = function () { d.remove(); window.__yt.alive--; };
  setTimeout(function () {
    if (window.__yt.failNext) { window.__yt.failNext = false; opts.events.onError({ data: 150 }); return; }
    opts.events.onReady({ target: self });
    opts.events.onStateChange({ data: 1 });
  }, 30);
} };
setTimeout(function () { window.onYouTubeIframeAPIReady && window.onYouTubeIframeAPIReady(); }, 0);
`;
