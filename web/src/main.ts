import './style.css';
import { ApiError, api, hasSession } from './api';
import { collapseExpanded } from './card';
import { h } from './dom';
import { createNav } from './nav';
import { errorState, homePage, myServerPage, searchPage, skeleton, type Page } from './pages';
import { installSpatial } from './spatial';
import { setToast } from './store';
import { stopTrailer } from './trailer';
import type { HomeResponse, Route } from './types';
import { parseRoute, routeHash } from './util';

const OWN_CLASS = 'fui-owns';
const HOME_TTL_MS = 60000;
const FAIL_BACKOFF_MS = 60000;

let started = false;

function start(): void {
  if (started) return;
  started = true;
  const nav = createNav({ onClassic: () => setClassic(true) });
  const main = h('main', { class: 'fui-main', id: 'fullui-main' });
  const toast = h('div', { class: 'fui-toast fui-hidden', role: 'status', 'aria-live': 'polite' });
  const root = h('div', { id: 'fullui-root', class: 'fui-root fui-hidden' }, nav.el, main, toast);
  document.body.appendChild(root);

  let page: Page | null = null;
  let renderedKey: string | null = null;
  let token = 0;
  let owning = false;
  let current: Route = { kind: 'native', q: '' };
  let home: { data: HomeResponse; at: number } | null = null;
  let homeFailedAt = 0;
  let serverName = '';
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

  root.addEventListener('scroll', () => root.classList.toggle('scrolled', root.scrollTop > 8), { passive: true });

  const brand = (name: string, accent?: string) => {
    if (name) serverName = name;
    nav.setBranding(serverName || 'FullUI', accent);
  };

  function disposePage(): void {
    stopTrailer();
    if (page) {
      try {
        page.dispose();
      } catch {
        /* ignore */
      }
    }
    page = null;
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
    banner.replaceChildren(
      h('span', {
        text: classic
          ? "You're using the classic view."
          : "The new home couldn't load right now, so you're seeing the classic view.",
      }),
      h('button', {
        type: 'button',
        class: 'fui-banner-btn',
        text: classic ? 'Switch to the new view' : 'Try again',
        on: { click: () => setClassic(false) },
      }),
    );
  }

  function release(): void {
    if (!owning && !renderedKey) return updateBanner();
    if (renderedKey) scrollMemo.set(renderedKey, root.scrollTop);
    owning = false;
    document.documentElement.classList.remove(OWN_CLASS);
    root.classList.add('fui-hidden');
    nav.stop();
    disposePage();
    main.replaceChildren();
    renderedKey = null;
    token++;
    updateBanner();
  }

  async function getHome(force = false): Promise<HomeResponse> {
    if (!force && home && Date.now() - home.at < HOME_TTL_MS) return home.data;
    const data = await api.home();
    if (!data || !Array.isArray(data.rows)) throw new ApiError(0, 'bad home payload');
    home = { data, at: Date.now() };
    brand(data.serverName, data.accentColor);
    return data;
  }

  function mount(p: Page, key: string): void {
    disposePage();
    page = p;
    main.replaceChildren(p.el);
    const y = scrollMemo.get(key);
    root.scrollTop = y || 0;
    p.focusFirst?.();
  }

  async function render(route: Route, key: string, force = false): Promise<void> {
    const my = ++token;
    disposePage();
    main.replaceChildren(skeleton());
    root.scrollTop = 0;
    const stale = () => my !== token;
    try {
      if (route.kind === 'search') {
        mount(
          searchPage(route.q, (q) => {
            // keep the URL shareable without triggering a re-render
            const target = routeHash('search', q);
            if (location.hash !== target) history.replaceState(null, '', target);
          }),
          key,
        );
        return;
      }
      if (route.kind === 'myserver') {
        const data = await api.myServer();
        if (stale()) return;
        mount(myServerPage(serverName || 'Server', data), key);
        return;
      }
      const data = await getHome(force);
      if (stale()) return;
      mount(homePage(data, route.kind), key);
    } catch (e) {
      if (stale()) return;
      const status = e instanceof ApiError ? e.status : 0;
      if (route.kind === 'home' || status === 401) {
        // Never leave a blank screen: hand control back to the native home.
        if (status !== 401) homeFailedAt = Date.now();
        release();
        if (status !== 401) console.warn('[FullUI] Home failed; showing the native home.', e);
        return;
      }
      renderedKey = null;
      disposePage();
      main.replaceChildren(
        errorState("We couldn't load this page. This is usually temporary, so please try again.", () => setClassic(true), () => {
          renderedKey = null;
          sync(true);
        }),
      );
    }
  }

  function sync(force = false): void {
    try {
      const route = parseRoute(location.hash);
      current = route;
      if (!hasSession() || route.kind === 'native' || classic) return release();
      if (route.kind === 'home' && Date.now() - homeFailedAt < FAIL_BACKOFF_MS) return release();
      const key = route.kind;
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

  // Branding as early as possible (the Home response refreshes it).
  api.status().then(
    (s) => s && brand(s.serverName, s.accentColor),
    () => {},
  );

  installSpatial(root, {
    isActive: () => owning && !root.classList.contains('fui-hidden'),
    closeMenus: () => nav.closeMenus(),
    back: () => {
      if (current.kind !== 'home' && owning) {
        history.back();
        return true;
      }
      return collapseExpanded();
    },
  });

  window.addEventListener('hashchange', () => sync());
  window.addEventListener('popstate', () => sync());
  // jellyfin-web fires viewshow on every page swap.
  document.addEventListener('viewshow', () => sync(), true);
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
  const timer = window.setInterval(() => {
    if (tryStart()) window.clearInterval(timer);
  }, 300);
}

boot();
