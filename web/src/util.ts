import { formatDate, formatRelative, t } from './i18n';
import type { ComingSoonCard, HomeRow, ItemCard, NotificationDto, Route, RouteKind } from './types';

export type ImageKind = 'Primary' | 'Backdrop' | 'Logo';

/** `base` is the server root (ApiClient.serverAddress() style); may be empty or end with a slash. */
export function joinUrl(base: string, path: string): string {
  return base.replace(/\/+$/, '') + '/' + path.replace(/^\/+/, '');
}

/** `tag` (the item's image tag, when the API provides one) lets the browser cache the image for a long time. */
export function imageUrl(base: string, id: string, kind: ImageKind, maxWidth: number, tag?: string | null): string {
  const t = tag ? `&tag=${encodeURIComponent(tag)}` : '';
  return joinUrl(base, `Items/${encodeURIComponent(id)}/Images/${kind}?maxWidth=${maxWidth}&quality=90${t}`);
}

export const DEFAULT_ACCENT = '#e50914';
/** Accept only #rgb / #rgba / #rrggbb / #rrggbbaa style colours; anything else (a typo like "e50914") falls back. */
export function safeAccent(v: string | null | undefined): string {
  return v && /^#[0-9a-f]{3,8}$/i.test(v.trim()) && [4, 5, 7, 9].includes(v.trim().length) ? v.trim() : DEFAULT_ACCENT;
}

/** Stable key for "who is signed in": cached data is only valid for the same user AND token. */
export function sessionKeyOf(userId: string | null | undefined, token: string | null | undefined): string {
  return token ? `${userId || ''}|${token}` : '';
}

export function tmdbImage(path: string | null | undefined, size = 'w342'): string | null {
  if (!path) return null;
  return `https://image.tmdb.org/t/p/${size}${path.startsWith('/') ? path : '/' + path}`;
}

export function detailsHash(id: string): string {
  return `#/details?id=${encodeURIComponent(id)}`;
}

export function debounce<A extends unknown[]>(
  fn: (...a: A) => void,
  ms: number,
): ((...a: A) => void) & { cancel(): void } {
  let t: ReturnType<typeof setTimeout> | undefined;
  const d = (...a: A) => {
    if (t !== undefined) clearTimeout(t);
    t = setTimeout(() => {
      t = undefined;
      fn(...a);
    }, ms);
  };
  d.cancel = () => {
    if (t !== undefined) clearTimeout(t);
    t = undefined;
  };
  return d;
}

// ---- hash routing -------------------------------------------------------------------------
// Our pages live inside Jellyfin's own home route (`#/home?fui=shows`) so jellyfin-web's router
// always sees a route it knows.

export function parseRoute(hash: string): Route {
  const h = hash || '';
  const qi = h.indexOf('?');
  const path = (qi >= 0 ? h.slice(0, qi) : h).replace(/^#!?/, '').replace(/\/+$/, '');
  const params = new URLSearchParams(qi >= 0 ? h.slice(qi + 1) : '');
  if (!/^\/?(home|home\.html)$/i.test(path)) return { kind: 'native', q: '' };
  // jellyfin-web's own home route takes ?tab=N for its home tabs; tab > 0 is not ours.
  const tab = params.get('tab');
  if (tab && tab !== '0') return { kind: 'native', q: '' };
  const fui = (params.get('fui') || '').toLowerCase();
  const kinds: string[] = ['shows', 'movies', 'myserver', 'search', 'row'];
  const kind = (kinds.includes(fui) ? fui : 'home') as RouteKind;
  return { kind, q: params.get('q') || '' };
}

export function routeHash(kind: RouteKind, q = ''): string {
  if (kind === 'home' || kind === 'native') return '#/home';
  const p = new URLSearchParams({ fui: kind });
  if (q) p.set('q', q);
  return `#/home?${p.toString()}`;
}

// ---- rating / vote state machines ---------------------------------------------------------
export type Rating = -1 | 0 | 1 | 2;

/** Clicking the active level clears it (0); otherwise sets it. One level active at a time. */
export function nextRating(current: number, clicked: number): Rating {
  if (clicked !== -1 && clicked !== 1 && clicked !== 2) return 0;
  return (current === clicked ? 0 : clicked) as Rating;
}

export function nextVote(current: number, clicked: number): -1 | 0 | 1 {
  if (clicked !== -1 && clicked !== 1) return 0;
  return (current === clicked ? 0 : clicked) as -1 | 0 | 1;
}

// ---- formatting ---------------------------------------------------------------------------
export function formatRuntime(min: number | null | undefined): string {
  if (!min || min <= 0) return '';
  const h = Math.floor(min / 60);
  const m = Math.round(min % 60);
  if (h === 0) return t('time.m', { m });
  return m === 0 ? t('time.h', { h }) : t('time.hm', { h, m });
}

export function metaParts(c: Pick<ItemCard, 'year' | 'rated' | 'runtimeMinutes'>): string[] {
  const out: string[] = [];
  if (c.year) out.push(String(c.year));
  if (c.rated) out.push(c.rated);
  const rt = formatRuntime(c.runtimeMinutes);
  if (rt) out.push(rt);
  return out;
}

export function formatRelease(date: string | null | undefined): string {
  if (!date) return t('soon.comingSoon');
  const d = new Date(date.length === 10 ? date + 'T00:00:00Z' : date);
  if (isNaN(d.getTime())) return t('soon.comingSoon');
  return formatDate(d, { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });
}

export function unreadCount(items: NotificationDto[]): number {
  return items.filter((n) => !n.read).length;
}

export function badgeText(n: number): string {
  return n > 9 ? '9+' : String(n);
}

export function timeAgo(iso: string, now = Date.now()): string {
  const ts = new Date(iso).getTime();
  if (isNaN(ts)) return '';
  const s = Math.max(0, Math.round((now - ts) / 1000));
  if (s < 60) return t('time.justNow');
  return formatRelative(s);
}

// ---- row helpers --------------------------------------------------------------------------
/** Filter rows to one item type. Coming Soon rows filter by TMDB media type. */
export function filterRowsByType(rows: HomeRow[], type: 'Series' | 'Movie'): HomeRow[] {
  const media = type === 'Series' ? 'tv' : 'movie';
  const out: HomeRow[] = [];
  for (const r of rows) {
    if (r.type === 'comingsoon') {
      const cs = (r.comingSoon || []).filter((c) => c.mediaType === media);
      if (cs.length) out.push({ ...r, items: [], comingSoon: cs });
      continue;
    }
    const items = (r.items || []).filter((i) => i.type === type);
    if (items.length) out.push({ ...r, items });
  }
  return out;
}

const HERO_SKIP = new Set(['continue', 'mylist', 'comingsoon', 'again']);

export function pickHeroItem(rows: HomeRow[]): ItemCard | null {
  const cands: ItemCard[] = [];
  for (const r of rows) if (!HERO_SKIP.has(r.type)) cands.push(...(r.items || []));
  const best = cands.find((i) => i.hasBackdrop && i.trailerKey) || cands.find((i) => i.hasBackdrop);
  if (best) return best;
  for (const r of rows) {
    const f = (r.items || []).find((i) => i.hasBackdrop);
    if (f) return f;
  }
  return null;
}

export function rowHasContent(r: HomeRow): boolean {
  return r.type === 'comingsoon' ? !!r.comingSoon && r.comingSoon.length > 0 : !!r.items && r.items.length > 0;
}

export function voteBody(c: ComingSoonCard, vote: number) {
  return {
    tmdbId: c.tmdbId,
    mediaType: c.mediaType,
    vote,
    title: c.title,
    posterPath: c.posterPath ?? null,
    backdropPath: c.backdropPath ?? null,
    releaseDate: c.releaseDate ?? null,
    overview: c.overview ?? null,
  };
}

// ---- spatial navigation (pure geometry) ---------------------------------------------------
export interface Rect {
  left: number;
  top: number;
  right: number;
  bottom: number;
}
export type Dir = 'left' | 'right' | 'up' | 'down';

/** Index of the best candidate in direction `dir`, or -1. Prefers aligned, near candidates. */
export function pickNeighbor(from: Rect, cands: Rect[], dir: Dir): number {
  const cx = (r: Rect) => (r.left + r.right) / 2;
  const cy = (r: Rect) => (r.top + r.bottom) / 2;
  let best = -1;
  let bestScore = Infinity;
  cands.forEach((c, i) => {
    const dx = cx(c) - cx(from);
    const dy = cy(c) - cy(from);
    let main: number;
    let cross: number;
    if (dir === 'right') {
      if (dx <= 1) return;
      main = c.left - from.right;
      cross = Math.abs(dy);
    } else if (dir === 'left') {
      if (dx >= -1) return;
      main = from.left - c.right;
      cross = Math.abs(dy);
    } else if (dir === 'down') {
      if (dy <= 1) return;
      main = c.top - from.bottom;
      cross = Math.abs(dx);
    } else {
      if (dy >= -1) return;
      main = from.top - c.bottom;
      cross = Math.abs(dx);
    }
    const score = Math.max(0, main) + cross * 2.5 + (main < 0 ? 200 : 0);
    if (score < bestScore) {
      bestScore = score;
      best = i;
    }
  });
  return best;
}

// ---- images ---------------------------------------------------------------------------------
export const WIDTH_BUCKETS = [240, 320, 480, 720, 960, 1280, 1920] as const;

/** Smallest server-side width bucket that still covers `cssPx` device-independent pixels at `dpr`. */
export function pickImageWidth(cssPx: number, dpr = 1): number {
  const want = Math.ceil((cssPx > 0 ? cssPx : 320) * (dpr > 0 ? Math.min(dpr, 3) : 1));
  for (const b of WIDTH_BUCKETS) if (b >= want) return b;
  return WIDTH_BUCKETS[WIDTH_BUCKETS.length - 1];
}

/** "42m left" / "1h 5m left" for resumable titles; '' when unknown. */
export function formatTimeLeft(minutes: number | null | undefined): string {
  if (!minutes || minutes <= 0) return '';
  return t('card.timeLeft', { time: formatRuntime(Math.round(minutes)) });
}

// ---- card expansion geometry ----------------------------------------------------------------
export type Origin = 'left' | 'center' | 'right';

/**
 * Which horizontal transform-origin lets a card scaled by `scale` stay inside [boxLeft, boxRight]?
 * `extent` is the width of the card plus anything hanging out to the right (the info panel of Top 10 cards).
 * With origin 'right' that panel is anchored to the card's right edge instead, so it hangs out to the left
 * by `flip` px. Prefers growing from the centre, then left, then right; if nothing fits, the least overflow wins.
 */
export function chooseOrigin(left: number, width: number, extent: number, scale: number, boxLeft: number, boxRight: number, flip = 0): Origin {
  const ratios: Array<[Origin, number]> = [
    ['center', 0.5],
    ['left', 0],
    ['right', 1],
  ];
  let best: Origin = 'center';
  let bestOver = Infinity;
  for (const [name, r] of ratios) {
    const x0 = left + r * width;
    const l = x0 + (left - (name === 'right' ? flip : 0) - x0) * scale;
    const rt = x0 + (left + (name === 'right' ? width : Math.max(width, extent)) - x0) * scale;
    const over = Math.max(0, boxLeft - l) + Math.max(0, rt - boxRight);
    if (over < bestOver - 0.5) {
      bestOver = over;
      best = name;
    }
    if (over === 0) return name;
  }
  return best;
}
