import { describe, expect, it, vi } from 'vitest';
import type { HomeRow, ItemCard } from './types';
import {
  badgeText,
  debounce,
  detailsHash,
  filterRowsByType,
  formatRuntime,
  imageUrl,
  metaParts,
  nextRating,
  nextVote,
  parseRoute,
  pickHeroItem,
  pickNeighbor,
  routeHash,
  timeAgo,
  tmdbImage,
  unreadCount,
  voteBody,
} from './util';

const card = (o: Partial<ItemCard> = {}): ItemCard => ({
  id: 'a',
  name: 'A',
  type: 'Movie',
  genres: [],
  badges: [],
  hasBackdrop: true,
  hasLogo: false,
  myRating: 0,
  inMyList: false,
  ...o,
});

describe('url builders', () => {
  it('builds image urls with a base path', () => {
    expect(imageUrl('http://x/jellyfin/', 'abc', 'Backdrop', 480)).toBe(
      'http://x/jellyfin/Items/abc/Images/Backdrop?maxWidth=480&quality=90',
    );
    expect(imageUrl('', 'abc', 'Logo', 300)).toBe('/Items/abc/Images/Logo?maxWidth=300&quality=90');
  });
  it('builds tmdb urls', () => {
    expect(tmdbImage('/p.jpg')).toBe('https://image.tmdb.org/t/p/w342/p.jpg');
    expect(tmdbImage('p.jpg', 'w500')).toBe('https://image.tmdb.org/t/p/w500/p.jpg');
    expect(tmdbImage(null)).toBeNull();
  });
  it('encodes ids in details links', () => {
    expect(detailsHash('a b')).toBe('#/details?id=a%20b');
  });
});

describe('hash routing', () => {
  it('recognises home variants', () => {
    for (const h of ['#/home', '#/home.html', '#!/home', '#/home/', '#/HOME.html']) expect(parseRoute(h).kind).toBe('home');
  });
  it('recognises our pages inside the home route', () => {
    expect(parseRoute('#/home?fui=shows').kind).toBe('shows');
    expect(parseRoute('#/home?fui=movies').kind).toBe('movies');
    expect(parseRoute('#/home?fui=myserver').kind).toBe('myserver');
    expect(parseRoute('#/home?fui=search&q=dune%20x')).toEqual({ kind: 'search', q: 'dune x' });
  });
  it('leaves everything else to jellyfin-web', () => {
    for (const h of ['', '#/details?id=1', '#/dashboard', '#/video', '#/home?tab=1', '#/homefoo']) {
      expect(parseRoute(h).kind).toBe('native');
    }
  });
  it('round-trips', () => {
    expect(routeHash('home')).toBe('#/home');
    expect(parseRoute(routeHash('search', 'a&b')).q).toBe('a&b');
    expect(parseRoute(routeHash('shows')).kind).toBe('shows');
  });
});

describe('rating state machine', () => {
  it('sets a level', () => {
    expect(nextRating(0, 1)).toBe(1);
    expect(nextRating(0, 2)).toBe(2);
    expect(nextRating(0, -1)).toBe(-1);
  });
  it('clicking the active level clears it', () => {
    for (const l of [-1, 1, 2]) expect(nextRating(l, l)).toBe(0);
  });
  it('switches levels with only one active', () => {
    expect(nextRating(1, 2)).toBe(2);
    expect(nextRating(2, -1)).toBe(-1);
  });
  it('ignores invalid input', () => {
    expect(nextRating(1, 7)).toBe(0);
  });
  it('vote toggles and clears with 0', () => {
    expect(nextVote(0, 1)).toBe(1);
    expect(nextVote(1, 1)).toBe(0);
    expect(nextVote(1, -1)).toBe(-1);
    expect(nextVote(-1, -1)).toBe(0);
  });
});

describe('debounce', () => {
  it('collapses bursts and can be cancelled', () => {
    vi.useFakeTimers();
    const fn = vi.fn();
    const d = debounce(fn, 100);
    d(1);
    d(2);
    d(3);
    vi.advanceTimersByTime(99);
    expect(fn).not.toHaveBeenCalled();
    vi.advanceTimersByTime(2);
    expect(fn).toHaveBeenCalledTimes(1);
    expect(fn).toHaveBeenCalledWith(3);
    d(4);
    d.cancel();
    vi.advanceTimersByTime(500);
    expect(fn).toHaveBeenCalledTimes(1);
    vi.useRealTimers();
  });
});

describe('formatting', () => {
  it('formats runtime', () => {
    expect(formatRuntime(45)).toBe('45m');
    expect(formatRuntime(120)).toBe('2h');
    expect(formatRuntime(112)).toBe('1h 52m');
    expect(formatRuntime(null)).toBe('');
  });
  it('builds the meta line', () => {
    expect(metaParts({ year: 2021, rated: 'TV-MA', runtimeMinutes: 112 })).toEqual(['2021', 'TV-MA', '1h 52m']);
    expect(metaParts({ year: null, rated: null, runtimeMinutes: null })).toEqual([]);
  });
  it('notification helpers', () => {
    expect(
      unreadCount([
        { id: '1', text: '', at: '', read: false },
        { id: '2', text: '', at: '', read: true },
      ]),
    ).toBe(1);
    expect(badgeText(3)).toBe('3');
    expect(badgeText(25)).toBe('9+');
    expect(timeAgo('2020-01-01T00:00:00Z', Date.parse('2020-01-01T00:05:00Z'))).toBe('5m ago');
    expect(timeAgo('nope')).toBe('');
  });
});

describe('row helpers', () => {
  const rows: HomeRow[] = [
    { id: 'c', title: 'Continue', type: 'continue', items: [card({ id: 'c1', type: 'Series' })] },
    {
      id: 't',
      title: 'Top',
      type: 'toppicks',
      items: [card({ id: 'm1', hasBackdrop: false }), card({ id: 's1', type: 'Series', trailerKey: 'abcdefghijk' })],
    },
    {
      id: 'cs',
      title: 'Soon',
      type: 'comingsoon',
      items: [],
      comingSoon: [
        { tmdbId: 1, mediaType: 'movie', title: 'M', myVote: 0 },
        { tmdbId: 2, mediaType: 'tv', title: 'T', myVote: 0 },
      ],
    },
  ];
  it('filters by type incl. coming soon media types', () => {
    const shows = filterRowsByType(rows, 'Series');
    expect(shows.map((r) => r.id)).toEqual(['c', 't', 'cs']);
    expect(shows[1].items.map((i) => i.id)).toEqual(['s1']);
    expect(shows[2].comingSoon!.map((c) => c.tmdbId)).toEqual([2]);
    const movies = filterRowsByType(rows, 'Movie');
    expect(movies.map((r) => r.id)).toEqual(['t', 'cs']);
  });
  it('drops rows that end up empty', () => {
    expect(filterRowsByType([rows[0]], 'Movie')).toEqual([]);
  });
  it('picks a hero, preferring trailers and skipping personal rows', () => {
    expect(pickHeroItem(rows)!.id).toBe('s1');
    expect(pickHeroItem([rows[0]])!.id).toBe('c1');
    expect(pickHeroItem([])).toBeNull();
  });
  it('builds the vote body', () => {
    expect(voteBody({ tmdbId: 5, mediaType: 'tv', title: 'T', myVote: 0 }, 1)).toEqual({
      tmdbId: 5,
      mediaType: 'tv',
      vote: 1,
      title: 'T',
      posterPath: null,
      backdropPath: null,
      releaseDate: null,
      overview: null,
    });
  });
});

describe('spatial geometry', () => {
  const r = (l: number, t: number, w = 100, h = 60) => ({ left: l, top: t, right: l + w, bottom: t + h });
  it('moves within a row and across rows', () => {
    const from = r(0, 0);
    const cands = [r(110, 0), r(220, 0), r(0, 100), r(110, 100)];
    expect(pickNeighbor(from, cands, 'right')).toBe(0);
    expect(pickNeighbor(from, cands, 'down')).toBe(2);
    expect(pickNeighbor(from, cands, 'left')).toBe(-1);
    expect(pickNeighbor(from, cands, 'up')).toBe(-1);
    expect(pickNeighbor(r(110, 100), cands, 'up')).toBe(0);
  });
});
