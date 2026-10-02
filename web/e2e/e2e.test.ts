import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { chromium, type Browser, type BrowserContext, type Page } from 'playwright-core';
import { existsSync, mkdirSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Mock, YT_STUB } from './mock-server';

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
}

async function open(o: Opts = {}): Promise<Page> {
  mock.reset();
  errors = [];
  ctx = await browser.newContext({
    viewport: { width: o.width ?? (o.mobile ? 390 : 1280), height: o.height ?? (o.mobile ? 844 : 800) },
    hasTouch: !!o.mobile,
    isMobile: !!o.mobile,
    reducedMotion: o.reducedMotion ? 'reduce' : 'no-preference',
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
  await page.goto(mock.origin + '/' + (o.hash ? '#' + o.hash.replace(/^#/, '') : ''));
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
    const c1 = card(page, 'a2');
    await c1.scrollIntoViewIfNeeded();
    await c1.hover();
    await page.waitForSelector('[data-row-id="toppicks"] .fui-card[data-id="a2"].expanded');
    const info = c1.locator('.fui-info');
    expect(await info.isVisible()).toBe(true);
    expect(await info.locator('.fui-meta').textContent()).toBe('2021PG-131h 52m');
    expect(await info.locator('.fui-synopsis').isVisible()).toBe(true);
    expect(await info.locator('.fui-play').isVisible()).toBe(true);
    expect(await info.locator('.fui-rate').count()).toBe(3);
    // trailer in the expanded card; hero trailer destroyed => exactly one player alive
    await page.waitForSelector('[data-id="a2"] .yt-stub', { timeout: 5000 });
    expect(await page.evaluate(() => (window as any).__yt.alive)).toBe(1);
    // hover another card: previous collapses, still exactly one player
    const c2 = card(page, 's1');
    await c2.hover();
    await page.waitForSelector('[data-row-id="toppicks"] .fui-card[data-id="s1"].expanded');
    expect(await page.locator('.fui-card.expanded').count()).toBe(1);
    await page.waitForSelector('[data-id="s1"] .yt-stub', { timeout: 5000 });
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
    const c = card(page, 'a2');
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector('.fui-card[data-id="a2"].expanded');
    const up = c.locator('.fui-rate-up');
    const love = c.locator('.fui-rate-love');
    await up.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(1);
    expect(mock.callsTo('POST', 'Rate')[0].body).toEqual({ itemId: 'a2', rating: 1 });
    expect(await up.getAttribute('aria-pressed')).toBe('true');
    await love.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(2);
    expect(mock.callsTo('POST', 'Rate')[1].body).toEqual({ itemId: 'a2', rating: 2 });
    expect(await up.getAttribute('aria-pressed')).toBe('false');
    expect(await love.getAttribute('aria-pressed')).toBe('true');
    await love.click();
    await expect.poll(() => mock.callsTo('POST', 'Rate').length).toBe(3);
    expect(mock.callsTo('POST', 'Rate')[2].body).toEqual({ itemId: 'a2', rating: 0 });
    expect(await love.getAttribute('aria-pressed')).toBe('false');
    await page.waitForTimeout(300);
    expect(mock.callsTo('POST', 'Rate').length).toBe(3);
    noConsoleErrors();
  });

  it('My List toggles add then remove', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, 'a3');
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector('.fui-card[data-id="a3"].expanded');
    const btn = c.locator('.fui-list');
    await btn.click();
    await expect.poll(() => mock.callsTo('POST', 'MyList').length).toBe(1);
    expect(mock.callsTo('POST', 'MyList')[0].body).toEqual({ itemId: 'a3', add: true });
    expect(await btn.getAttribute('aria-pressed')).toBe('true');
    await btn.click();
    await expect.poll(() => mock.callsTo('POST', 'MyList').length).toBe(2);
    expect(mock.callsTo('POST', 'MyList')[1].body).toEqual({ itemId: 'a3', add: false });
    expect(await btn.getAttribute('aria-pressed')).toBe('false');
    noConsoleErrors();
  });

  it('opens the native details page from a card click and More Info', async () => {
    const page = await open();
    await homeReady(page);
    await card(page, 'a3').click();
    await page.waitForFunction(() => location.hash.startsWith('#/details?id=a3'));
    expect(await page.locator('#nativeDetails').isVisible()).toBe(true);
    expect(await page.locator('#nativeHeader').isVisible()).toBe(true);
    await page.goBack();
    await homeReady(page);
    await page.locator('.fui-hero-more').click();
    await page.waitForFunction(() => location.hash.startsWith('#/details?id=a1'));
  });

  it('shows a rating failure message and rolls back (friendly, no raw error)', async () => {
    const page = await open();
    await homeReady(page);
    await page.route('**/FullUI/Rate', (r) => r.fulfill({ status: 500, body: '{}' }));
    const c = card(page, 'a2');
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector('.fui-card[data-id="a2"].expanded');
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
    expect(showIds.every((i) => i!.startsWith('s') || i === 'c1')).toBe(true);
    expect(await page.locator('.fui-tab.active').textContent()).toBe('Shows');
    expect(await page.locator('.fui-page-shows .fui-row-comingsoon .fui-soon-title').allTextContents()).toEqual(['Future Show']);
    await page.click('.fui-tab[data-kind="movies"]');
    await page.waitForSelector('.fui-page-movies .fui-row');
    const movieIds = await page.locator('.fui-page-movies .fui-row:not(.fui-row-comingsoon) .fui-card').evaluateAll((els) => els.map((e) => (e as HTMLElement).dataset.id));
    expect(movieIds.every((i) => i!.startsWith('a') || i === 'd1')).toBe(true);
    expect(mock.callsTo('GET', 'Home').length).toBe(1); // reuses the cached home payload
    noConsoleErrors();
  });

  it('My MowFlix shows continue watching, my list and wanted', async () => {
    const page = await open();
    await homeReady(page);
    const c = card(page, 'a3');
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
    expect(await page.locator('.fui-row-mylist .fui-card').getAttribute('data-id')).toBe('a3');
    await page.screenshot({ path: join(shots, 'desktop-myserver.png') });
    noConsoleErrors();
  });

  it('Search debounces, shows mode and results, handles failure with a retry', async () => {
    const page = await open();
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

  it('a failing Shows page shows a plain-English message with Try again and classic view', async () => {
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
    const c = card(page, 'a1');
    await c.focus();
    await page.keyboard.press('ArrowRight');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe('a2');
    await page.keyboard.press('ArrowRight');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe('s1');
    await page.waitForSelector('.fui-card[data-id="s1"].expanded');
    await page.keyboard.press('ArrowLeft');
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe('a2');
    // focus ring is visible on the art
    const outline = await page.evaluate(() => getComputedStyle(document.querySelector('.fui-card[data-id="a2"] .fui-art')!).outlineStyle);
    expect(outline).toBe('solid');
    // down goes to an action button of the expanded card, then Back collapses
    await page.waitForSelector('.fui-card[data-id="a2"].expanded');
    await page.keyboard.press('ArrowDown');
    expect(await page.evaluate(() => !!document.activeElement?.closest('.fui-info'))).toBe(true);
    await page.keyboard.press('Escape');
    await page.waitForSelector('.fui-card.expanded', { state: 'detached' });
    expect(await page.evaluate(() => (document.activeElement as HTMLElement).dataset.id)).toBe('a2');
    // Enter opens the details page
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => location.hash.startsWith('#/details?id=a2'));
    noConsoleErrors();
  });

  it('uses 10-foot sizing on large viewports', async () => {
    const page = await open({ width: 1920, height: 1080 });
    await homeReady(page);
    expect(await page.evaluate(() => getComputedStyle(document.getElementById('fullui-root')!).fontSize)).toBe('22px');
    const c = card(page, 'a2');
    await c.scrollIntoViewIfNeeded();
    await c.hover();
    await page.waitForSelector('.fui-card[data-id="a2"].expanded');
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

    const c = card(page, 'a2');
    await c.scrollIntoViewIfNeeded();
    await c.tap();
    await page.waitForSelector('.fui-card[data-id="a2"].expanded');
    expect(await c.locator('.fui-info').isVisible()).toBe(true);
    expect(await page.evaluate(() => location.hash)).not.toContain('details');
    await page.screenshot({ path: join(shots, 'mobile-card-expanded.png') });

    // long press on another card opens its info (details) page
    const target = card(page, 's2');
    await target.scrollIntoViewIfNeeded();
    await target.evaluate((el) => {
      el.dispatchEvent(new PointerEvent('pointerdown', { pointerType: 'touch', bubbles: true }));
    });
    await page.waitForFunction(() => location.hash.startsWith('#/details?id=s2'), undefined, { timeout: 3000 });
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
