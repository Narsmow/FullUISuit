import './style.css';
import { ApiError, api, client, hasSession, sessionKey } from './api';
import { collapseExpanded, disposeCards } from './card';
import { h, setChildren } from './dom';
import { refreshLocale, t } from './i18n';
import { createNav } from './nav';
import { setAttribution } from './attribution';
import { errorState, homePage, myServerPage, rowPage, searchPage, skeleton, type Page } from './pages';
import { createPlayerAssist } from './playerAssist';
import { installSpatial } from './spatial';
import { resetStore, setNavigator, setOnMutate, setToast } from './store';
import { setTrailersEnabled, stopTrailer } from './trailer';
import type { HomeResponse, PluginStatus, Route, RouteKind } from './types';
import { parseRoute, routeHash, rowHasContent } from './util';

const OWN_CLASS = 'fui-owns';
const HOME_TTL_MS = 60000;
const FAIL_BACKOFF_MS = 60000;
/** After a hash change to a native page, keep our overlay until that page's `viewshow` (or this long). */
const RELEASE_GRACE_MS = 800;
const STATUS_WAIT_MS = 1500;
const BOOT_POLL_MS = 300;
const BOOT_POLL_MAX_MS = 120000;

declare global {
  interface Window {
    __fullui?: boolean;
  }
}

let started = false;

function start(): void {
  if (started) return;
  started = true;
  const nav = createNav({
    onClassic: () => setClassic(true),
    onSignOut: () => signOut(),
    onGo: (kind) => go(kind),
  });
  const main = h('main', { class: 'fui-main', id: 'fullui-main' });
  const toast = h('div', { class: 'fui-toast fui-hidden', role: 'status', 'aria-live': 'polite' });
  const root = h('div', { id: 'fullui-root', class: 'fui-root fui-hidden' }, nav.el, main, toast);
  document.body.appendChild(root);

  let page: Page | null = null;
  let renderedKey: string | null = null;
  let token = 0;
  let owning = false;
  let current: Route = { kind: 'native', q: '' };
  // Fail-safe skip-intro / next-episode helper for the native player; only runs when Status says so.
  const assist = createPlayerAssist({ isEnabled: () => !!status && status.playerAssistEnabled !== false });
  let home: { data: HomeResponse; at: number; session: string } | null = null;
  let homeFailedAt = 0;
  let serverName = '';
  let status: PluginStatus | null = null;
  let statusP: Promise<void> = Promise.resolve();
  let lastSession = '';
  let signingOut = false;
  let releaseT: ReturnType<typeof setTimeout> | undefined;
  const scrollMemo = new Map<string, number>();
  let classic = readClassic();
  const banner = h('div', { id: 'fullui-banner', class: 'fui-banner fui-hidden', role: 'status' });
  document.body.appendChild(banner);
  let toastT: ReturnType<typeof setTimeout> | undefined;

  const showToast = (msg: string) => {
    toast.textContent = msg;
    toast.classList.remove('fui-hidden');
    clearTimeout(toastT);
    toastT = setTimeout(() => toast.classList.add('fui-hidden'), 4000);
  };
  setToast(showToast);
  // Ratings / My List / votes change what Home should show: drop the cached payload.
  setOnMutate(() => {
    home = null;
  });

  root.addEventListener('scroll', () => root.classList.toggle('scrolled', root.scrollTop > 8), { passive: true });

  const brand = (name: string, accent?: string) => {
    if (name) serverName = name;
    nav.setBranding(serverName || t('nav.defaultName'), accent);
  };

  function disposePage(): void {
    stopTrailer();
    disposeCards();
    if (page) {
      try {
        page.dispose();
      } catch {
        /* ignore */
      }
    }
    page = null;
  }

  // ---- identity: everything cached belongs to one user + token --------------------------------
  function loadStatus(): void {
    const key = sessionKey();
    if (!key) return;
    statusP = api.status().then(
      (s) => {
        if (!s || key !== sessionKey()) return;
        status = s;
        brand(s.serverName, s.accentColor);
        setTrailersEnabled(s.trailersEnabled !== false);
        setAttribution(s.tmdbAttribution);
        assist.sync();
      },
      () => {},
    );
  }

  function resetSession(key: string): void {
    refreshLocale();
    home = null;
    homeFailedAt = 0;
    status = null;
    scrollMemo.clear();
    setTrailersEnabled(true);
    setAttribution(null);
    assist.stop();
    nav.reset();
    resetStore();
    if (owning || renderedKey) {
      disposePage();
      setChildren(main);
      renderedKey = null;
      token++;
    }
    statusP = Promise.resolve();
    if (key) loadStatus(); // Status needs a session: never called on the login page
  }

  function checkSession(): void {
    const k = sessionKey();
    if (k === lastSession) return;
    lastSession = k;
    resetSession(k);
  }

  // ---- navigation helpers ----------------------------------------------------------------------
  /** Switch FullUI pages without a hash change (a hash change makes jellyfin-web reload its hidden home). */
  function go(kind: RouteKind, q = ''): void {
    const target = routeHash(kind, q);
    if (location.hash !== target) history.replaceState(history.state, '', target);
    sync();
  }

  function signOut(): void {
    signingOut = true;
    setTimeout(() => (signingOut = false), 8000);
    nav.stop();
    release();
    const fallback = () => {
      location.hash = '#/login';
    };
    try {
      const D = window.Dashboard;
      if (D && typeof D.logout === 'function') {
        const r = D.logout() as { then?: (a: () => void, b: () => void) => void } | undefined;
        if (r && typeof r.then === 'function') r.then(() => {}, fallback);
        return;
      }
    } catch {
      /* fall through to the plain client */
    }
    try {
      const c = client();
      const r = c && c.logout ? c.logout() : undefined;
      if (r && typeof (r as Promise<unknown>).then === 'function') (r as Promise<unknown>).then(fallback, fallback);
      else fallback();
    } catch {
      fallback();
    }
  }

  function setClassic(on: boolean): void {
    classic = on;
    writeClassic(on);
    if (!on) homeFailedAt = 0;
    renderedKey = null;
    sync(true);
  }

  function updateBanner(): void {
    const showing =
      !owning && hasSession() && current.kind !== 'native' && (classic || Date.now() - homeFailedAt < FAIL_BACKOFF_MS);
    banner.classList.toggle('fui-hidden', !showing);
    if (!showing) return;
    setChildren(
      banner,
      h('span', {
        text: classic ? t('banner.classic') : t('banner.failed'),
      }),
      h('button', {
        type: 'button',
        class: 'fui-banner-btn',
        text: classic ? t('banner.switchBack') : t('common.tryAgain'),
        on: { click: () => setClassic(false) },
      }),
    );
  }

  function release(): void {
    clearTimeout(releaseT);
    releaseT = undefined;
    if (!owning && !renderedKey) {
      nav.stop();
      return updateBanner();
    }
    if (renderedKey) scrollMemo.set(renderedKey, root.scrollTop);
    // coming back from a native page (e.g. after playback) should show fresh progress / My List
    if (owning && home) home = null;
    owning = false;
    document.documentElement.classList.remove(OWN_CLASS);
    root.classList.add('fui-hidden');
    nav.stop();
    disposePage();
    setChildren(main);
    renderedKey = null;
    token++;
    updateBanner();
  }

  async function getHome(force = false): Promise<HomeResponse> {
    const sk = sessionKey();
    if (!force && home && home.session === sk && Date.now() - home.at < HOME_TTL_MS) return home.data;
    const data = await api.home();
    if (!data || !Array.isArray(data.rows)) throw new ApiError(0, 'bad home payload');
    if (sk !== sessionKey()) throw new ApiError(401, 'session changed'); // never cache/show another user's rows
    home = { data, at: Date.now(), session: sk };
    brand(data.serverName, data.accentColor);
    return data;
  }

  function mount(p: Page, key: string): void {
    disposePage();
    page = p;
    setChildren(main, p.el);
    const y = scrollMemo.get(key);
    root.scrollTop = y || 0;
    p.focusFirst?.();
  }

  function settleStatus(): Promise<void> {
    return Promise.race([statusP, new Promise<void>((r) => setTimeout(r, STATUS_WAIT_MS))]);
  }

  async function render(route: Route, key: string, force = false): Promise<void> {
    const my = ++token;
    disposePage();
    setChildren(main, skeleton());
    root.scrollTop = 0;
    const stale = () => my !== token;
    try {
      await settleStatus(); // so the trailer switch / branding are known before anything plays
      if (stale()) return;
      if (route.kind === 'search') {
        mount(
          searchPage(
            route.q,
            (q) => {
              // keep the URL shareable without triggering a re-render or touching React Router's state
              const target = routeHash('search', q);
              if (location.hash !== target) history.replaceState(history.state, '', target);
            },
            !!(status && status.ollamaEnabled),
            { rows: home && home.session === sessionKey() ? home.data.rows : [] },
          ),
          key,
        );
        return;
      }
      if (route.kind === 'myserver') {
        const data = await api.myServer();
        if (stale()) return;
        mount(myServerPage(serverName || t('nav.serverFallback'), data), key);
        return;
      }
      const data = await getHome(force);
      if (stale()) return;
      if (route.kind === 'row') {
        const row = data.rows.find((x) => x.id === route.q);
        mount(rowPage(row, () => go('home'), () => setClassic(true)), key);
        return;
      }
      if (route.kind === 'home' && !data.rows.some(rowHasContent)) {
        // An empty home is indistinguishable from a failed build: use the classic home instead of a blank page.
        home = null;
        throw new ApiError(0, 'empty home');
      }
      mount(homePage(data, route.kind), key);
    } catch (e) {
      if (stale()) return;
      const code = e instanceof ApiError ? e.status : 0;
      if (route.kind === 'home' || code === 401) {
        // Never leave a blank screen: hand control back to the native home.
        if (code !== 401) homeFailedAt = Date.now();
        release();
        if (code !== 401) console.warn('[FullUI] Home failed; showing the native home.', e);
        return;
      }
      renderedKey = null;
      disposePage();
      setChildren(
        main,
        errorState(t('error.page'), () => setClassic(true), () => {
          renderedKey = null;
          sync(true);
        }),
      );
    }
  }

  function sync(force = false, viaViewshow = false): void {
    try {
      checkSession();
      assist.sync(); // never throws; does nothing off the native video route
      const route = parseRoute(location.hash);
      current = route;
      if (signingOut) {
        if (!hasSession()) signingOut = false;
        return release();
      }
      if (!hasSession() || classic) return release();
      if (route.kind === 'native') {
        // Keep the overlay up until the native page has actually appeared (no flash of the classic home).
        if (owning && !viaViewshow) {
          if (releaseT === undefined) releaseT = setTimeout(release, RELEASE_GRACE_MS);
          return;
        }
        return release();
      }
      clearTimeout(releaseT);
      releaseT = undefined;
      if (route.kind === 'home' && Date.now() - homeFailedAt < FAIL_BACKOFF_MS) return release();
      const key = route.kind === 'row' ? 'row:' + route.q : route.kind;
      owning = true;
      document.documentElement.classList.add(OWN_CLASS);
      root.classList.remove('fui-hidden');
      nav.setActive(route.kind);
      nav.start();
      if (renderedKey === key && !force) return; // idempotent across repeated viewshow events
      if (renderedKey) scrollMemo.set(renderedKey, root.scrollTop);
      renderedKey = key;
      updateBanner();
      void render(route, key, force);
    } catch (e) {
      console.error('[FullUI] sync failed', e);
      release();
    }
  }

  setNavigator((kind, q) => go(kind, q));
  installSpatial(root, {
    isActive: () => owning && !root.classList.contains('fui-hidden'),
    closeMenus: () => nav.closeMenus(),
    back: () => {
      if (current.kind !== 'home' && owning) {
        go('home'); // stays inside FullUI; never leaves the app
        return true;
      }
      return collapseExpanded();
    },
  });

  window.addEventListener('hashchange', () => sync());
  window.addEventListener('popstate', () => sync());
  // jellyfin-web fires viewshow on every page swap.
  document.addEventListener('viewshow', () => sync(false, true), true);
  sync();
}

function readClassic(): boolean {
  try {
    return localStorage.getItem('fullui-classic') === '1';
  } catch {
    return false;
  }
}
function writeClassic(on: boolean): void {
  try {
    if (on) localStorage.setItem('fullui-classic', '1');
    else localStorage.removeItem('fullui-classic');
  } catch {
    /* storage unavailable: classic mode just lasts for this page view */
  }
}

function boot(): void {
  // The bundle can be injected twice (cached + fresh index.html, or two plugins): mount only once.
  if (window.__fullui || document.getElementById('fullui-root')) return;
  window.__fullui = true;
  const tryStart = () => {
    if (window.ApiClient && document.body) {
      try {
        start();
      } catch (e) {
        console.error('[FullUI] failed to start', e);
      }
      return true;
    }
    return false;
  };
  if (tryStart()) return;
  const t0 = Date.now();
  let timer = 0;
  const done = () => {
    window.clearInterval(timer);
    window.removeEventListener('apiclientcreated', onCreated);
    document.removeEventListener('apiclientcreated', onCreated);
  };
  const onCreated = () => {
    if (tryStart()) done();
  };
  window.addEventListener('apiclientcreated', onCreated);
  document.addEventListener('apiclientcreated', onCreated);
  timer = window.setInterval(() => {
    if (tryStart() || Date.now() - t0 > BOOT_POLL_MAX_MS) done(); // give up quietly: jellyfin-web works as usual
  }, BOOT_POLL_MS);
}

boot();
