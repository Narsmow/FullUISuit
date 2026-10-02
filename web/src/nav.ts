import { api, client, serverBase, sessionKey } from './api';
import { h, icon, ICONS, setChildren } from './dom';
import { openDetails } from './card';
import type { NotificationDto, RouteKind } from './types';
import { badgeText, joinUrl, routeHash, safeAccent, timeAgo, unreadCount } from './util';

export interface Nav {
  el: HTMLElement;
  setActive(kind: RouteKind): void;
  setBranding(name: string, accent?: string): void;
  start(): void;
  stop(): void;
  /** Forget everything tied to the previous user (notifications, avatar, admin link). */
  reset(): void;
  closeMenus(): boolean;
}

/** Native jellyfin-web destinations that FullUI's own tabs do not cover (routes verified against release-10.11.z). */
export const LIBRARY_LINKS: Array<{ label: string; href: string }> = [
  { label: 'Movies library', href: '#/movies' },
  { label: 'TV Shows library', href: '#/tv' },
  { label: 'Music', href: '#/music' },
  { label: 'Live TV', href: '#/livetv' },
  { label: 'Favorites', href: '#/home?tab=1' },
];

const POLL_MS = 60000;

export function createNav(opts: { onClassic: () => void; onSignOut: () => void; onGo: (kind: RouteKind) => void }): Nav {
  let serverName = 'FullUI';
  let notes: NotificationDto[] = [];
  let notesFailed = false;
  let timer: ReturnType<typeof setInterval> | undefined;
  let notesSeq = 0; // a late reply for an old request (or old user) is ignored
  let notesAt = 0;
  let notesKey = '';
  let lastTrigger: HTMLElement = document.body;
  let lastKeyboard = false;

  const wordmark = h('a', { class: 'fui-wordmark', href: routeHash('home'), text: serverName });
  // Switch tabs inside FullUI without a hash change: jellyfin-web re-loads its (hidden) home on every one.
  const internal = (a: HTMLElement, kind: RouteKind) => {
    a.addEventListener('click', (e) => {
      if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
      e.preventDefault();
      opts.onGo(kind);
    });
    return a;
  };
  internal(wordmark, 'home');
  const tab = (kind: RouteKind, label: string) =>
    internal(h('a', { class: 'fui-tab', href: routeHash(kind), data: { kind }, text: label }), kind);
  const tabHome = tab('home', 'Home');
  const tabShows = tab('shows', 'Shows');
  const tabMovies = tab('movies', 'Movies');
  const tabMy = tab('myserver', 'My ' + serverName);
  const tabs = h('nav', { class: 'fui-tabs', 'aria-label': 'Main' }, tabHome, tabShows, tabMovies, tabMy);

  // Libraries: a visible way to the native library pages (not reachable from the recommendation tabs)
  const libsBtn = h('button', {
    type: 'button',
    class: 'fui-libs-btn',
    'aria-haspopup': 'true',
    'aria-expanded': 'false',
    text: 'Libraries',
  });
  const libsMenu = h(
    'div',
    { class: 'fui-panel fui-libs-menu fui-hidden', role: 'menu', 'aria-label': 'Libraries' },
    ...LIBRARY_LINKS.map((l) => h('a', { class: 'fui-libs-item', href: l.href, role: 'menuitem', text: l.label })),
  );
  const libsWrap = h('div', { class: 'fui-nav-item fui-libs' }, libsBtn, libsMenu);
  libsMenu.addEventListener('click', () => closeAll());

  const classicLink = h('button', { type: 'button', class: 'fui-classic', text: 'Use classic view', title: 'Switch back to the standard Jellyfin home' });
  classicLink.addEventListener('click', () => opts.onClassic());
  const searchBtn = h('a', { class: 'fui-ibtn fui-nav-search', href: routeHash('search'), 'aria-label': 'Search', title: 'Search' });
  searchBtn.appendChild(icon(ICONS.search));
  internal(searchBtn, 'search');

  // Notifications
  const bellBtn = h('button', {
    type: 'button',
    class: 'fui-ibtn fui-bell',
    'aria-label': 'Notifications',
    'aria-haspopup': 'true',
    'aria-expanded': 'false',
  });
  bellBtn.appendChild(icon(ICONS.bell));
  const count = h('span', { class: 'fui-bell-count fui-hidden', 'aria-hidden': 'true' });
  bellBtn.appendChild(count);
  const panel = h('div', { class: 'fui-panel fui-notes fui-hidden', 'aria-label': 'Notifications' });
  const bellWrap = h('div', { class: 'fui-nav-item' }, bellBtn, panel);

  // Profile
  const avatarBtn = h('button', {
    type: 'button',
    class: 'fui-avatar',
    'aria-label': 'Profile menu',
    'aria-haspopup': 'true',
    'aria-expanded': 'false',
  });
  const menu = h('div', { class: 'fui-panel fui-menu fui-hidden', role: 'menu' });
  const profileWrap = h('div', { class: 'fui-nav-item' }, avatarBtn, menu);

  const el = h(
    'header',
    { class: 'fui-nav' },
    wordmark,
    tabs,
    libsWrap,
    h('div', { class: 'fui-nav-right' }, classicLink, searchBtn, bellWrap, profileWrap),
  );

  function renderCount(): void {
    const n = unreadCount(notes);
    count.classList.toggle('fui-hidden', n === 0);
    count.textContent = n ? badgeText(n) : '';
    bellBtn.setAttribute('aria-label', n ? `Notifications, ${n} unread` : 'Notifications');
  }

  function renderNotes(): void {
    // keep keyboard focus across the rebuild
    const focusedIdx = Array.from(panel.querySelectorAll('button')).indexOf(document.activeElement as HTMLButtonElement);
    setChildren(panel);
    const head = h('div', { class: 'fui-panel-head' }, h('strong', { text: 'Notifications' }));
    if (unreadCount(notes) > 0) {
      const all = h('button', { type: 'button', class: 'fui-link', text: 'Mark all read' });
      all.addEventListener('click', () => void markRead(null));
      head.appendChild(all);
    }
    panel.appendChild(head);
    if (notesFailed) {
      panel.appendChild(h('p', { class: 'fui-panel-empty', text: "We couldn't load your notifications." }));
      panel.appendChild(h('button', { type: 'button', class: 'fui-link fui-notes-retry', text: 'Try again', on: { click: () => void refreshNotes() } }));
    } else if (!notes.length) panel.appendChild(h('p', { class: 'fui-panel-empty', text: 'No notifications yet.' }));
    const list = h('div', { role: 'menu', 'aria-label': 'Notification list' });
    for (const n of notes.slice(0, 20)) {
      const item = h(
        'button',
        { type: 'button', class: 'fui-note' + (n.read ? '' : ' unread'), role: 'menuitem' },
        h('span', { class: 'fui-note-text', text: n.text }),
        h('span', { class: 'fui-note-time', text: timeAgo(n.at) }),
      );
      item.addEventListener('click', () => {
        if (!n.read) void markRead([n.id]);
        closeAll();
        if (n.itemId) openDetails(n.itemId);
      });
      list.appendChild(item);
    }
    panel.appendChild(list);
    if (focusedIdx >= 0) {
      const btns = panel.querySelectorAll('button');
      (btns[Math.min(focusedIdx, btns.length - 1)] as HTMLElement | undefined)?.focus({ preventScroll: true });
    }
  }

  async function markRead(ids: string[] | null): Promise<void> {
    const prev = notes;
    notes = notes.map((n) => (ids === null || ids.includes(n.id) ? { ...n, read: true } : n));
    renderCount();
    renderNotes();
    try {
      await api.markRead(ids);
    } catch {
      notes = prev;
      renderCount();
      renderNotes();
    }
  }

  async function refreshNotes(): Promise<void> {
    const my = ++notesSeq;
    const key = sessionKey();
    if (!key) return;
    notesKey = key; // mark as fetched-for-this-user right away so a second sync() does not fetch again
    notesAt = Date.now();
    try {
      const res = await api.notifications();
      if (my !== notesSeq || key !== sessionKey()) return; // stale reply: newer request or different user
      notes = (res && res.items) || [];
      notesFailed = false;
    } catch {
      if (my !== notesSeq) return;
      notesAt = 0; // let the next start() / poll retry
      notesFailed = true; // keep old items; the panel offers a retry
      renderNotes();
      return;
    }
    renderCount();
    renderNotes();
  }

  function setOpen(which: 'notes' | 'menu' | 'libs' | null): void {
    const notesOpen = which === 'notes';
    const menuOpen = which === 'menu';
    const libsOpen = which === 'libs';
    panel.classList.toggle('fui-hidden', !notesOpen);
    menu.classList.toggle('fui-hidden', !menuOpen);
    libsMenu.classList.toggle('fui-hidden', !libsOpen);
    bellBtn.setAttribute('aria-expanded', String(notesOpen));
    avatarBtn.setAttribute('aria-expanded', String(menuOpen));
    libsBtn.setAttribute('aria-expanded', String(libsOpen));
    if (notesOpen) {
      renderNotes();
      void refreshNotes();
    }
  }
  function closeAll(): boolean {
    const was =
      !panel.classList.contains('fui-hidden') || !menu.classList.contains('fui-hidden') || !libsMenu.classList.contains('fui-hidden');
    setOpen(null);
    return was;
  }

  libsBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    lastTrigger = libsBtn;
    setOpen(libsMenu.classList.contains('fui-hidden') ? 'libs' : null);
    focusFirstItem(libsMenu);
  });
  bellBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    lastTrigger = bellBtn;
    setOpen(panel.classList.contains('fui-hidden') ? 'notes' : null);
  });
  avatarBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    lastTrigger = avatarBtn;
    setOpen(menu.classList.contains('fui-hidden') ? 'menu' : null);
    focusFirstItem(menu);
  });
  document.addEventListener('click', (e) => {
    if (!el.contains(e.target as Node)) closeAll();
  });
  // opening a menu with the keyboard / remote moves focus to its first item
  function focusFirstItem(m: HTMLElement): void {
    if (m.classList.contains('fui-hidden') || !lastKeyboard) return;
    const first = m.querySelector<HTMLElement>('[role="menuitem"]');
    if (first) first.focus();
  }
  el.addEventListener('keydown', () => (lastKeyboard = true), true);
  el.addEventListener('pointerdown', () => (lastKeyboard = false), true);
  el.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && closeAll()) {
      e.stopPropagation();
      lastTrigger.focus(); // back to whichever button opened the menu
      return;
    }
    // arrow-key navigation inside open menus (role=menu contract)
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End') {
      const m = (e.target as HTMLElement).closest<HTMLElement>('[role="menu"]');
      if (!m || m.closest('.fui-hidden')) return;
      const items = Array.from(m.querySelectorAll<HTMLElement>('[role="menuitem"]'));
      if (!items.length) return;
      const i = items.indexOf(e.target as HTMLElement);
      let n = i;
      if (e.key === 'ArrowDown') n = (i + 1) % items.length;
      else if (e.key === 'ArrowUp') n = i <= 0 ? items.length - 1 : i - 1;
      else if (e.key === 'Home') n = 0;
      else n = items.length - 1;
      items[n].focus();
      e.preventDefault();
      e.stopPropagation();
    }
  });

  // Profile menu contents
  function buildMenu(isAdmin: boolean): void {
    setChildren(menu);
    const link = (text: string, href: string) => h('a', { class: 'fui-menu-item', href, role: 'menuitem', text });
    menu.appendChild(link('Settings', '#/mypreferencesmenu'));
    const cl = h('button', { type: 'button', class: 'fui-menu-item', role: 'menuitem', text: 'Use classic view' });
    cl.addEventListener('click', () => opts.onClassic());
    menu.appendChild(cl);
    if (isAdmin) menu.appendChild(link('Dashboard', '#/dashboard'));
    const out = h('button', { type: 'button', class: 'fui-menu-item', role: 'menuitem', text: 'Sign out' });
    out.addEventListener('click', () => {
      closeAll();
      opts.onSignOut();
    });
    menu.appendChild(out);
  }
  buildMenu(false);

  let identityDone = false;
  function loadIdentity(): void {
    if (identityDone) return;
    const c = client();
    if (!c) return;
    identityDone = true;
    const key = sessionKey();
    let uid = '';
    try {
      uid = c.getCurrentUserId ? c.getCurrentUserId() : '';
    } catch {
      uid = '';
    }
    avatarBtn.textContent = '';
    const letter = h('span', { class: 'fui-avatar-letter', text: '?' });
    avatarBtn.appendChild(letter);
    if (uid) {
      const img = h('img', { src: joinUrl(serverBase(c), `Users/${encodeURIComponent(uid)}/Images/Primary?maxWidth=96`), alt: '' });
      img.addEventListener('error', () => img.remove());
      avatarBtn.appendChild(img);
    }
    if (c.getCurrentUser) {
      c.getCurrentUser().then(
        (u) => {
          if (key !== sessionKey()) return; // signed out / another user meanwhile
          if (u && u.Name) letter.textContent = u.Name.charAt(0).toUpperCase();
          buildMenu(!!(u && u.Policy && u.Policy.IsAdministrator));
        },
        () => {},
      );
    }
  }

  return {
    el,
    setActive(kind) {
      el.querySelectorAll<HTMLElement>('.fui-tab').forEach((t) => {
        const on = t.dataset.kind === kind;
        t.classList.toggle('active', on);
        if (on) t.setAttribute('aria-current', 'page');
        else t.removeAttribute('aria-current');
      });
      searchBtn.classList.toggle('active', kind === 'search');
      closeAll();
    },
    setBranding(name, accent) {
      serverName = name || serverName;
      wordmark.textContent = serverName;
      tabMy.textContent = 'My ' + serverName;
      document.documentElement.style.setProperty('--fullui-accent', safeAccent(accent));
    },
    start() {
      loadIdentity();
      // Fetch on first start / after a user change / when the data is old; not on every navigation.
      if (notesKey !== sessionKey() || Date.now() - notesAt > POLL_MS) void refreshNotes();
      if (!timer) {
        timer = setInterval(() => {
          if (!document.hidden) void refreshNotes(); // no polling while the tab is in the background
        }, POLL_MS);
      }
    },
    stop() {
      if (timer) clearInterval(timer);
      timer = undefined;
    },
    reset() {
      notesSeq++; // invalidate in-flight replies
      notes = [];
      notesFailed = false;
      notesAt = 0;
      notesKey = '';
      identityDone = false;
      avatarBtn.textContent = '';
      avatarBtn.appendChild(h('span', { class: 'fui-avatar-letter', text: '?' }));
      buildMenu(false);
      closeAll();
      renderCount();
      renderNotes();
    },
    closeMenus() {
      const was = closeAll();
      if (was) lastTrigger.focus(); // keyboard users land back on the button that opened the menu
      return was;
    },
  };
}
