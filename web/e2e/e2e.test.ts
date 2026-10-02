import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright-core';
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { G, Mock, YT_STUB } from './mock-server';

const here = dirname(fileURLToPath(import.meta.url));
const shots = join(here, '..', 'e2e-artifacts');

function findChromium(): string {
  const root = process.env.PLAYWRIGHT_BROWSERS_PATH || '/opt/pw-browsers';
  const direct = join(root, 'chromium');
  if (existsSync(direct)) return direct;
  const dir = readdirSync(root).find((d) => d.startsWith('chromium-'));
  if (!dir) throw new Error('no chromium under ' + root);
  return join(root, dir, 'chrome-linux', 'chrome');
}

const mock = new Mock();
let browser: Browser;
let ctx: BrowserContext | null = null;
let errors: string[] = [];

beforeAll(async () => {
  mkdirSync(shots, { recursive: true });
  await mock.start();
  browser = await chromium.launch({ executablePath: findChromium(), args: ['--no-sandbox'] });
});
afterAll(async () => {
  await browser?.close();
  await mock.stop();
});
afterEach(async () => {
  await ctx?.close();
  ctx = null;
});

interface Opts {
  mobile?: boolean;
  width?: number;
  height?: number;
  hash?: string;
  allowErrors?: RegExp;
  reducedMotion?: boolean;
  /** Serve the whole site under this path prefix, e.g. '/jellyfin'. */
  base?: string;
  /** Experimental layout (MUI app bar / drawer) in the page chrome. */
  experimental?: boolean;
  /** Start signed out (login page). */
  signedOut?: boolean;
  /** devicePixelRatio of the browser context. */
  dpr?: number;
  /** `<html lang>` the served page declares (jellyfin-web sets it from the user's language). */
  lang?: string;
  /** Mock settings applied before the page loads. */
  setup?: (m: Mock) => void;
}

async function open(o: Opts = {}): Promise<Page> {
  mock.reset();
  mock.base = o.base ?? '';
  mock.experimental = !!o.experimental;
  mock.token = o.signedOut ? null : 'tok';
  mock.lang = o.lang ?? 'en';
  o.setup?.(mock);
  errors = [];
  ctx = await browser.newContext({
    viewport: { width: o.width ?? (o.mobile ? 390 : 1280), height: o.height ?? (o.mobile ? 844 : 800) },
    hasTouch: !!o.mobile,
    isMobile: !!o.mobile,
    reducedMotion: o.reducedMotion ? 'reduce' : 'no-preference',
    deviceScaleFactor: o.dpr ?? 1,
  });
  await ctx.route('https://www.youtube.com/iframe_api', (r) => r.fulfill({ contentType: 'text/javascript', body: YT_STUB }));
  await ctx.route('https://image.tmdb.org/**', (r) =>
    r.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="342" height="513"><rect width="342" height="513" fill="#335"/></svg>' }),
  );
  const page = await ctx.newPage();
  page.on('console', (m) => {
    if (m.type() === 'error') errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
  await page.goto(mock.origin + mock.base + '/' + (o.hash ? '#' + o.hash.replace(/^#/, '') : ''));
  return page;
}

function noConsoleErrors(allow?: RegExp): void {
  const bad = errors.filter((e) => !(allow && allow.test(e)));
  expect(bad).toEqual([]);
}

const root = '#fullui-root';
const card = (page: Page, id: string, row = 'toppicks') => page.locator(`[data-row-id="${row}"] .fui-card[data-id="${id}"]`);

async function homeReady(page: Page): Promise<void> {
  await page.waitForSelector(`${root} .fui-hero`);
  await page.waitForSelector(`${root} .fui-row`);
  // rows below the first few are built lazily when they get near the viewport
  await page.waitForFunction(() => Array.from(document.querySelectorAll('.fui-row-sk')).every((e) => e.getBoundingClientRect().top > innerHeight * 2.5));
}

describe('shell + nav', () => {
  it('renders the top nav, hides native chrome only while owning, no console errors', async () => {
    const page = await open();
    await homeReady(page);
    expect(await page.locator('.fui-wordmark').textContent()).toBe('MowFlix');
    expect(await page.locator('.fui-tab').allTextContents()).toEqual(['Home', 'Shows', 'Movies', 'My MowFlix']);
    expect(await page.locator('.fui-tab.active').textContent()).toBe('Home');
    await page.waitForSelector('.fui-bell-count:not(.fui-hidden)');
    expect(await page.locator('.fui-bell-count').textContent()).toBe('2');
    expect(await page.locator('.fui-avatar').count()).toBe(1);
    expect(await page.locator('.fui-classic').textContent()).toBe('Use classic view');
    expect(await page.locator('#nativeHeader').isVisible()).toBe(false);
    expect(await page.locator('#nativeDrawer').isVisible()).toBe(false);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--fullui-accent').trim())).toBe('#e50914');
    // no sidebar-like drawer inside our UI
    expect(await page.locator(`${root} .mainDrawer, ${root} aside`).count()).toBe(0);
    // every API call carried the Jellyfin auth header
    expect(mock.calls.length).toBeGreaterThan(0);
    expect(mock.calls.every((c) => c.auth === 'MediaBrowser Token="tok"')).toBe(true);
    await page.screenshot({ path: join(shots, 'desktop-home.png') });
    noConsoleErrors();
  });

  it('leaves native pages (details, dashboard) usable and visible', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => (location.hash = '#/dashboard'));
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    expect(await page.locator('#nativeDash').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    await page.evaluate(() => (location.hash = '#/home'));
    await page.waitForSelector(`${root}:not(.fui-hidden) .fui-hero`);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(false);
    noConsoleErrors();
  });

  it('mounts once even when viewshow fires repeatedly', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => {
      for (let i = 0; i < 5; i++) document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true }));
    });
    await page.waitForTimeout(200);
    expect(await page.locator('#fullui-root').count()).toBe(1);
    expect(await page.locator('.fui-hero').count()).toBe(1);
    expect(mock.callsTo('GET', 'Home').length).toBe(1);
  });
});

describe('home', () => {
  it('renders hero, rows, top 10 numerals and the coming soon row', async () => {
    const page = await open();
    await homeReady(page);
    // untrusted title rendered as text, not markup
    expect(await page.locator('.fui-hero h1').textContent()).toBe('Hero <b>Movie</b>');
    expect(await page.locator('.fui-hero b').count()).toBe(0);
    expect(await page.locator('.fui-hero-synopsis').textContent()).toContain('Synopsis for');
    expect(await page.locator('.fui-hero-play').isVisible()).toBe(true);
    expect(await page.locator('.fui-hero-more').isVisible()).toBe(true);
    const titles = await page.locator('.fui-row-title').allTextContents();
    expect(titles).toEqual(['Continue Watching', 'Top Picks for Sam', 'Top 10 Movies Today', 'Trending Now', 'Coming Soon']); // empty row skipped
    expect(await page.locator('.fui-row-top10 .fui-rank').allTextContents()).toEqual(['1', '2', '3']);
    expect(await page.locator('.fui-row-comingsoon .fui-card').count()).toBe(2);
    expect(await page.locator('.fui-progress').count()).toBe(1);
    expect(await page.locator('b').count()).toBe(0);
    noConsoleErrors();
  });

  it('autoplays a muted hero trailer after a short delay and toggles mute', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on', { timeout: 5000 });
    expect(await page.locator('.fui-hero .yt-stub').count()).toBe(1);
    expect(await page.evaluate(() => (window as any).__yt.muted.at(-1))).toBe(true);
    await page.click('.fui-hero-mute');
    expect(await page.evaluate(() => (window as any).__yt.muted.at(-1))).toBe(false);
    expect(await page.locator('.fui-hero-mute').getAttribute('aria-label')).toBe('Mute trailer');
    await page.click('.fui-hero-mute');
    expect(await page.evaluate(() => (window as any).__yt.muted.at(-1))).toBe(true);
    noConsoleErrors();
  });

  it('falls back to the backdrop when the trailer fails', async () => {
    const page = await open();
    await page.evaluate(() => ((window as any).__yt = { failNext: true }));
    await homeReady(page);
    await page.waitForTimeout(1800);
    expect(await page.locator('.fui-hero.trailer-on').count()).toBe(0);
    expect(await page.locator('.fui-hero-img').isVisible()).toBe(true);
    expect(await page.locator('.fui-hero-mute').isVisible()).toBe(false);
    noConsoleErrors();
  });

  it('does not autoplay trailers with prefers-reduced-motion', async () => {
    const page = await open({ reducedMotion: true });
    await homeReady(page);
    await page.waitForTimeout(1500);
    expect(await page.locator('.yt-stub').count()).toBe(0);
    expect(await page.locator('.fui-hero-img').isVisible()).toBe(true);
  });
});

describe('cards', () => {
  it('expands on hover with details, runs one trailer at a time, collapses on leave', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    const c1 = card(page, G('a2'));
    await c1.scrollIntoViewIfNeeded();
    await c1.hover();
    await page.waitForSelector(`[data-row-id="toppicks"] .fui-card[data-id="${G('a2')}"].expanded`);
    const info = c1.locator('.fui-info');
    expect(await info.isVisible()).toBe(true);
    expect(await info.locator('.fui-meta').textContent()).toBe('2021PG-131h 52m');
    expect(await info.locator('.fui-synopsis').isVisible()).toBe(true);
    expect(await info.locator('.fui-play').isVisible()).toBe(true);
    expect(await info.locator('.fui-rate').count()).toBe(3);
    // trailer in the expanded card; hero trailer destroyed => exactly one player alive
    await page.waitForSelector(`[data-id="${G('a2')}"] .yt-stub`, { timeout: 5000 });
    expect(await page.evaluate(() => (window as any).__yt.alive)).toBe(1);
    // hover another card: previous collapses, still exactly one player
    const c2 = card(page, G('s1'));
    await c2.hover();
    await page.waitForSelector(`[data-row-id="toppicks"] .fui-card[data-id="${G('s1')}"].expanded`);
    expect(await page.locator('.fui-card.expanded').count()).toBe(1);
    await page.waitForSelector(`[data-id="${G('s1')}"] .yt-stub`, { timeout: 5000 });
    expect(await page.locator('.yt-stub').count()).toBe(1);
    expect(await page.evaluate(() => (window as any).__yt.alive)).toBe(1);
    await page.screenshot({ path: join(shots, 'desktop-card-expanded.png') });
    // leave: collapses and destroys the player
    await page.mouse.move(5, 5);
    await page.waitForSelector('.fui-card.expanded', { state: 'detached', timeout: 3000 });
    expect(await page.evaluate(() => (window as any).__yt.alive)).toBe(0);
    noConsoleErrors();
  });

  it('thumbs: one POST per click, one active at a time, active click clears', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    const up = c.locator('.fui-rate-up');
    const love = c.locator('.fui-rate-love');
    await up.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(1);
    expect(mock.callsTo('POST', 'Rate')[0].body).toEqual({ itemId: G('a2'), rating: 1 });
    expect(await up.getAttribute('aria-pressed')).toBe('true');
    await love.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(2);
    expect(mock.callsTo('POST', 'Rate')[1].body).toEqual({ itemId: G('a2'), rating: 2 });
    expect(await up.getAttribute('aria-pressed')).toBe('false');
    expect(await love.getAttribute('aria-pressed')).toBe('true');
    await love.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(3);
    expect(mock.callsTo('POST', 'Rate')[2].body).toEqual({ itemId: G('a2'), rating: 0 });
    expect(await love.getAttribute('aria-pressed')).toBe('false');
    await page.waitForTimeout(300);
    expect(mock.callsTo('POST', 'Rate').length).toBe(3);
    noConsoleErrors();
  });

  it('My List toggles add then remove', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, G('a3'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`.fui-card[data-id="${G('a3')}"].expanded`);
    const btn = c.locator('.fui-list');
    await btn.click();
    await expect.poll(() => mock.callsTo('POST', 'MyList').length).toBe(1);
    expect(mock.callsTo('POST', 'MyList')[0].body).toEqual({ itemId: G('a3'), add: true });
    expect(await btn.getAttribute('aria-pressed')).toBe('true');
    await btn.click();
    await expect.poll(() => mock.callsTo('POST', 'MyList').length).toBe(2);
    expect(mock.callsTo('POST', 'MyList')[1].body).toEqual({ itemId: G('a3'), add: false });
    expect(await btn.getAttribute('aria-pressed')).toBe('false');
    noConsoleErrors();
  });

  it('opens the native details page from a card click and More Info', async () => {
    const page = await open();
    await homeReady(page);
    await card(page, G('a3')).click();
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('a3'));
    expect(await page.locator('#nativeDetails').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    await page.goBack();
    await homeReady(page);
    await page.locator('.fui-hero-more').click();
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('a1'));
  });

  it('shows a rating failure message and rolls back (friendly, no raw error)', async () => {
    const page = await open();
    await homeReady(page);
    await page.route('**/FullUI/Rate', (r) => r.fulfill({ status: 500, body: '{}' }));
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    await c.locator('.fui-rate-up').click();
    await page.waitForSelector('.fui-toast:not(.fui-hidden)');
    expect(await page.locator('.fui-toast').textContent()).toBe("Couldn't save your rating. Try again.");
    expect(await c.locator('.fui-rate-up').getAttribute('aria-pressed')).toBe('false');
    noConsoleErrors(/500/);
  });
});

describe('coming soon', () => {
  it('votes I want this / not for me and clears with vote 0', async () => {
    const page = await open();
    await homeReady(page);
    const soon = page.locator('.fui-row-comingsoon .fui-card').first();
    await soon.scrollIntoViewIfNeeded();
    expect(await soon.locator('.fui-ribbon').textContent()).toBe('Mar 5, 2027');
    const want = soon.locator('.fui-want');
    const nope = soon.locator('.fui-nope');
    await want.click();
    expect(await want.getAttribute('aria-pressed')).toBe('true'); // optimistic
    await expect.poll(() => mock.callsTo('POST', 'Vote').length).toBe(1);
    expect(mock.callsTo('POST', 'Vote')[0].body).toMatchObject({ tmdbId: 501, mediaType: 'movie', vote: 1, title: 'Future Movie', posterPath: '/f.jpg' });
    await want.click();
    await expect.poll(() => mock.callsTo('POST', 'Vote').length).toBe(2);
    expect(mock.callsTo('POST', 'Vote')[1].body).toMatchObject({ tmdbId: 501, vote: 0 });
    expect(await want.getAttribute('aria-pressed')).toBe('false');
    await nope.click();
    await expect.poll(() => mock.callsTo('POST', 'Vote').length).toBe(3);
    expect(mock.callsTo('POST', 'Vote')[2].body).toMatchObject({ vote: -1 });
    expect(await nope.getAttribute('aria-pressed')).toBe('true');
    noConsoleErrors();
  });
});

describe('pages', () => {
  it('Shows and Movies filter by type', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-tab[data-kind="shows"]');
    await page.waitForSelector('.fui-page-shows .fui-row');
    const showIds = await page.locator('.fui-page-shows .fui-row:not(.fui-row-comingsoon) .fui-card').evaluateAll((els) => els.map((e) => (e as HTMLElement).dataset.id));
    expect(showIds.length).toBeGreaterThan(0);
    const showSet = ['s1', 's2', 's3', 'c1'].map(G);
    expect(showIds.every((i) => showSet.includes(i!))).toBe(true);
    expect(await page.locator('.fui-tab.active').textContent()).toBe('Shows');
    expect(await page.locator('.fui-page-shows .fui-row-comingsoon .fui-soon-title').allTextContents()).toEqual(['Future Show']);
    await page.click('.fui-tab[data-kind="movies"]');
    await page.waitForSelector('.fui-page-movies .fui-row');
    const movieIds = await page.locator('.fui-page-movies .fui-row:not(.fui-row-comingsoon) .fui-card').evaluateAll((els) => els.map((e) => (e as HTMLElement).dataset.id));
    const movieSet = ['a1', 'a2', 'a3', 'd1'].map(G);
    expect(movieIds.every((i) => movieSet.includes(i!))).toBe(true);
    expect(mock.callsTo('GET', 'Home').length).toBe(1); // reuses the cached home payload
    noConsoleErrors();
  });

  it('My MowFlix shows continue watching, my list and wanted', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, G('a3'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await c.locator('.fui-list').click();
    const soon = page.locator('.fui-row-comingsoon .fui-card').first();
    await soon.locator('.fui-want').click();
    await expect.poll(() => mock.callsTo('POST', 'Vote').length).toBe(1);
    await page.click('.fui-tab[data-kind="myserver"]');
    await page.waitForSelector('.fui-page-myserver .fui-row');
    expect(await page.locator('.fui-page-title').textContent()).toBe('My MowFlix');
    expect(await page.locator('.fui-row-title').allTextContents()).toEqual(['Continue Watching', 'My List', 'I Want This']);
    expect(await page.locator('.fui-row-mylist .fui-card').getAttribute('data-id')).toBe(G('a3'));
    await page.screenshot({ path: join(shots, 'desktop-myserver.png') });
    noConsoleErrors();
  });

  it('Search debounces, shows mode and results, handles failure with a retry', async () => {
    const page = await open({ setup: (m) => (m.ollamaEnabled = true) });
    await homeReady(page);
    await page.click('.fui-nav-search');
    await page.waitForSelector('.fui-search-input');
    expect(await page.evaluate(() => document.activeElement?.className)).toBe('fui-search-input');
    await page.keyboard.type('dune', { delay: 40 });
    await page.waitForSelector('.fui-grid .fui-card');
    expect(await page.locator('.fui-grid .fui-card').count()).toBe(1);
    expect(await page.locator('.fui-search-mode').textContent()).toContain('Smart search');
    const searches = mock.calls.filter((c) => c.path.startsWith('Search'));
    expect(searches.map((s) => s.path)).toEqual(['Search?q=dune']); // 4 keystrokes, one request
    expect(page.url()).toContain('fui=search');
    await page.screenshot({ path: join(shots, 'desktop-search.png') });
    // no results
    await page.fill('.fui-search-input', 'zzzz');
    await page.waitForSelector('.fui-search-status:has-text("No results")');
    // failure
    mock.failSearch = true;
    await page.fill('.fui-search-input', 'dune2');
    await page.waitForSelector('.fui-search-status .fui-retry');
    expect(await page.locator('.fui-search-status').textContent()).toContain("Search isn't working right now");
    mock.failSearch = false;
    await page.fill('.fui-search-input', 'dune');
    await page.waitForSelector('.fui-grid .fui-card');
    noConsoleErrors(/500/);
  });

  it('notification bell lists items and marks them read', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-bell-count:not(.fui-hidden)');
    await page.click('.fui-bell');
    await page.waitForSelector('.fui-notes:not(.fui-hidden) .fui-note');
    expect(await page.locator('.fui-note').count()).toBe(3);
    expect(await page.locator('.fui-note.unread').count()).toBe(2);
    await page.click('.fui-notes .fui-link');
    await expect.poll(() => mock.callsTo('POST', 'Notifications/Read').length).toBe(1);
    expect(await page.locator('.fui-bell-count').isVisible()).toBe(false);
    expect(await page.locator('.fui-note.unread').count()).toBe(0);
    // Escape closes the dropdown
    await page.keyboard.press('Escape');
    expect(await page.locator('.fui-notes').isVisible()).toBe(false);
    noConsoleErrors();
  });

  it('notification failures are friendly and recoverable', async () => {
    const page = await open();
    mock.failNotifications = true;
    await page.reload();
    await homeReady(page);
    await page.click('.fui-bell');
    await page.waitForSelector('.fui-notes-retry');
    expect(await page.locator('.fui-notes').textContent()).toContain("We couldn't load your notifications.");
    mock.failNotifications = false;
    await page.click('.fui-notes-retry');
    await page.waitForSelector('.fui-note');
    noConsoleErrors(/500/);
  });
});

describe('failure states are friendly and recoverable', () => {
  it('Home 500 falls back to the native home with a visible Try again; recovers', async () => {
    const page = await open();
    await homeReady(page);
    mock.failHome = true;
    await page.reload(); // fresh load, no cached home payload
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    const text = (await page.locator('#fullui-banner').textContent()) || '';
    expect(text).toContain("couldn't load right now");
    expect(text).not.toMatch(/500|error|exception|undefined|\[object/i);
    expect(await page.locator('#fullui-banner button').textContent()).toBe('Try again');
    await page.screenshot({ path: join(shots, 'desktop-fallback.png') });
    mock.failHome = false;
    await page.click('#fullui-banner button');
    await page.waitForSelector(`${root}:not(.fui-hidden) .fui-hero`);
    expect(await page.locator('#fullui-banner').isVisible()).toBe(false);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(false);
    noConsoleErrors(/500/);
  });

  it('Home 500 on first load shows the native home, never a blank screen', async () => {
    mock.reset();
    mock.failHome = true;
    errors = [];
    ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await ctx.newPage();
    page.on('pageerror', (e) => errors.push('pageerror: ' + e.message));
    // reset() inside open() would clear the flag, so build the page by hand
    await page.goto(mock.origin + '/#/home');
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    expect(errors).toEqual([]);
  });

  it('a failing My MowFlix page shows a plain-English message with Try again and classic view', async () => {
    const page = await open();
    await homeReady(page);
    // make the cached payload expire by failing a fresh load
    mock.failHome = true;
    await page.evaluate(() => {
      location.hash = '#/home?fui=myserver';
    });
    await page.waitForSelector('.fui-page-myserver');
    await page.route('**/FullUI/MyServer', (r) => r.fulfill({ status: 500, body: '{"error":"stack trace at Foo.Bar"}' }));
    await page.evaluate(() => (location.hash = '#/home'));
    await page.waitForSelector(`${root}:not(.fui-hidden)`);
    // go to myserver again, which now fails
    await page.evaluate(() => (location.hash = '#/home?fui=myserver'));
    await page.waitForSelector('.fui-error');
    const msg = (await page.locator('.fui-error').textContent()) || '';
    expect(msg).toContain("We couldn't load this page");
    expect(msg).not.toMatch(/500|stack|Foo\.Bar|undefined|\[object|Error:/i);
    expect(await page.locator('.fui-error button').allTextContents()).toEqual(['Try again', 'Use classic view']);
    // retry after the server recovers
    await page.unroute('**/FullUI/MyServer');
    await page.click('.fui-error .fui-btn-primary');
    await page.waitForSelector('.fui-page-myserver .fui-row');
    noConsoleErrors(/500/);
  });

  it('"Use classic view" in the nav reaches the native home, and the banner switches back', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-classic');
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    expect(await page.locator('#fullui-banner').textContent()).toContain("You're using the classic view.");
    // stays classic across a reload
    await page.reload();
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    await page.click('#fullui-banner button');
    await page.waitForSelector(`${root}:not(.fui-hidden) .fui-hero`);
    expect(await page.locator('#fullui-banner').isVisible()).toBe(false);
    // also reachable from the profile menu
    await page.click('.fui-avatar');
    expect(await page.locator('.fui-menu .fui-menu-item').allTextContents()).toEqual(['Settings', 'Use classic view', 'Dashboard', 'Sign out']);
    noConsoleErrors();
  });

  it('admin dashboard link stays reachable from the profile menu', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-avatar');
    await page.click('.fui-menu a:has-text("Dashboard")');
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeDash').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
  });

  it('a 401 hands control back to the native UI quietly (Jellyfin handles sign-in)', async () => {
    const page = await open();
    await homeReady(page);
    mock.failHome = true;
    mock.homeStatus = 401;
    await page.reload();
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    expect(await page.locator('#fullui-banner').isVisible()).toBe(false);
    noConsoleErrors(/401/);
  });
});

describe('keyboard / D-pad navigation', () => {
  it('moves focus with arrows, shows a focus ring, expands on focus and Back collapses', async () => {
    const page = await open();
    await homeReady(page);
    await page.keyboard.press('ArrowDown'); // first focus lands in the page
    const first = await page.evaluate(() => document.activeElement?.className || '');
    expect(first).not.toBe('');
    const c = card(page, G('a1')).locator('.fui-art');
    await c.focus();
    await page.keyboard.press('ArrowRight');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe(G('a2'));
    await page.keyboard.press('ArrowRight');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe(G('s1'));
    await page.waitForSelector(`.fui-card[data-id="${G('s1')}"].expanded`);
    await page.keyboard.press('ArrowLeft');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe(G('a2'));
    // focus ring is visible on the art
    const outline = await page.evaluate((sel) => getComputedStyle(document.querySelector(sel)!).outlineStyle, `.fui-card[data-id="${G("a2")}"] .fui-art`);
    expect(outline).toBe('solid');
    // down goes to an action button of the expanded card, then Back collapses
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    await page.keyboard.press('ArrowDown');
    expect(await page.evaluate(() => !!document.activeElement?.closest('.fui-info'))).toBe(true);
    await page.keyboard.press('Escape');
    await page.waitForSelector('.fui-card.expanded', { state: 'detached' });
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe(G('a2'));
    // Enter opens the details page
    await page.keyboard.press('Enter');
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('a2'));
    noConsoleErrors();
  });

  it('uses 10-foot sizing on large viewports', async () => {
    const page = await open({ width: 1920, height: 1080 });
    await homeReady(page);
    expect(await page.evaluate(() => getComputedStyle(document.getElementById('fullui-root')!).fontSize)).toBe('22px');
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    await page.screenshot({ path: join(shots, 'tv-1080p-card-expanded.png') });
  });
});

describe('mobile', () => {
  it('lays out below 700px without horizontal overflow; tap expands; long press opens info', async () => {
    const page = await open({ mobile: true });
    await homeReady(page);
    const overflow = await page.evaluate(() => ({
      doc: document.documentElement.scrollWidth - window.innerWidth,
      root: document.getElementById('fullui-root')!.scrollWidth - document.getElementById('fullui-root')!.clientWidth,
    }));
    expect(overflow.doc).toBeLessThanOrEqual(0);
    expect(overflow.root).toBeLessThanOrEqual(0);
    expect(await page.locator('.fui-tabs').isVisible()).toBe(true);
    await page.screenshot({ path: join(shots, 'mobile-home.png') });

    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.tap();
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    expect(await c.locator('.fui-info').isVisible()).toBe(true);
    expect(await page.evaluate(() => location.hash)).not.toContain('details');
    await page.screenshot({ path: join(shots, 'mobile-card-expanded.png') });

    // long press on another card opens its info (details) page
    const target = card(page, G('s2'));
    await target.scrollIntoViewIfNeeded();
    await target.evaluate((el) => {
      el.dispatchEvent(new PointerEvent('pointerdown', { pointerType: 'touch', bubbles: true }));
    });
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('s2'), { timeout: 3000 });
    noConsoleErrors();
  });

  it('mobile screenshots of the other pages', async () => {
    const page = await open({ mobile: true });
    await homeReady(page);
    await page.evaluate(() => (location.hash = '#/home?fui=search&q=dune'));
    await page.waitForSelector('.fui-grid .fui-card');
    await page.screenshot({ path: join(shots, 'mobile-search.png') });
    await page.evaluate(() => (location.hash = '#/home?fui=myserver'));
    await page.waitForSelector('.fui-page-myserver .fui-row');
    await page.screenshot({ path: join(shots, 'mobile-myserver.png') });
    await page.click('.fui-bell');
    await page.waitForSelector('.fui-note');
    const box = await page.locator('.fui-notes').boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);
    await page.screenshot({ path: join(shots, 'mobile-notifications.png') });
    for (const w of [390]) {
      const o = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      expect(o).toBeLessThanOrEqual(0);
      void w;
    }
    noConsoleErrors();
  });

  it('Home 500 fallback screenshot on mobile', async () => {
    const page = await open({ mobile: true });
    await homeReady(page);
    mock.failHome = true;
    await page.reload();
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    const box = await page.locator('#fullui-banner').boundingBox();
    expect(box!.x + box!.width).toBeLessThanOrEqual(390);
    await page.screenshot({ path: join(shots, 'mobile-fallback.png') });
  });
});

// ---------------------------------------------------------------------------------------------
// Regression tests for the review findings (docs/BUGS.md)
// ---------------------------------------------------------------------------------------------
const win = (page: Page) => ({
  get: <T>(fn: () => T) => page.evaluate(fn),
});
void win;

describe('session: sign out, user switch, pre-login (B-08, B-09, B-33)', () => {
  it('Sign out uses Dashboard.logout(), releases the overlay and lands on the login page', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-avatar');
    await page.click('.fui-menu-item:has-text("Sign out")');
    await page.waitForFunction(() => location.hash === '#/login');
    expect(await page.evaluate(() => (window as any).__logout)).toBe(1);
    expect(await page.locator(root).isVisible()).toBe(false);
    expect(await page.locator('#nativeLogin').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    // our polling stopped: no more FullUI requests with a dead token
    const n = mock.calls.length;
    await page.waitForTimeout(600);
    expect(mock.calls.length).toBe(n);
    noConsoleErrors();
  });

  it('a different user signing in never sees the previous user\'s rows, bell or Dashboard link', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-bell-count:not(.fui-hidden)');
    expect(await page.locator('.fui-row-title').allTextContents()).toContain('Top Picks for Sam');
    await page.click('.fui-avatar');
    await page.click('.fui-menu-item:has-text("Sign out")');
    await page.waitForFunction(() => location.hash === '#/login');
    // log in as the second account within the 60 s cache window
    await page.evaluate(() => {
      (window as any).__token = 'tok2';
      location.hash = '#/home';
    });
    await page.waitForSelector('.fui-row-title:has-text("Top Picks for Alex")');
    expect(await page.locator('body').textContent()).not.toContain('Top Picks for Sam');
    expect(await page.locator('.fui-bell-count').isVisible()).toBe(false);
    await page.click('.fui-avatar');
    await expect.poll(() => page.locator('.fui-menu .fui-menu-item').allTextContents()).toEqual(['Settings', 'Use classic view', 'Sign out']);
    const homeUsers = mock.callsTo('GET', 'Home').map((c) => c.user);
    expect(homeUsers).toEqual([G('u1'), G('u2')]); // refetched for the new user, never reused
    noConsoleErrors();
  });

  it('switching the token in place (no navigation) also drops all cached data', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => {
      (window as any).__token = 'tok2';
      document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true }));
    });
    await page.waitForSelector('.fui-row-title:has-text("Top Picks for Alex")');
    expect(await page.locator('.fui-row-title').allTextContents()).not.toContain('Top Picks for Sam');
    expect(await page.locator('.fui-bell-count').isVisible()).toBe(false);
  });

  it('makes no FullUI request before sign-in, and calls Status only after it', async () => {
    const page = await open({ signedOut: true });
    await page.waitForSelector('#nativeLogin', { state: 'visible' });
    await page.waitForTimeout(800);
    expect(mock.calls).toEqual([]); // no Status/Home with Token="null" on the login page
    expect(await page.locator(root).isVisible()).toBe(false);
    await page.evaluate(() => {
      (window as any).__token = 'tok';
      location.hash = '#/home';
    });
    await homeReady(page);
    expect(mock.callsTo('GET', 'Status').length).toBe(1);
    expect(mock.calls.every((c) => c.auth === 'MediaBrowser Token="tok"')).toBe(true);
    noConsoleErrors();
  });
});

describe('server base path and the real ApiClient contract (B-01)', () => {
  it('works when jellyfin is served under /jellyfin and the client rejects getUrl("")', async () => {
    const page = await open({ base: '/jellyfin' });
    await homeReady(page);
    expect(mock.callsTo('GET', 'Home').length).toBe(1);
    // hero + card images came from under the base path (the mock 404s anything else)
    await page.waitForFunction(() => Array.from(document.images).every((i) => i.complete && i.naturalWidth > 0));
    expect(mock.images.length).toBeGreaterThan(0);
    expect(mock.images.every((i) => i.startsWith('/Items/') || i.startsWith('/Users/'))).toBe(true);
    noConsoleErrors();
  });

  it('the mock ApiClient throws like jellyfin-apiclient for an empty url name', async () => {
    const page = await open();
    await homeReady(page);
    const msg = await page.evaluate(() => {
      try {
        (window as any).ApiClient.getUrl('');
        return 'no error';
      } catch (e) {
        return (e as Error).message;
      }
    });
    expect(msg).toBe('Url name cannot be empty');
  });
});

describe('experimental layout (B-26)', () => {
  it('hides the MUI app bar and drawer only while FullUI owns the route', async () => {
    const page = await open({ experimental: true });
    await homeReady(page);
    expect(await page.locator('#muiAppBar').isVisible()).toBe(false);
    expect(await page.locator('#muiDrawer').isVisible()).toBe(false);
    await page.evaluate(() => (location.hash = '#/dashboard'));
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#muiAppBar').isVisible()).toBe(true);
    expect(await page.locator('#muiDrawer').isVisible()).toBe(true);
  });

  it('does not restyle native pages (no forced dark background on the page chrome)', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => (location.hash = '#/dashboard'));
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    // the mock page's own inline styles must win: FullUI adds no global body/header rules
    expect(await page.evaluate(() => getComputedStyle(document.getElementById('nativeHeader')!).backgroundColor)).toBe('rgb(32, 32, 32)');
  });
});

describe('navigation inside FullUI (B-27, B-28, B-29, B-53)', () => {
  it('switching tabs does not touch the native router (no hashchange / viewshow), and Back returns to Home', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => {
      (window as any).__nav = 0;
      window.addEventListener('hashchange', () => (window as any).__nav++);
      document.addEventListener('viewshow', () => (window as any).__nav++);
      history.replaceState({ usr: { keep: 1 }, key: 'abc', idx: 0 }, '', location.href);
    });
    await page.click('.fui-tab[data-kind="shows"]');
    await page.waitForSelector('.fui-page-shows .fui-row');
    expect(await page.evaluate(() => location.hash)).toContain('fui=shows');
    await page.click('.fui-tab[data-kind="movies"]');
    await page.waitForSelector('.fui-page-movies .fui-row');
    await page.click('.fui-nav-search');
    await page.waitForSelector('.fui-search-input');
    await page.keyboard.type('dune');
    await page.waitForSelector('.fui-grid .fui-card');
    // Back (Escape on an empty search box) goes to the Home tab, not out of the app
    await page.fill('.fui-search-input', '');
    await page.keyboard.press('Escape');
    await page.waitForSelector('.fui-page-home');
    expect(await page.evaluate(() => (window as any).__nav)).toBe(0);
    expect(await page.evaluate(() => history.state)).toEqual({ usr: { keep: 1 }, key: 'abc', idx: 0 }); // router state kept
    expect(mock.callsTo('GET', 'Home').length).toBe(1);
  });

  it('Escape in a non-empty search box clears it instead of leaving the page', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-nav-search');
    await page.waitForSelector('.fui-search-input');
    await page.keyboard.type('dune');
    await page.waitForSelector('.fui-grid .fui-card');
    await page.keyboard.press('Escape');
    expect(await page.inputValue('.fui-search-input')).toBe('');
    expect(await page.locator('.fui-page-search').count()).toBe(1);
  });

  it('keeps the overlay until the native page has appeared (no flash of the classic home)', async () => {
    const page = await open();
    await homeReady(page);
    // Make the native route slow: the hash changes first, the view appears later.
    await page.evaluate(() => {
      (window as any).__vsHold = true;
    });
    const seen = await page.evaluate(async () => {
      const rootEl = document.getElementById('fullui-root')!;
      const samples: boolean[] = [];
      location.hash = '#/dashboard';
      for (let i = 0; i < 5; i++) {
        await new Promise((r) => setTimeout(r, 20));
        samples.push(getComputedStyle(document.getElementById('nativeHome')!).display !== 'none' && rootEl.classList.contains('fui-hidden'));
      }
      return samples;
    });
    // the native *home* page is never the thing on screen while we hand over to the dashboard
    expect(seen.every((x) => x === false)).toBe(true);
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeDash').isVisible()).toBe(true);
  });

  it('a Libraries menu links to the native library pages', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-libs-btn');
    expect(await page.locator('.fui-libs-menu a').allTextContents()).toEqual(['Movies library', 'TV Shows library', 'Music', 'Live TV', 'Favorites']);
    expect(await page.locator('.fui-libs-menu a').evaluateAll((els) => els.map((e) => e.getAttribute('href')))).toEqual([
      '#/movies',
      '#/tv',
      '#/music',
      '#/livetv',
      '#/home?tab=1',
    ]);
    await page.click('.fui-libs-menu a:has-text("Music")');
    await page.waitForFunction(() => location.hash === '#/music');
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
  });

  it('Favorites (home tab 1) is a native view, not ours', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-libs-btn');
    await page.click('.fui-libs-menu a:has-text("Favorites")');
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
  });
});

describe('Play (B-25)', () => {
  it('Play opens the native details page and presses its own Play button; More Info only opens it', async () => {
    const page = await open();
    await homeReady(page);
    await page.locator('.fui-hero-play').click();
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('a1'));
    await page.waitForFunction(() => (window as any).__played === 1);
    await page.goBack();
    await homeReady(page);
    await page.locator('.fui-hero-more').click();
    await page.waitForFunction((id) => location.hash.startsWith('#/details?id=' + id), G('a1'));
    await page.waitForTimeout(500);
    expect(await page.evaluate(() => (window as any).__played)).toBe(1); // still just the one press
  });
});

describe('trailers (B-10, B-57, B-31)', () => {
  it('creates the iframe itself with a referrer policy and enablejsapi, and loads the API script once', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on', { timeout: 5000 });
    const f = await page.evaluate(() => (window as any).__yt.frames[0]);
    expect(f.referrerpolicy).toBe('strict-origin-when-cross-origin');
    expect(f.src).toMatch(/^https:\/\/www\.youtube-nocookie\.com\/embed\/dQw4w9WgXcQ\?/);
    expect(f.src).toContain('enablejsapi=1');
    expect(f.src).toContain('mute=1');
    expect(f.src).toContain('origin=' + encodeURIComponent(mock.origin));
    expect(f.allow).toContain('autoplay');
    // hover a card trailer: still only one api <script>
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`[data-id="${G('a2')}"] .yt-stub`, { timeout: 5000 });
    expect(await page.locator('script[src="https://www.youtube.com/iframe_api"]').count()).toBe(1);
    noConsoleErrors();
  });

  it('honours trailersEnabled:false from Status (no player, no YouTube script)', async () => {
    const page = await open({ setup: (m) => (m.statusExtra = { trailersEnabled: false }) });
    await homeReady(page);
    await page.waitForTimeout(1500);
    expect(await page.locator('.yt-stub').count()).toBe(0);
    expect(await page.locator('script[src="https://www.youtube.com/iframe_api"]').count()).toBe(0);
    expect(await page.locator('.fui-hero-img').isVisible()).toBe(true);
  });

  it('a card trailer takes over from the hero cleanly, and the hero resumes afterwards', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`[data-id="${G('a2')}"] .yt-stub`, { timeout: 5000 });
    expect(await page.locator('.fui-hero.trailer-on').count()).toBe(0); // no dead "playing" state
    expect(await page.locator('.fui-hero-mute').isVisible()).toBe(false);
    // the hero only plays while it is on screen: scroll back up, then let the card trailer go
    await page.evaluate(() => (document.getElementById('fullui-root')!.scrollTop = 0));
    await page.mouse.move(5, 5);
    await page.waitForSelector('.fui-hero.trailer-on', { timeout: 5000 }); // hero resumed
    expect(await page.evaluate(() => (window as any).__yt.alive)).toBe(1);
  });
});

describe('home failures (B-16)', () => {
  it('an empty Home (HTTP 200, no rows) falls back to the native home with the friendly banner', async () => {
    const page = await open({ setup: (m) => (m.emptyHome = true) });
    await page.waitForSelector('#fullui-banner:not(.fui-hidden)');
    expect(await page.locator('#nativeHome').isVisible()).toBe(true);
    expect(await page.locator(root).isVisible()).toBe(false);
    expect(await page.locator('#fullui-banner').textContent()).toContain("couldn't load right now");
  });

  it('a failing Shows page shows a plain-English message with Try again and classic view', async () => {
    const page = await open({ hash: '/home?fui=shows', setup: (m) => (m.failHome = true) });
    await page.waitForSelector('.fui-error');
    const msg = (await page.locator('.fui-error').textContent()) || '';
    expect(msg).toContain("We couldn't load this page");
    expect(msg).not.toMatch(/500|stack|undefined|\[object|Error:|Problem/i);
    expect(await page.locator('.fui-error button').allTextContents()).toEqual(['Try again', 'Use classic view']);
    mock.failHome = false;
    await page.click('.fui-error .fui-btn-primary');
    await page.waitForSelector('.fui-page-shows .fui-row');
    noConsoleErrors(/500/);
  });
});

describe('caching and polling (B-30, B-62)', () => {
  it('does not refetch notifications on every navigation', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-bell-count:not(.fui-hidden)');
    await page.click('.fui-tab[data-kind="shows"]');
    await page.click('.fui-tab[data-kind="movies"]');
    await page.click('.fui-tab[data-kind="myserver"]');
    await page.click('.fui-tab[data-kind="home"]');
    await page.evaluate(() => (location.hash = '#/dashboard'));
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    await page.evaluate(() => (location.hash = '#/home'));
    await homeReady(page);
    expect(mock.callsTo('GET', 'Notifications').length).toBe(1);
  });

  it('My List changes and returning from a native page refresh the cached Home', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, G('a3'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await c.locator('.fui-list').click();
    await expect.poll(() => mock.callsTo('POST', 'MyList').length).toBe(1);
    await page.click('.fui-tab[data-kind="shows"]');
    await page.waitForSelector('.fui-page-shows .fui-row');
    expect(mock.callsTo('GET', 'Home').length).toBe(2); // cache dropped after the change
    await page.evaluate(() => (location.hash = '#/details?id=' + 'x'));
    await page.waitForSelector(`${root}.fui-hidden`, { state: 'attached' });
    await page.evaluate(() => (location.hash = '#/home'));
    await homeReady(page);
    expect(mock.callsTo('GET', 'Home').length).toBe(3); // fresh progress after playback / details
  });

  it('rapid clicks are queued, not dropped', async () => {
    const page = await open({ setup: (m) => (m.rateDelay = 250) });
    await homeReady(page);
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`.fui-card[data-id="${G('a2')}"].expanded`);
    await c.locator('.fui-rate-up').click();
    await c.locator('.fui-rate-love').click(); // while "like" is still being saved
    await expect.poll(() => mock.callsTo('POST', 'Rate').length, { timeout: 5000 }).toBe(2);
    expect(mock.callsTo('POST', 'Rate').map((r) => (r.body as { rating: number }).rating)).toEqual([1, 2]);
    expect(await c.locator('.fui-rate-love').getAttribute('aria-pressed')).toBe('true');
    await expect.poll(() => mock.ratings.get(G('a2'))).toBe(2);
  });
});

describe('look and robustness (B-55, B-59, B-60, B-32, B-54, B-50)', () => {
  it('ignores an invalid accent colour from the server', async () => {
    const page = await open({ setup: (m) => (m.accent = 'e50914;}</style>') });
    await homeReady(page);
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--fullui-accent').trim())).toBe('#e50914');
    const page2 = await open({ setup: (m) => (m.accent = '#0a84ff') });
    await homeReady(page2);
    expect(await page2.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--fullui-accent').trim())).toBe('#0a84ff');
  });

  it('only one overlay even if the bundle is injected twice', async () => {
    const page = await open();
    await homeReady(page);
    await page.evaluate(() => {
      const s = document.createElement('script');
      s.src = '/fullui.js';
      document.body.appendChild(s);
    });
    await page.waitForTimeout(400);
    expect(await page.locator('#fullui-root').count()).toBe(1);
    expect(await page.locator('.fui-nav').count()).toBe(1);
  });

  it('search placeholder only promises AI search when Ollama is on', async () => {
    const page = await open();
    await homeReady(page);
    await page.click('.fui-nav-search');
    await page.waitForSelector('.fui-search-input');
    expect(await page.getAttribute('.fui-search-input', 'placeholder')).not.toContain('describe');
    const page2 = await open({ setup: (m) => (m.ollamaEnabled = true) });
    await homeReady(page2);
    await page2.click('.fui-nav-search');
    await page2.waitForSelector('.fui-search-input');
    expect(await page2.getAttribute('.fui-search-input', 'placeholder')).toContain('describe');
  });

  it('cards: artwork is the single button, one tab stop per row, no nested interactive roles', async () => {
    const page = await open();
    await homeReady(page);
    expect(await page.locator('.fui-card[role="button"]').count()).toBe(0);
    expect(await page.locator('.fui-row-toppicks .fui-art[tabindex="0"]').count()).toBe(1);
    expect(await page.locator('.fui-art[role="button"]').count()).toBeGreaterThan(5);
    const tabStops = await page.locator(`${root} .fui-art[tabindex="0"]`).count();
    expect(tabStops).toBeLessThanOrEqual(5); // one per row, not one per card
    // roving: focusing another card moves the tab stop
    await card(page, G('s1')).locator('.fui-art').focus();
    expect(await page.locator('.fui-row-toppicks .fui-art[tabindex="0"]').evaluate((e) => (e.closest('.fui-card') as HTMLElement).dataset.id)).toBe(G('s1'));
  });

  it('menus: arrow keys move between items and Escape returns focus to the button that opened them', async () => {
    const page = await open();
    await homeReady(page);
    await page.focus('.fui-avatar');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-menu:not(.fui-hidden)');
    expect(await page.evaluate(() => document.activeElement?.textContent)).toBe('Settings');
    await page.keyboard.press('ArrowDown');
    expect(await page.evaluate(() => document.activeElement?.textContent)).toBe('Use classic view');
    await page.keyboard.press('Escape');
    expect(await page.evaluate(() => document.activeElement?.className)).toContain('fui-avatar');
    // the bell: Escape goes back to the bell, not the avatar
    await page.focus('.fui-bell');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-notes:not(.fui-hidden) .fui-note');
    await page.keyboard.press('Escape');
    expect(await page.evaluate(() => document.activeElement?.className)).toContain('fui-bell');
    // "Mark all read" is not inside the role=menu list
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-notes:not(.fui-hidden) .fui-note');
    expect(await page.locator('.fui-notes [role="menu"] .fui-link').count()).toBe(0);
  });
});


// ===================================================================================================
// Netflix-feel wave 1: hero lifecycle, card/row polish, player assist, i18n, TMDB attribution, search
// ===================================================================================================

const setHidden = (page: Page, hidden: boolean) =>
  page.evaluate((v) => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => v });
    document.dispatchEvent(new Event('visibilitychange'));
  }, hidden);
const yt = (page: Page) =>
  page.evaluate(() => {
    const y = (window as any).__yt;
    return { paused: y.paused as number, resumed: y.resumed as number, seeks: y.seeks as number[], alive: y.alive as number };
  });
const scrollRoot = (page: Page, top: number | 'end') =>
  page.evaluate((t) => {
    const r = document.getElementById('fullui-root')!;
    r.scrollTop = t === 'end' ? r.scrollHeight : t;
  }, top);

describe('hero lifecycle (WCAG 2.2.2, pause off-screen, replay)', () => {
  it('pauses when the tab is hidden and resumes when it is visible again', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    const base = await yt(page);
    await setHidden(page, true);
    await page.waitForFunction((n) => (window as any).__yt.paused > n, base.paused);
    await setHidden(page, false);
    await page.waitForFunction((n) => (window as any).__yt.resumed > n, base.resumed);
    expect(await page.locator('.fui-hero.trailer-on').count()).toBe(1);
    noConsoleErrors();
  });

  it('pauses when the hero scrolls out of view and resumes when it is back', async () => {
    const page = await open({ setup: (m) => (m.extraRows = 6) });
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    const base = await yt(page);
    await scrollRoot(page, 'end');
    await page.waitForFunction((n) => (window as any).__yt.paused > n, base.paused);
    await scrollRoot(page, 0);
    await page.waitForFunction((n) => (window as any).__yt.resumed > n, base.resumed);
    noConsoleErrors();
  });

  it('the trailer does not loop: when it ends the hero fades back to the poster and offers Replay', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    expect(await page.evaluate(() => (window as any).__yt.frames[0].src)).not.toContain('loop=1');
    await page.evaluate(() => (window as any).__yt.players.at(-1).emit(0)); // YouTube ENDED
    await page.waitForSelector('.fui-hero:not(.trailer-on)');
    expect(await page.locator('.fui-hero-img').isVisible()).toBe(true);
    expect(await page.locator('.fui-hero-replay').isVisible()).toBe(true);
    expect(await page.locator('.fui-hero-pause').isVisible()).toBe(false);
    expect(await page.locator('.fui-hero-mute').isVisible()).toBe(false);
    expect(await page.locator('.fui-hero-replay').getAttribute('aria-label')).toBe('Replay trailer');
    await page.click('.fui-hero-replay');
    await page.waitForSelector('.fui-hero.trailer-on');
    expect((await yt(page)).seeks).toEqual([0]);
    expect(await page.locator('.fui-hero-replay').isVisible()).toBe(false);
    expect(await page.locator('.fui-hero-pause').isVisible()).toBe(true);
    noConsoleErrors();
  });

  it('has an accessible pause/play control with aria-pressed that the viewer always wins with', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    const btn = page.locator('.fui-hero-pause');
    expect(await btn.getAttribute('aria-pressed')).toBe('false');
    expect(await btn.getAttribute('aria-label')).toBe('Pause trailer');
    const base = await yt(page);
    await btn.click();
    expect(await btn.getAttribute('aria-pressed')).toBe('true');
    expect(await btn.getAttribute('aria-label')).toBe('Pause trailer'); // constant label: state lives in aria-pressed
    await page.waitForFunction((n) => (window as any).__yt.paused > n, base.paused);
    // hiding and showing the tab must NOT restart a trailer the viewer paused
    await setHidden(page, true);
    await setHidden(page, false);
    await page.waitForTimeout(300);
    expect((await yt(page)).resumed).toBe(base.resumed);
    await btn.click();
    expect(await btn.getAttribute('aria-pressed')).toBe('false');
    await page.waitForFunction((n) => (window as any).__yt.resumed > n, base.resumed);
    // keyboard operable
    await btn.focus();
    await page.keyboard.press('Enter');
    expect(await btn.getAttribute('aria-pressed')).toBe('true');
    noConsoleErrors();
  });

  it('a viewer-paused trailer stays paused after a card trailer takes over and leaves', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero.trailer-on');
    await page.click('.fui-hero-pause');
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`[data-id="${G('a2')}"] .yt-stub`, { timeout: 5000 });
    await scrollRoot(page, 0);
    await page.mouse.move(5, 5);
    await page.waitForTimeout(1500);
    expect(await page.locator('.fui-hero.trailer-on').count()).toBe(0); // not restarted behind the viewer's back
  });

  it('shows no trailer controls with prefers-reduced-motion', async () => {
    const page = await open({ reducedMotion: true });
    await homeReady(page);
    await page.waitForTimeout(1200);
    expect(await page.locator('.fui-hero-pause, .fui-hero-replay, .fui-hero-mute').evaluateAll((els) => els.filter((e) => (e as HTMLElement).offsetParent !== null).length)).toBe(0);
  });

  it('boxed maturity badge, green match % and the reason on the hero; graceful when absent', async () => {
    const page = await open({ setup: (m) => (m.cardExtra[G('a1')] = { matchPercent: 97, reason: 'Because you watched Dark' }) });
    await homeReady(page);
    expect(await page.locator('.fui-hero .fui-rated').textContent()).toBe('PG-13');
    expect(await page.locator('.fui-hero .fui-rated').evaluate((e) => getComputedStyle(e).borderTopStyle)).toBe('solid');
    expect(await page.locator('.fui-hero .fui-match').textContent()).toBe('97% Match');
    expect(await page.locator('.fui-hero .fui-match').evaluate((e) => getComputedStyle(e).color)).toBe('rgb(70, 211, 105)');
    expect(await page.locator('.fui-hero .fui-reason').textContent()).toBe('Because you watched Dark');
    const page2 = await open();
    await homeReady(page2);
    expect(await page2.locator('.fui-hero .fui-why').count()).toBe(0);
    expect(await page2.locator('.fui-hero .fui-rated').count()).toBe(1);
  });

  it('expanded cards show match % and reason; Continue Watching shows episode label and time left', async () => {
    const page = await open({
      setup: (m) => {
        m.cardExtra[G('a2')] = { matchPercent: 88, reason: 'Because you liked Dune' };
        m.cardExtra[G('c1')] = { seriesLabel: 'S2:E5', minutesLeft: 42 };
      },
    });
    await homeReady(page);
    const c = card(page, G('a2'));
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector(`[data-row-id="toppicks"] .fui-card[data-id="${G('a2')}"].expanded`);
    expect(await c.locator('.fui-match').textContent()).toBe('88% Match');
    expect(await c.locator('.fui-reason').textContent()).toBe('Because you liked Dune');
    expect(await c.locator('.fui-info .fui-rated').textContent()).toBe('PG-13');
    const cw = card(page, G('c1'), 'continue');
    expect(await cw.locator('.fui-cwlabel').textContent()).toBe('S2:E5 · 42m left');
    // a title without the optional fields renders exactly as before
    const c3 = card(page, G('a3'));
    await c3.scrollIntoViewIfNeeded();
    await c3.hover();
    await page.waitForSelector(`[data-row-id="toppicks"] .fui-card[data-id="${G('a3')}"].expanded`);
    expect(await c3.locator('.fui-why').count()).toBe(0);
    noConsoleErrors();
  });
});

describe('card and row polish', () => {
  for (const width of [1280, 800]) {
    it(`expanded cards are never clipped, first/middle/last, at ${width}px; neighbours do not move`, async () => {
      const page = await open({ width });
      await homeReady(page);
      for (const rowId of ['toppicks', 'top10-movies', 'trending']) {
        const sel = `[data-row-id="${rowId}"] .fui-card`;
        const n = await page.locator(sel).count();
        for (const which of ['start', 'end']) {
          await page.evaluate(
            ([s, w]) => {
              const strip = document.querySelector(`[data-row-id="${s}"] .fui-strip`) as HTMLElement;
              strip.scrollLeft = w === 'end' ? strip.scrollWidth : 0;
            },
            [rowId, which],
          );
          const idx = which === 'end' ? n - 1 : 0;
          const c = page.locator(sel).nth(idx);
          await c.scrollIntoViewIfNeeded();
          const nextBefore = n > idx + 1 ? await page.locator(sel).nth(idx + 1).evaluate((e) => (e as HTMLElement).offsetLeft) : null;
          await c.hover();
          await page.waitForSelector(`[data-row-id="${rowId}"] .fui-card.expanded`);
          await page.waitForTimeout(350); // transform transition
          const box = await c.evaluate((el) => {
            const r = (el.querySelector('.fui-info') as HTMLElement).getBoundingClientRect();
            const a = (el.querySelector('.fui-art') as HTMLElement).getBoundingClientRect();
            return { left: Math.min(r.left, a.left), right: Math.max(r.right, a.right), origin: (el as HTMLElement).dataset.origin };
          });
          expect(['left', 'center', 'right']).toContain(box.origin);
          expect(box.left, `${rowId} ${which} left`).toBeGreaterThanOrEqual(-1);
          expect(box.right, `${rowId} ${which} right`).toBeLessThanOrEqual(width + 1);
          if (nextBefore !== null) expect(await page.locator(sel).nth(idx + 1).evaluate((e) => (e as HTMLElement).offsetLeft)).toBe(nextBefore);
          await page.mouse.move(2, 2);
          await page.waitForSelector(`[data-row-id="${rowId}"] .fui-card.expanded`, { state: 'detached', timeout: 3000 });
        }
      }
      noConsoleErrors();
    });
  }

  it('Top 10 cards: a TOP 10 glyph on the poster and "#N today" in the expanded panel', async () => {
    const page = await open();
    await homeReady(page);
    expect(await page.locator('.fui-row-top10 .fui-top10-glyph').allTextContents()).toEqual(['TOP 10', 'TOP 10', 'TOP 10']);
    const c = page.locator('.fui-row-top10 .fui-card').nth(1);
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector('.fui-row-top10 .fui-card.expanded');
    expect(await c.locator('.fui-rankcap').textContent()).toBe('#2 today');
    expect(await c.locator('.fui-art').getAttribute('aria-label')).toContain('number 2 today');
  });

  it('"Explore all" opens a full grid of the row from the cached Home, and Back returns', async () => {
    const page = await open();
    await homeReady(page);
    const link = page.locator('[data-row-id="toppicks"] .fui-row-explore');
    expect(await link.getAttribute('aria-label')).toBe('Explore all: Top Picks for Sam');
    expect(await page.locator('[data-row-id="comingsoon"] .fui-row-explore').count()).toBe(0); // only 2 cards
    await link.evaluate((e) => (e as HTMLElement).focus());
    await page.waitForFunction(() => getComputedStyle(document.querySelector('[data-row-id="toppicks"] .fui-row-explore')!).opacity === '1'); // keyboard focus reveals it
    await link.click();
    await page.waitForSelector('.fui-page-row .fui-grid');
    expect(await page.locator('.fui-page-row .fui-card').count()).toBe(6);
    expect(await page.locator('.fui-page-row h1').textContent()).toBe('Top Picks for Sam');
    expect(await page.evaluate(() => location.hash)).toContain('fui=row');
    expect(mock.callsTo('GET', 'Home').length).toBe(1); // client-side only
    await page.click('.fui-row-back');
    await page.waitForSelector('.fui-hero');
    expect(await page.locator('.fui-tab.active').textContent()).toBe('Home');
    noConsoleErrors();
  });

  it('an unknown row gives a friendly page with Try again and classic view', async () => {
    const page = await open({ hash: '/home?fui=row&q=nope' });
    await page.waitForSelector('.fui-error');
    expect(await page.locator('.fui-error h2').textContent()).toContain('find that row');
    expect(await page.locator('.fui-error button').allTextContents()).toEqual(['Try again', 'Use classic view']);
    await page.click('.fui-error .fui-btn-primary');
    await page.waitForSelector('.fui-hero');
  });

  it('40 rows stay cheap: below-the-fold rows are placeholders until they approach the viewport, without layout shift', async () => {
    const page = await open({ setup: (m) => (m.extraRows = 40) });
    await page.waitForSelector(`${root} .fui-hero`);
    await page.waitForSelector(`${root} .fui-row`);
    await page.waitForTimeout(300);
    const initial = await page.evaluate(() => ({ sk: document.querySelectorAll('.fui-row-sk').length, cards: document.querySelectorAll('.fui-card').length, rows: document.querySelectorAll('.fui-row').length }));
    expect(initial.rows).toBe(45);
    expect(initial.sk).toBeGreaterThan(25);
    expect(initial.cards).toBeLessThan(60);
    expect(await page.locator('.fui-row-title').count()).toBe(45); // titles exist even for placeholders
    expect(await page.locator('.fui-row-sk').first().getAttribute('aria-busy')).toBe('true');
    await page.evaluate(() => {
      (window as any).__cls = 0;
      new PerformanceObserver((l) => {
        for (const e of l.getEntries() as any[]) if (!e.hadRecentInput) (window as any).__cls += e.value;
      }).observe({ type: 'layout-shift', buffered: true });
    });
    for (let i = 1; i <= 14; i++) {
      await page.evaluate((k) => {
        const r = document.getElementById('fullui-root')!;
        r.scrollTop = (r.scrollHeight / 14) * k;
      }, i);
      await page.waitForTimeout(120);
    }
    await scrollRoot(page, 'end');
    await page.waitForFunction(() => document.querySelectorAll('.fui-row-sk').length === 0, undefined, { timeout: 8000 });
    expect(await page.locator('.fui-card').count()).toBeGreaterThan(200);
    expect(await page.evaluate(() => (window as any).__cls as number)).toBeLessThan(0.1);
    noConsoleErrors();
  });

  for (const dpr of [1, 2]) {
    it(`image URLs are size-aware at devicePixelRatio ${dpr}; hero has fetchpriority and a preconnect`, async () => {
      const ctxOpts = { dpr };
      const page = await open(ctxOpts);
      await homeReady(page);
      await page.waitForTimeout(500);
      const widths = (kind: string) =>
        [...new Set(mock.images.filter((u) => u.startsWith('/Items/') && u.includes(`/Images/${kind}?`)).map((u) => Number(/maxWidth=(\d+)/.exec(u)![1])))].sort((a, b) => a - b);
      const allowed = [240, 320, 480, 720, 960, 1280, 1920];
      for (const w of [...widths('Backdrop'), ...widths('Primary')]) expect(allowed).toContain(w);
      // 16em cards (256px) x 1.35 expansion x dpr
      expect(widths('Backdrop')).toContain(dpr === 1 ? 480 : 720);
      expect(widths('Backdrop')).toContain(dpr === 1 ? 1280 : 1920); // hero
      expect(widths('Primary')).toContain(dpr === 1 ? 240 : 480); // Top 10 posters (9em)
      expect(await page.locator('.fui-hero-img').getAttribute('fetchpriority')).toBe('high');
      expect(await page.locator(`link[rel="preconnect"][href="${mock.origin}"]`).count()).toBe(1);
    });
  }

  it('images fade in when loaded', async () => {
    const page = await open();
    await homeReady(page);
    await page.waitForSelector('.fui-hero-img.loaded');
    await page.waitForSelector('.fui-art img.loaded');
    await page.waitForFunction(() => getComputedStyle(document.querySelector('.fui-art img.loaded')!).opacity === '1');
  });
});

describe('player assist on the native video page', () => {
  const ticks = (s: number) => s * 10_000_000;
  const INTRO = { Type: 'Intro', StartTicks: ticks(10), EndTicks: ticks(70) };
  const OUTRO = { Type: 'Outro', StartTicks: ticks(1300), EndTicks: ticks(1400) };
  /** Rows of text for everything native on the page (our overlay and the FullUI root excluded). */
  const nativeSnap = (page: Page) =>
    page.evaluate(() => {
      const out: string[] = [];
      document.body.childNodes.forEach((n) => {
        const e = n as HTMLElement;
        if (e.id === 'fullui-assist' || e.id === 'fullui-root' || e.id === 'fullui-banner') return;
        out.push(e.outerHTML || e.textContent || '');
      });
      return out.join('\n');
    });
  const tickAt = async (page: Page, t: number) => {
    await page.waitForTimeout(300); // the assist throttles timeupdate to 4 per second
    await page.evaluate((x) => (window as any).__player.tick(x), t);
  };
  const overlay = (page: Page) => page.locator('#fullui-assist');
  const skip = (page: Page) => page.locator('#fullui-assist .fui-assist-skip');
  const next = (page: Page) => page.locator('#fullui-assist .fui-assist-next');
  const player = (page: Page) =>
    page.evaluate(() => {
      const P = (window as any).__player;
      const v = document.querySelector('video') as HTMLVideoElement | null;
      return { writes: P.writes as number[], nextClicks: P.nextClicks as number, t: P.t as number, paused: v ? v.paused : null };
    });
  const withSegments = (...s: Array<{ Type: string; StartTicks: number; EndTicks: number }>) => (m: Mock) => (m.segments = s);
  const NET = /Failed to load resource/;

  async function ready(page: Page) {
    await page.waitForSelector('#fullui-assist', { state: 'attached' });
    await page.waitForTimeout(150);
  }

  it('shows Skip Intro only inside the segment and a click sets currentTime to the segment end (the only native write)', async () => {
    const page = await open({ hash: '/video', setup: withSegments(INTRO) });
    await ready(page);
    const before = await nativeSnap(page);
    await tickAt(page, 1);
    expect(await skip(page).isVisible()).toBe(false);
    await tickAt(page, 20);
    await skip(page).waitFor({ state: 'visible' });
    expect(await skip(page).textContent()).toBe('Skip Intro');
    expect(await nativeSnap(page)).toBe(before); // no native node touched
    expect((await player(page)).writes).toEqual([]); // showing a button writes nothing
    await skip(page).click();
    const after = await player(page);
    expect(after.writes).toEqual([70]);
    expect(after.paused).toBe(false);
    await tickAt(page, 80);
    expect(await skip(page).isVisible()).toBe(false);
    // the request used the real Jellyfin route and the ApiClient token
    const call = mock.coreCalls.find((c) => c.path.startsWith('/MediaSegments/'))!;
    expect(call.path).toBe(`/MediaSegments/${G('s1')}`);
    expect(call.auth).toBe('MediaBrowser Token="tok"');
    expect(await nativeSnap(page)).toBe(before);
    noConsoleErrors();
  });

  it('labels Recap and Credits segments', async () => {
    const page = await open({ hash: '/video', setup: withSegments({ Type: 'Recap', StartTicks: 0, EndTicks: ticks(30) }, OUTRO) });
    await ready(page);
    await tickAt(page, 5);
    await skip(page).waitFor({ state: 'visible' });
    expect(await skip(page).textContent()).toBe('Skip Recap');
    await tickAt(page, 1320);
    await page.waitForFunction(() => document.querySelector('#fullui-assist .fui-assist-skip')!.textContent === 'Skip Credits');
  });

  it('next episode: countdown card, Play now clicks the native next control, Cancel keeps it away', async () => {
    const page = await open({ hash: '/video', setup: withSegments(OUTRO) });
    await ready(page);
    await tickAt(page, 1000);
    expect(await next(page).isVisible()).toBe(false);
    await tickAt(page, 1310);
    await next(page).waitFor({ state: 'visible' });
    expect(await page.locator('.fui-assist-next-text').textContent()).toBe('Next episode in 10 seconds');
    await tickAt(page, 1315);
    await page.waitForFunction(() => document.querySelector('.fui-assist-next-text')!.textContent === 'Next episode in 5 seconds');
    expect(await page.locator('.fui-assist-playnow').textContent()).toBe('Play now');
    expect(await page.locator('.fui-assist-cancel').textContent()).toBe('Cancel');
    await page.click('.fui-assist-playnow');
    expect((await player(page)).nextClicks).toBe(1);
    expect((await player(page)).writes).toEqual([]);
    // Cancel
    const page2 = await open({ hash: '/video', setup: withSegments(OUTRO) });
    await ready(page2);
    await tickAt(page2, 1310);
    await next(page2).waitFor({ state: 'visible' });
    await page2.click('.fui-assist-cancel');
    await tickAt(page2, 1330);
    await tickAt(page2, 1340);
    expect(await next(page2).isVisible()).toBe(false);
    expect((await player(page2)).nextClicks).toBe(0);
  });

  it('the countdown presses the native next control exactly once when it reaches zero', async () => {
    const page = await open({ hash: '/video', setup: withSegments(OUTRO) });
    await ready(page);
    await tickAt(page, 1380); // 20 s left => 10 s countdown... starts here
    await next(page).waitFor({ state: 'visible' });
    await tickAt(page, 1391);
    await tickAt(page, 1392);
    await tickAt(page, 1393);
    expect((await player(page)).nextClicks).toBe(1);
  });

  it('no native next control (last episode) means no countdown card at all', async () => {
    const page = await open({ hash: '/video', setup: withSegments(OUTRO) });
    await ready(page);
    await page.evaluate(() => (document.getElementById('nativeNext')!.className = 'btnNextTrack hide'));
    await tickAt(page, 1390);
    expect(await next(page).isVisible()).toBe(false);
    expect(await skip(page).isVisible()).toBe(true); // credits can still be skipped
    // and the native up-next dialog being on screen also keeps ours away
    await page.evaluate(() => {
      document.getElementById('nativeNext')!.className = 'btnNextTrack';
      const c = document.querySelector('.upNextContainer')!;
      c.className = 'upNextContainer';
      c.appendChild(document.createElement('div'));
    });
    await tickAt(page, 1392);
    expect(await next(page).isVisible()).toBe(false);
  });

  it('stays out of the way when the native player shows its own skip button', async () => {
    const page = await open({ hash: '/video', setup: withSegments(INTRO, { Type: 'Recap', StartTicks: ticks(100), EndTicks: ticks(160) }) });
    await ready(page);
    await page.evaluate(() => (window as any).__player.nativeSkip(true));
    await tickAt(page, 20);
    expect(await skip(page).isVisible()).toBe(false);
    // the native button times out (8 s) but ours must not pop up for that same segment
    await page.evaluate(() => (window as any).__player.nativeSkip(false));
    await tickAt(page, 30);
    expect(await skip(page).isVisible()).toBe(false);
    // a later segment the native player did not offer: ours may show
    await tickAt(page, 110);
    await skip(page).waitFor({ state: 'visible' });
    expect(await skip(page).textContent()).toBe('Skip Recap');
  });

  it('is invisible to the page: fixed, click-through except its own buttons, below the native controls', async () => {
    const page = await open({ hash: '/video', setup: withSegments(INTRO, OUTRO) });
    await ready(page);
    await tickAt(page, 20);
    await skip(page).waitFor({ state: 'visible' });
    await tickAt(page, 1310);
    await next(page).waitFor({ state: 'visible' });
    const css = await page.evaluate(() => {
      const o = document.getElementById('fullui-assist')!;
      const cs = getComputedStyle(o);
      const b = getComputedStyle(o.querySelector('.fui-assist-skip')!);
      // the countdown card's padding is not a button: events must reach whatever is underneath
      const r = o.querySelector('.fui-assist-next')!.getBoundingClientRect();
      const hit = document.elementFromPoint(r.left + 2, r.top + 2);
      return { position: cs.position, pe: cs.pointerEvents, btnPe: b.pointerEvents, z: Number(cs.zIndex), hitInside: !!hit && !!hit.closest('#fullui-assist'), role: o.getAttribute('role') };
    });
    expect(css.position).toBe('fixed');
    expect(css.pe).toBe('none');
    expect(css.btnPe).toBe('auto');
    expect(css.z).toBeLessThanOrEqual(1);
    expect(css.hitInside).toBe(false);
    expect(css.role).toBe('region');
  });

  it('with prefers-reduced-motion the overlay has no transitions or animations', async () => {
    const page = await open({ hash: '/video', setup: withSegments(INTRO), reducedMotion: true });
    await ready(page);
    await tickAt(page, 20);
    await skip(page).waitFor({ state: 'visible' });
    expect(await skip(page).evaluate((e) => getComputedStyle(e).transitionDuration)).toMatch(/^0s$/);
    await skip(page).click();
    expect((await player(page)).writes).toEqual([70]);
  });

  describe('fault injection: playback is never affected', () => {
    it('no <video> element: nothing is created, nothing throws', async () => {
      const page = await open({ hash: '/video?novideo=1' });
      await page.waitForTimeout(1500);
      expect(await overlay(page).count()).toBe(0);
      noConsoleErrors();
    });

    for (const mode of ['404', '500', 'garbage', 'malformed'] as const) {
      it(`segments ${mode}: no button, native untouched, no uncaught errors`, async () => {
        const page = await open({ hash: '/video', setup: (m) => (m.segmentsMode = mode), allowErrors: NET });
        await ready(page);
        const before = await nativeSnap(page);
        await tickAt(page, 20);
        await tickAt(page, 40);
        expect(await skip(page).isVisible()).toBe(false);
        expect(await next(page).isVisible()).toBe(false);
        expect(await nativeSnap(page)).toBe(before);
        expect((await player(page)).writes).toEqual([]);
        expect(mock.coreCalls.length).toBeGreaterThan(0); // it did ask
        noConsoleErrors(NET);
      });
    }

    it('a throwing handler switches the assist off after 3 errors with one warning; the native player is untouched', async () => {
      const warns: string[] = [];
      const page = await open({ hash: '/video', setup: withSegments(INTRO), allowErrors: /native getter exploded/ });
      page.on('console', (m) => {
        if (m.type() === 'warning') warns.push(m.text());
      });
      await ready(page);
      const before = await nativeSnap(page);
      await page.evaluate(() => ((window as any).__player.throwOnRead = true)); // only OUR reads explode
      for (let i = 0; i < 5; i++) await tickAt(page, 20);
      await page.waitForFunction(() => sessionStorage.getItem('fullui-assist-off') === '1');
      expect(await overlay(page).count()).toBe(0);
      expect(warns.filter((w) => w.includes('Player assist turned itself off'))).toHaveLength(1);
      await page.evaluate(() => ((window as any).__player.throwOnRead = false));
      const p = await player(page);
      expect(p.writes).toEqual([]);
      expect(p.paused).toBe(false);
      expect(await nativeSnap(page)).toBe(before);
      // stays off for the whole session, also after leaving and re-entering the player
      await page.evaluate(() => (location.hash = '#/home'));
      await page.waitForSelector('.fui-hero');
      await page.evaluate(() => (location.hash = '#/video'));
      await page.waitForTimeout(1200);
      expect(await overlay(page).count()).toBe(0);
      expect(errors.filter((e) => !/native getter exploded/.test(e))).toEqual([]);
    });

    it('leaving the player mid-playback removes the overlay; coming back re-attaches', async () => {
      const page = await open({ hash: '/video', setup: withSegments(INTRO) });
      await ready(page);
      await tickAt(page, 20);
      await skip(page).waitFor({ state: 'visible' });
      await page.evaluate(() => (location.hash = '#/home'));
      await page.waitForSelector('.fui-hero');
      expect(await overlay(page).count()).toBe(0);
      await page.evaluate(() => (location.hash = '#/video'));
      await ready(page);
      await tickAt(page, 25);
      await skip(page).waitFor({ state: 'visible' });
      noConsoleErrors();
    });

    it('is removed when the video ends and does not run on other native pages', async () => {
      const page = await open({ hash: '/video', setup: withSegments(INTRO) });
      await ready(page);
      await page.evaluate(() => (window as any).__player.end());
      expect(await overlay(page).count()).toBe(0);
      await page.evaluate(() => (location.hash = '#/dashboard'));
      await page.waitForTimeout(1800);
      expect(await overlay(page).count()).toBe(0);
      await page.evaluate(() => (location.hash = `#/details?id=${'0'.repeat(32)}`));
      await page.waitForTimeout(700);
      expect(await overlay(page).count()).toBe(0);
      noConsoleErrors();
    });

    it('the admin kill switch (Status.playerAssistEnabled:false) means no overlay and no segment requests', async () => {
      const page = await open({ hash: '/video', setup: (m) => ((m.statusExtra = { playerAssistEnabled: false }), withSegments(INTRO)(m)) });
      await page.waitForTimeout(1500);
      expect(await overlay(page).count()).toBe(0);
      expect(mock.coreCalls).toEqual([]);
    });

    it('signed out: no overlay and no requests', async () => {
      const page = await open({ hash: '/video', signedOut: true });
      await page.waitForTimeout(1200);
      expect(await overlay(page).count()).toBe(0);
      expect(mock.coreCalls).toEqual([]);
      expect(mock.calls).toEqual([]);
    });

    it('HLS (blob: URL): finds the playing item through the Sessions API for this device', async () => {
      const page = await open({ hash: '/home', setup: withSegments(INTRO) });
      await homeReady(page);
      await page.evaluate(() => {
        (window as any).__player.src = 'blob:http://example/1234';
        location.hash = '#/video';
      });
      await ready(page);
      await tickAt(page, 20);
      await skip(page).waitFor({ state: 'visible' });
      expect(mock.coreCalls.map((c) => c.path)).toEqual(['/Sessions?DeviceId=dev-1', `/MediaSegments/${G('s1')}`]);
    });

    it('a different episode in the same <video>: segments are fetched again, old ones are dropped', async () => {
      const page = await open({ hash: '/video', setup: withSegments(INTRO) });
      await ready(page);
      await tickAt(page, 20);
      await skip(page).waitFor({ state: 'visible' });
      mock.segments = [];
      await page.evaluate((id) => {
        const P = (window as any).__player;
        P.src = location.origin + '/Videos/' + id + '/stream.mp4';
        P.duration = 1500;
      }, G('s2'));
      await tickAt(page, 20); // new key: reset
      expect(await skip(page).isVisible()).toBe(false);
      await page.waitForTimeout(300);
      await tickAt(page, 21);
      expect(await skip(page).isVisible()).toBe(false); // the new episode has no segments
      expect(mock.coreCalls.filter((c) => c.path.startsWith('/MediaSegments/')).map((c) => c.path)).toEqual([`/MediaSegments/${G('s1')}`, `/MediaSegments/${G('s2')}`]);
    });
  });
});

describe('i18n in the browser', () => {
  it('follows the language jellyfin-web set on <html> (es)', async () => {
    const page = await open({ lang: 'es-MX' });
    await homeReady(page);
    expect(await page.locator('.fui-tab').allTextContents()).toEqual(['Inicio', 'Series', 'Películas', 'Mi MowFlix']);
    expect(await page.locator('.fui-hero-play').textContent()).toBe('Reproducir');
    expect(await page.locator('.fui-classic').textContent()).toBe('Usar la vista clásica');
    expect(await page.locator('.fui-hero-more').textContent()).toBe('Más información');
    expect(await page.locator('[data-row-id="comingsoon"] .fui-ribbon').first().textContent()).toMatch(/mar/i); // Intl month name, not "Mar 5, 2027"
    expect(await page.locator('[data-row-id="comingsoon"] .fui-ribbon').first().textContent()).not.toBe('Mar 5, 2027');
    await page.click('.fui-bell');
    await page.waitForSelector('.fui-note');
    expect(await page.locator('.fui-note-time').first().textContent()).not.toMatch(/ago/);
    expect(await page.getAttribute('.fui-hero-pause', 'aria-label')).toBe('Pausar el tráiler');
    noConsoleErrors();
  });

  it('failure states are translated too, with Try again', async () => {
    const page = await open({ lang: 'fr', hash: '/home?fui=search&q=zz', setup: (m) => (m.failSearch = true) });
    await page.waitForSelector('.fui-search-status .fui-retry');
    expect(await page.locator('.fui-search-status').textContent()).toContain('La recherche ne fonctionne pas pour le moment');
    expect(await page.locator('.fui-retry').textContent()).toBe('Réessayer');
    const page2 = await open({ lang: 'de', setup: (m) => (m.failHome = true) });
    await page2.waitForSelector('#fullui-banner:not(.fui-hidden) button');
    expect(await page2.locator('#fullui-banner').textContent()).toContain('Die neue Startseite konnte gerade nicht geladen werden');
    expect(await page2.locator('#fullui-banner button').textContent()).toBe('Erneut versuchen');
  });

  it('an unsupported language falls back to English', async () => {
    const page = await open({ lang: 'zz' });
    await homeReady(page);
    expect(await page.locator('.fui-tab').allTextContents()).toEqual(['Home', 'Shows', 'Movies', 'My MowFlix']);
  });
});

describe('TMDB attribution', () => {
  it('shows the sentence under Coming Soon cards; adds an inline (not hotlinked) logo when Status asks for it', async () => {
    const hosts = new Set<string>();
    const page = await open({ setup: (m) => (m.statusExtra = { tmdbAttribution: true }) });
    page.on('request', (r) => hosts.add(new URL(r.url()).host));
    await page.reload();
    await homeReady(page);
    const f = page.locator('[data-row-id="comingsoon"] .fui-tmdb');
    expect(await f.textContent()).toContain('This product uses the TMDB API but is not endorsed or certified by TMDB.');
    expect(await f.locator('svg.fui-tmdb-logo').count()).toBe(1);
    expect(await f.locator('img').count()).toBe(0);
    await page.waitForTimeout(300);
    expect([...hosts].filter((h) => /themoviedb|tmdb/.test(h) && h !== 'image.tmdb.org')).toEqual([]);
    const page2 = await open();
    await homeReady(page2);
    const f2 = page2.locator('[data-row-id="comingsoon"] .fui-tmdb');
    expect(await f2.textContent()).toContain('This product uses the TMDB API');
    expect(await f2.locator('svg').count()).toBe(0);
    expect(await page2.locator('.fui-tmdb').count()).toBe(1); // only under Coming Soon
  });

  it('is also shown on the Explore all grid of a Coming Soon row and on I Want This', async () => {
    const page = await open({ hash: '/home?fui=row&q=comingsoon' });
    await page.waitForSelector('.fui-page-row .fui-tmdb');
    expect(await page.locator('.fui-page-row .fui-card').count()).toBe(2);
    const page2 = await open({ hash: '/home?fui=myserver', setup: (m) => m.votes.set(501, 1) });
    await page2.waitForSelector('.fui-page-myserver .fui-tmdb');
  });
});

describe('search upgrades (client-only)', () => {
  const gotoSearch = async (page: Page) => {
    await homeReady(page);
    await page.click('.fui-nav-search');
    await page.waitForSelector('.fui-search-input');
  };

  it('remembers recent searches per user (max 8), lets you remove and clear them, never shows another user\'s', async () => {
    const page = await open();
    await gotoSearch(page);
    expect(await page.locator('.fui-qchip-q').count()).toBe(0);
    await page.fill('.fui-search-input', 'dune');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-search-results .fui-card');
    await page.fill('.fui-search-input', 'second');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-search-results .fui-card');
    await page.waitForTimeout(150);
    // back to the empty search: the terms are offered again, newest first
    await page.fill('.fui-search-input', '');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-qchip-q');
    expect(await page.locator('.fui-qchip-q').allTextContents()).toEqual(['second', 'dune']);
    expect(await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('fullui-recent')))).toEqual([`fullui-recent:${G('u1')}`]);
    expect(await page.evaluate(() => JSON.stringify(localStorage).includes('tok'))).toBe(false); // no token in storage
    await page.click('.fui-qchip-q');
    await page.waitForSelector('.fui-search-results .fui-card');
    // a different user on the same browser sees nothing
    await page.evaluate(() => ((window as any).__token = 'tok2'));
    await page.evaluate(() => document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true })));
    await page.waitForTimeout(300);
    // the page was rebuilt for the new user (and re-ran the URL's query as that user); u1's list must not leak
    await page.fill('.fui-search-input', '');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(300);
    expect(await page.locator('.fui-qchip-q').allTextContents()).toEqual(['second']); // only u2's own search
    expect(await page.evaluate(() => JSON.parse(localStorage.getItem('fullui-recent:' + (window as any).__users.tok2.id) || '[]'))).toEqual(['second']);
    // back to the first user: remove, then clear
    await page.evaluate(() => ((window as any).__token = 'tok'));
    await page.evaluate(() => document.dispatchEvent(new CustomEvent('viewshow', { bubbles: true })));
    await page.waitForTimeout(300);
    for (const q of ['hero', 'third', 'show one', 'show two', 'show three', 'continue', 'dune', 'movie']) {
      await page.fill('.fui-search-input', q);
      await page.keyboard.press('Enter');
      await page.waitForSelector('.fui-search-results .fui-card');
      await page.waitForTimeout(150);
    }
    await page.fill('.fui-search-input', '');
    await page.keyboard.press('Enter');
    await page.waitForSelector('.fui-qchip-q');
    expect(await page.locator('.fui-qchip-q').count()).toBe(8); // 9 searches in total, the oldest dropped
    await page.click('.fui-recent-clear');
    expect(await page.locator('.fui-qchip-q').count()).toBe(0);
    expect(await page.evaluate((k) => localStorage.getItem(k), `fullui-recent:${G('u1')}`)).toBeNull();
  });

  it('"Try:" genre chips come from the cached Home rows and filter locally (no Search request)', async () => {
    const page = await open();
    await gotoSearch(page);
    const chips = await page.locator('.fui-qchip-try').allTextContents();
    expect(chips).toEqual(['Drama', 'Sci-Fi']);
    await page.locator('.fui-qchip-try', { hasText: 'Sci-Fi' }).click();
    await page.waitForSelector('.fui-search-results .fui-card');
    expect(await page.locator('.fui-group-title').textContent()).toBe('Sci-Fi');
    expect(await page.locator('.fui-search-results .fui-card').count()).toBeGreaterThan(3);
    expect(mock.calls.filter((c) => c.path.startsWith('Search'))).toEqual([]);
    noConsoleErrors();
  });

  it('no results: friendly text and a "You might like" row from cached Home rows', async () => {
    const page = await open();
    await gotoSearch(page);
    await page.fill('.fui-search-input', 'zzzzz');
    await page.waitForSelector('.fui-search-results .fui-row');
    expect(await page.locator('.fui-search-status').textContent()).toContain('No results for "zzzzz".');
    expect(await page.locator('.fui-search-results .fui-row-title').textContent()).toBe('You might like');
    expect(await page.locator('.fui-search-results .fui-card').count()).toBeGreaterThan(2);
    // nothing was remembered for a search without results
    expect(await page.evaluate(() => Object.keys(localStorage).filter((k) => k.startsWith('fullui-recent')))).toEqual([]);
    noConsoleErrors();
  });

  it('renders server-driven groups when the response has them, and then mentions people in the placeholder', async () => {
    const page = await open({
      setup: (m) =>
        (m.searchGroups = [
          { type: 'people', label: 'Cast & crew', items: [{ ...(m.home().rows[1].items[0] as object) }] },
          { type: 'genres', items: [] },
          { type: 'titles', items: [{ ...(m.home().rows[1].items[1] as object) }] },
        ]),
    });
    await gotoSearch(page);
    expect(await page.getAttribute('.fui-search-input', 'placeholder')).not.toMatch(/person|actor|people/i);
    await page.fill('.fui-search-input', 'second');
    await page.waitForSelector('.fui-group-title');
    expect(await page.locator('.fui-group-title').allTextContents()).toEqual(['Cast & crew', 'Titles']);
    expect(await page.getAttribute('.fui-search-input', 'placeholder')).toMatch(/person/);
    noConsoleErrors();
  });

  it('without groups the response is rendered flat and the placeholder never promises people or genres', async () => {
    const page = await open();
    await gotoSearch(page);
    await page.fill('.fui-search-input', 'second');
    await page.waitForSelector('.fui-search-results .fui-card');
    expect(await page.locator('.fui-group-title').count()).toBe(0);
    expect(await page.getAttribute('.fui-search-input', 'placeholder')).toBe('Search by title or keyword');
  });
});

describe('only known endpoints are called (new server endpoints are feature-detected, not assumed)', () => {
  it('visits every page without calling anything outside the current server contract', async () => {
    const page = await open({ setup: (m) => (m.extraRows = 2) });
    await homeReady(page);
    await page.click('.fui-nav-search');
    await page.fill('.fui-search-input', 'second');
    await page.waitForSelector('.fui-search-results .fui-card');
    await page.click('.fui-tab[data-kind="myserver"]');
    await page.waitForSelector('.fui-page-myserver');
    await page.waitForTimeout(500);
    const known = /^(Status|Home|MyServer|Notifications|Search\?q=.*|Rate|MyList|Vote|Notifications\/Read|Item\/.*)$/;
    expect(mock.calls.map((c) => c.path).filter((p) => !known.test(p))).toEqual([]);
  });
});
