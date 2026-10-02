import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from './api';
import { attributionSentence, createAttribution, setAttribution } from './attribution';
import { createCard } from './card';
import { refreshLocale } from './i18n';
import { searchPage, searchPlaceholder, localTitles, topGenres } from './pages';
import {
  activeSegment,
  ASSIST_OFF_KEY,
  countdownFrom,
  createPlayerAssist,
  inNextZone,
  isVideoRoute,
  itemIdFromUrl,
  parseSegments,
  skipLabel,
} from './playerAssist';
import { addRecent, loadRecent, removeRecent, saveRecent } from './recent';
import { createHero, mountRows } from './rows';
import { resetStore } from './store';
import type { HomeRow, ItemCard } from './types';
import { chooseOrigin, formatTimeLeft, pickImageWidth } from './util';

function stubClient(uid = 'u1', token: string | null = 'tok') {
  window.ApiClient = {
    serverAddress: () => 'http://srv',
    getUrl: (p: string) => {
      if (!p) throw new Error('Url name cannot be empty');
      return 'http://srv/' + p.replace(/^\//, '');
    },
    accessToken: () => token as string,
    getCurrentUserId: () => uid,
  };
}

const mk = (o: Partial<ItemCard> = {}): ItemCard => ({
  id: 'i1',
  name: 'Name',
  type: 'Movie',
  genres: ['Drama'],
  badges: [],
  hasBackdrop: true,
  hasLogo: false,
  myRating: 0,
  inMyList: false,
  rated: 'PG-13',
  year: 2021,
  runtimeMinutes: 112,
  ...o,
});

beforeEach(() => {
  localStorage.clear();
  sessionStorage.clear();
  document.documentElement.lang = 'en';
  refreshLocale();
  resetStore();
  stubClient();
  document.body.replaceChildren();
  location.hash = '';
});
afterEach(() => vi.useRealTimers());

describe('image sizing', () => {
  it('picks the smallest bucket that covers css width x dpr', () => {
    expect(pickImageWidth(256, 1)).toBe(320);
    expect(pickImageWidth(256, 2)).toBe(720);
    expect(pickImageWidth(100, 1)).toBe(240);
    expect(pickImageWidth(1280, 1)).toBe(1280);
    expect(pickImageWidth(1920, 2)).toBe(1920); // capped at the biggest bucket
    expect(pickImageWidth(0, 1)).toBe(320); // unknown width: a safe default
  });
});

describe('edge-aware expansion', () => {
  const scale = 1.35;
  it('grows from the centre when there is room', () => {
    expect(chooseOrigin(500, 256, 256, scale, 0, 1280)).toBe('center');
  });
  it('first card grows to the right, last card grows to the left', () => {
    expect(chooseOrigin(10, 256, 256, scale, 0, 1280)).toBe('left');
    expect(chooseOrigin(1280 - 266, 256, 256, scale, 0, 1280)).toBe('right');
  });
  it('accounts for a wide info panel hanging out of a Top 10 card', () => {
    expect(chooseOrigin(900, 232, 368, scale, 0, 1280)).not.toBe('left');
    // 800px wide page, third card: only anchoring the panel to the right edge (flip) keeps it on screen
    expect(chooseOrigin(512, 232, 368, scale, 0, 800, 56)).toBe('right');
    expect(chooseOrigin(512, 232, 368, scale, 0, 800)).not.toBe('left');
  });
});

describe('card details', () => {
  it('meta text is unchanged but the rating is boxed', () => {
    const el = createCard(mk());
    el.dispatchEvent(new Event('mouseenter'));
    vi.useFakeTimers();
    el.dispatchEvent(new Event('mouseenter'));
    vi.advanceTimersByTime(500);
    const meta = el.querySelector('.fui-meta')!;
    expect(meta.textContent).toBe('2021PG-131h 52m');
    expect(meta.querySelector('.fui-rated')!.textContent).toBe('PG-13');
  });

  it('shows match % and reason when present, nothing when absent', () => {
    vi.useFakeTimers();
    const withMatch = createCard(mk({ matchPercent: 97, reason: 'Because you watched Dark' }));
    withMatch.dispatchEvent(new Event('mouseenter'));
    vi.advanceTimersByTime(500);
    expect(withMatch.querySelector('.fui-match')!.textContent).toBe('97% Match');
    expect(withMatch.querySelector('.fui-reason')!.textContent).toBe('Because you watched Dark');
    const without = createCard(mk({ id: 'i2' }));
    without.dispatchEvent(new Event('mouseenter'));
    vi.advanceTimersByTime(500);
    expect(without.querySelector('.fui-info')).not.toBeNull();
    expect(without.querySelector('.fui-why')).toBeNull();
    const weird = createCard(mk({ id: 'i3', matchPercent: 0 as unknown as number, reason: '   ' }));
    weird.dispatchEvent(new Event('mouseenter'));
    vi.advanceTimersByTime(500);
    expect(weird.querySelector('.fui-why')).toBeNull();
  });

  it('Continue Watching cards show the episode label and time left only when present', () => {
    const c = createCard(mk({ progress: 0.4, seriesLabel: 'S2:E5', minutesLeft: 65 }));
    expect(c.querySelector('.fui-cwlabel')!.textContent).toBe('S2:E5 · 1h 5m left');
    expect(createCard(mk({ id: 'x', progress: 0.4 })).querySelector('.fui-cwlabel')).toBeNull();
    expect(createCard(mk({ id: 'y', minutesLeft: 10 })).querySelector('.fui-cwlabel')).toBeNull(); // no progress bar: no label
    expect(formatTimeLeft(42)).toBe('42m left');
    expect(formatTimeLeft(0)).toBe('');
  });

  it('Top 10 cards get a glyph on the poster and a "#N today" caption in the panel', () => {
    vi.useFakeTimers();
    const c = createCard(mk({ rank: 3 }), 'top10');
    expect(c.querySelector('.fui-top10-glyph')!.textContent).toBe('TOP 10');
    c.dispatchEvent(new Event('mouseenter'));
    vi.advanceTimersByTime(500);
    expect(c.querySelector('.fui-rankcap')!.textContent).toBe('#3 today');
  });

  it('image URLs use a width bucket and the image fades in once loaded', () => {
    const c = createCard(mk());
    const img = c.querySelector('img')!;
    expect(img.getAttribute('src')).toMatch(/maxWidth=(240|320|480|720|960|1280|1920)&/);
    expect(img.classList.contains('loaded')).toBe(false);
    img.dispatchEvent(new Event('load'));
    expect(img.classList.contains('loaded')).toBe(true);
  });
});

describe('hero', () => {
  it('has high fetch priority, a preconnect hint and a maturity box', () => {
    const hero = createHero(mk({ trailerKey: null, matchPercent: 91 }));
    expect(hero.querySelector('.fui-hero-img')!.getAttribute('fetchpriority')).toBe('high');
    expect(document.head.querySelector('link[rel="preconnect"]')).not.toBeNull();
    expect(hero.querySelector('.fui-rated')!.textContent).toBe('PG-13');
    expect(hero.querySelector('.fui-match')!.textContent).toBe('91% Match');
  });
});

describe('lazy rows', () => {
  it('builds the first rows at once and the rest when they near the viewport', () => {
    const cbs: Array<(e: Array<Partial<IntersectionObserverEntry>>) => void> = [];
    const observed: Element[] = [];
    class FakeIO {
      constructor(cb: (e: Array<Partial<IntersectionObserverEntry>>) => void) {
        cbs.push(cb);
      }
      observe(el: Element) {
        observed.push(el);
      }
      unobserve() {}
      disconnect() {}
    }
    vi.stubGlobal('IntersectionObserver', FakeIO);
    try {
      const rows: HomeRow[] = Array.from({ length: 40 }, (_, i) => ({
        id: 'r' + i,
        title: 'Row ' + i,
        type: 'genre',
        items: [mk({ id: 'a' + i }), mk({ id: 'b' + i })],
      }));
      const host = document.createElement('div');
      document.body.appendChild(host);
      const h = mountRows(host, rows);
      expect(host.querySelectorAll('.fui-row').length).toBe(40);
      expect(host.querySelectorAll('.fui-row-sk').length).toBe(36); // only the first 4 are built
      expect(host.querySelectorAll('.fui-card').length).toBe(8);
      expect(host.querySelectorAll('.fui-row-title').length).toBe(40); // titles are always there
      cbs[0]([{ target: observed[0], isIntersecting: true }]);
      expect(host.querySelectorAll('.fui-row-sk').length).toBe(35);
      expect(host.querySelectorAll('.fui-card').length).toBe(10);
      h.dispose();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('shows "Explore all" only for rows with enough titles', () => {
    const host = document.createElement('div');
    mountRows(host, [
      { id: 'big', title: 'Big', type: 'genre', items: [1, 2, 3, 4, 5].map((n) => mk({ id: 'g' + n })) },
      { id: 'small', title: 'Small', type: 'genre', items: [mk({ id: 'z1' })] },
    ]);
    expect(host.querySelector('[data-row-id="big"] .fui-row-explore')!.getAttribute('href')).toBe('#/home?fui=row&q=big');
    expect(host.querySelector('[data-row-id="small"] .fui-row-explore')).toBeNull();
  });
});

describe('TMDB attribution', () => {
  afterEach(() => setAttribution(null));
  it('always shows the sentence; adds the inline logo only when the server asks', () => {
    const plain = createAttribution();
    expect(plain.textContent).toContain('This product uses the TMDB API but is not endorsed or certified by TMDB.');
    expect(plain.querySelector('svg')).toBeNull();
    setAttribution(true);
    const withLogo = createAttribution();
    expect(withLogo.querySelector('svg.fui-tmdb-logo')).not.toBeNull();
    expect(withLogo.querySelector('img')).toBeNull(); // never hotlinked
    expect(attributionSentence({ text: 'Custom text.' })).toBe('Custom text.');
    expect(attributionSentence('  Other. ')).toBe('Other.');
  });
});

describe('recent searches', () => {
  it('keeps at most 8, newest first, no duplicates, drops typing prefixes', () => {
    let l: string[] = [];
    for (const q of ['aa', 'bb', 'cc', 'dd', 'ee', 'ff', 'gg', 'hh', 'ii']) l = addRecent(l, q);
    expect(l).toHaveLength(8);
    expect(l[0]).toBe('ii');
    expect(addRecent(['Dark', 'bre'], 'breaking')).toEqual(['breaking', 'Dark']);
    expect(addRecent(['Dark', 'Bad'], 'dark')).toEqual(['dark', 'Bad']);
    expect(addRecent([], 'a')).toEqual([]);
    expect(removeRecent(['Dark', 'Bad'], 'dark')).toEqual(['Bad']);
  });
  it('is stored per user and never read for another user', () => {
    saveRecent(['secret'], 'u1');
    expect(loadRecent('u1')).toEqual(['secret']);
    expect(loadRecent('u2')).toEqual([]);
    expect(loadRecent('')).toEqual([]);
    saveRecent(['x'], ''); // no user id: nothing is stored
    expect(Object.keys(localStorage).filter((k) => k.startsWith('fullui-recent'))).toEqual(['fullui-recent:u1']);
    localStorage.setItem('fullui-recent:u3', '{not json');
    expect(loadRecent('u3')).toEqual([]);
  });
});

describe('search page (client-only upgrades)', () => {
  const rows: HomeRow[] = [
    { id: 'tp', title: 'Top', type: 'toppicks', items: [mk({ id: 'a', genres: ['Drama', 'Sci-Fi'] }), mk({ id: 'b', genres: ['Drama'] })] },
    { id: 'c', title: 'Continue', type: 'continue', items: [mk({ id: 'c1', genres: ['Western'] })] },
  ];
  const flush = () => new Promise((r) => setTimeout(r, 0));

  it('derives Try: genres and suggestions from cached rows, skipping Continue Watching', () => {
    expect(topGenres(rows)).toEqual(['Drama', 'Sci-Fi']);
    expect(localTitles(rows).map((i) => i.id)).toEqual(['a', 'b']);
    expect(localTitles(rows, 'Sci-Fi').map((i) => i.id)).toEqual(['a']);
  });

  it('placeholder never promises people/genres unless the server proved it', () => {
    expect(searchPlaceholder(false)).not.toMatch(/actor|person|genre|describe/i);
    expect(searchPlaceholder(true)).toMatch(/describe/);
    expect(searchPlaceholder(false, true)).toMatch(/person/);
  });

  it('empty state shows recents (this user only) and Try: chips', async () => {
    saveRecent(['dune'], 'u1');
    saveRecent(['other-users-search'], 'u2');
    const p = searchPage('', () => {}, false, { rows });
    await flush();
    expect(p.el.textContent).toContain('dune');
    expect(p.el.textContent).not.toContain('other-users-search');
    expect(p.el.querySelectorAll('.fui-qchip-try').length).toBe(2);
    p.el.querySelector<HTMLButtonElement>('.fui-recent-clear')!.click();
    expect(loadRecent('u1')).toEqual([]);
    expect(p.el.querySelector('.fui-chiprow[aria-label="Recent searches"]')).toBeNull();
    p.dispose();
  });

  it('a Try: chip lists matching cached titles locally (no request)', async () => {
    const spy = vi.spyOn(api, 'search');
    const p = searchPage('', () => {}, false, { rows });
    await flush();
    [...p.el.querySelectorAll<HTMLButtonElement>('.fui-qchip-try')].find((b) => b.textContent === 'Sci-Fi')!.click();
    expect(p.el.querySelectorAll('.fui-search-results .fui-card').length).toBe(1);
    expect(spy).not.toHaveBeenCalled();
    spy.mockRestore();
    p.dispose();
  });

  it('no results: friendly text plus a "You might like" row from cached rows', async () => {
    const spy = vi.spyOn(api, 'search').mockResolvedValue({ mode: 'keyword', items: [] });
    const p = searchPage('zzzz', () => {}, false, { rows });
    await flush();
    await flush();
    expect(p.el.querySelector('.fui-search-status')!.textContent).toContain('No results for "zzzz".');
    expect(p.el.querySelector('.fui-search-results .fui-row-title')!.textContent).toBe('You might like');
    expect(loadRecent('u1')).toEqual([]); // empty searches are not remembered
    spy.mockRestore();
    p.dispose();
  });

  it('remembers successful searches and renders server groups when present', async () => {
    const spy = vi.spyOn(api, 'search').mockResolvedValue({
      mode: 'keyword',
      items: [],
      groups: [
        { type: 'people', items: [mk({ id: 'p1' })] },
        { type: 'titles', label: 'Matching titles', items: [mk({ id: 't1' }), mk({ id: 't2' })] },
        { type: 'genres', items: [] },
      ],
    });
    const p = searchPage('nolan', () => {}, false, { rows });
    await flush();
    await flush();
    expect([...p.el.querySelectorAll('.fui-group-title')].map((e) => e.textContent)).toEqual(['People', 'Matching titles']);
    expect(p.el.querySelectorAll('.fui-search-results .fui-card').length).toBe(3);
    expect(loadRecent('u1')).toEqual(['nolan']);
    expect(p.el.querySelector<HTMLInputElement>('.fui-search-input')!.placeholder).toMatch(/person/);
    spy.mockRestore();
    p.dispose();
  });

  it('ignores a malformed groups payload', async () => {
    const spy = vi.spyOn(api, 'search').mockResolvedValue({ mode: 'keyword', items: [mk({ id: 'k1' })], groups: [null, { type: 'people' }, 5] as never });
    const p = searchPage('abc', () => {}, false, { rows });
    await flush();
    await flush();
    expect(p.el.querySelectorAll('.fui-search-results .fui-card').length).toBe(1);
    expect(p.el.querySelectorAll('.fui-group-title').length).toBe(0);
    spy.mockRestore();
    p.dispose();
  });
});

describe('player assist: pure rules', () => {
  const ticks = (s: number) => s * 10_000_000;
  it('parses segments strictly', () => {
    const segs = parseSegments({
      Items: [
        { Type: 'Intro', StartTicks: ticks(10), EndTicks: ticks(70) },
        { Type: 'Outro', StartTicks: ticks(1300), EndTicks: ticks(1400) },
        { Type: 'Intro', StartTicks: 'x', EndTicks: 5 },
        { Type: 'Intro', StartTicks: ticks(50), EndTicks: ticks(40) }, // end before start
        { Type: 'Bogus', StartTicks: ticks(1), EndTicks: ticks(2) },
        null,
        7,
      ],
    });
    expect(segs.map((s) => [s.type, s.start, s.end])).toEqual([
      ['Unknown', 1, 2],
      ['Intro', 10, 70],
      ['Outro', 1300, 1400],
    ]);
    expect(parseSegments(null)).toEqual([]);
    expect(parseSegments('nope')).toEqual([]);
    expect(parseSegments({ Items: 'x' })).toEqual([]);
    expect(parseSegments([{ Type: 'Recap', StartTicks: 0, EndTicks: ticks(30) }])).toHaveLength(1);
  });
  it('active segment only inside skippable ranges with time left to skip', () => {
    const segs = parseSegments({ Items: [{ Type: 'Intro', StartTicks: ticks(10), EndTicks: ticks(70) }, { Type: 'Commercial', StartTicks: ticks(100), EndTicks: ticks(200) }] });
    expect(activeSegment(segs, 5)).toBeNull();
    expect(activeSegment(segs, 10)!.type).toBe('Intro');
    expect(activeSegment(segs, 69.5)).toBeNull(); // under a second left
    expect(activeSegment(segs, 150)).toBeNull(); // commercials are not skippable
    expect(activeSegment(segs, NaN)).toBeNull();
    expect(skipLabel('Intro')).toBe('Skip Intro');
    expect(skipLabel('Recap')).toBe('Skip Recap');
    expect(skipLabel('Outro')).toBe('Skip Credits');
  });
  it('route and id helpers', () => {
    expect(isVideoRoute('#/video')).toBe(true);
    expect(isVideoRoute('#!/video?x=1')).toBe(true);
    expect(isVideoRoute('#/videos')).toBe(false);
    expect(isVideoRoute('#/home')).toBe(false);
    expect(isVideoRoute('')).toBe(false);
    const g = '0123456789abcdef0123456789abcdef';
    expect(itemIdFromUrl(`http://s/Videos/${g}/stream.mkv?a=1`)).toBe(g);
    expect(itemIdFromUrl(`http://s/videos/${g}/master.m3u8`)).toBe(g);
    expect(itemIdFromUrl('blob:http://s/abc')).toBeNull();
    expect(itemIdFromUrl(null)).toBeNull();
  });
  it('next-episode zone and countdown length', () => {
    expect(inNextZone(1300, 1320, [])).toBe(true); // last 20 s
    expect(inNextZone(1000, 1320, [])).toBe(false);
    expect(inNextZone(1000, 40, [])).toBe(false); // too short to be an episode
    const outro = parseSegments({ Items: [{ Type: 'Outro', StartTicks: ticks(1250), EndTicks: ticks(1320) }] });
    expect(inNextZone(1260, 1320, outro)).toBe(true);
    expect(inNextZone(1260, 1500, outro)).toBe(false); // outro that does not run to the end
    expect(countdownFrom(60)).toBe(10);
    expect(countdownFrom(8)).toBe(6);
    expect(countdownFrom(4)).toBe(3);
  });
});

describe('player assist: it can never break playback', () => {
  function mockVideo(): HTMLVideoElement {
    const v = document.createElement('video');
    v.className = 'htmlvideoplayer';
    document.body.appendChild(v);
    return v;
  }
  const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

  it('does nothing off the video route, when disabled, or without a <video>', async () => {
    mockVideo();
    const a = createPlayerAssist({ isEnabled: () => true });
    location.hash = '#/home';
    a.sync();
    expect(document.getElementById('fullui-assist')).toBeNull();
    location.hash = '#/video';
    const off = createPlayerAssist({ isEnabled: () => false });
    off.sync();
    expect(document.getElementById('fullui-assist')).toBeNull();
    a.stop();
    off.stop();
    document.body.replaceChildren();
    const b = createPlayerAssist({ isEnabled: () => true });
    expect(() => b.sync()).not.toThrow(); // no video yet: it just keeps looking
    b.stop();
  });

  it('removes its overlay on route change and leaves the native video untouched', () => {
    const v = mockVideo();
    v.setAttribute('data-native', '1');
    const before = document.body.innerHTML;
    location.hash = '#/video';
    const a = createPlayerAssist({ isEnabled: () => true });
    a.sync();
    expect(document.getElementById('fullui-assist')).not.toBeNull();
    expect(document.getElementById('fullui-assist')!.style.pointerEvents).toBe(''); // set by css: none
    location.hash = '#/home';
    a.sync();
    expect(document.getElementById('fullui-assist')).toBeNull();
    expect(document.body.innerHTML).toBe(before); // byte-identical native DOM
    expect(v.paused).toBe(true);
    a.stop();
  });

  it('switches itself off after 3 caught errors: one warning, sessionStorage flag, nothing left behind', () => {
    vi.useFakeTimers();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const v = mockVideo();
    Object.defineProperty(v, 'currentTime', {
      get() {
        throw new Error('boom');
      },
    });
    location.hash = '#/video';
    const a = createPlayerAssist({ isEnabled: () => true });
    expect(() => a.sync()).not.toThrow(); // the first throw happens inside the initial tick
    for (let i = 0; i < 5; i++) {
      vi.advanceTimersByTime(300);
      expect(() => v.dispatchEvent(new Event('timeupdate'))).not.toThrow();
    }
    expect(warn).toHaveBeenCalledTimes(1);
    expect(sessionStorage.getItem(ASSIST_OFF_KEY)).toBe('1');
    expect(document.getElementById('fullui-assist')).toBeNull();
    // and it stays off for the session, even for a brand-new instance
    const again = createPlayerAssist({ isEnabled: () => true });
    again.sync();
    expect(document.getElementById('fullui-assist')).toBeNull();
    warn.mockRestore();
  });

  it('a skip click is the only write: currentTime = segment end', async () => {
    const v = mockVideo();
    Object.defineProperty(v, 'duration', { value: 1400, configurable: true });
    let time = 20;
    let writes = 0;
    Object.defineProperty(v, 'currentTime', {
      get: () => time,
      set: (x: number) => {
        writes++;
        time = x;
      },
    });
    const g = '0123456789abcdef0123456789abcdef';
    Object.defineProperty(v, 'currentSrc', { value: `http://srv/Videos/${g}/stream.mp4`, configurable: true });
    const fetchMock = vi.fn(async (url: string) => {
      expect(url).toBe(`http://srv/MediaSegments/${g}`);
      return new Response(JSON.stringify({ Items: [{ Type: 'Intro', StartTicks: 100_000_000, EndTicks: 700_000_000 }] }), { status: 200 });
    });
    vi.stubGlobal('fetch', fetchMock);
    try {
      location.hash = '#/video';
      const a = createPlayerAssist({ isEnabled: () => true });
      a.sync();
      await wait(20); // segments arrive
      await wait(260);
      v.dispatchEvent(new Event('timeupdate'));
      const btn = document.querySelector<HTMLButtonElement>('.fui-assist-skip')!;
      expect(btn.classList.contains('fui-assist-hidden')).toBe(false);
      expect(btn.textContent).toBe('Skip Intro');
      expect(writes).toBe(0); // showing the button changed nothing
      btn.click();
      expect(time).toBe(70);
      expect(writes).toBe(1);
      a.stop();
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
