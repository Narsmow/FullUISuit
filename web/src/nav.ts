import { api, client, serverBase } from './api';
import { h, icon, ICONS } from './dom';
import { openDetails } from './card';
import type { NotificationDto, RouteKind } from './types';
import { badgeText, joinUrl, routeHash, timeAgo, unreadCount } from './util';

export interface Nav {
  el: HTMLElement;
  setActive(kind: RouteKind): void;
  setBranding(name: string, accent?: string): void;
  start(): void;
  stop(): void;
  closeMenus(): boolean;
}

const POLL_MS = 60000;

export function createNav(opts: { onClassic: () => void }): Nav {
  let serverName = 'FullUI';
  let notes: NotificationDto[] = [];
  let notesFailed = false;
  let timer: ReturnType<typeof setInterval> | undefined;

  const wordmark = h('a', { class: 'fui-wordmark', href: routeHash('home'), text: serverName });
  const tab = (kind: RouteKind, label: string) =>
    h('a', { class: 'fui-tab', href: routeHash(kind), data: { kind }, text: label });
  const tabHome = tab('home', 'Home');
  const tabShows = tab('shows', 'Shows');
  const tabMovies = tab('movies', 'Movies');
  const tabMy = tab('myserver', 'My ' + serverName);
  const tabs = h('nav', { class: 'fui-tabs', 'aria-label': 'Main' }, tabHome, tabShows, tabMovies, tabMy);

  const classicLink = h('button', { type: 'button', class: 'fui-classic', text: 'Use classic view', title: 'Switch back to the standard Jellyfin home' });
  classicLink.addEventListener('click', () => opts.onClassic());
  const searchBtn = h('a', { class: 'fui-ibtn fui-nav-search', href: routeHash('search'), 'aria-label': 'Search', title: 'Search' });
  searchBtn.appendChild(icon(ICONS.search));

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
  const panel = h('div', { class: 'fui-panel fui-notes fui-hidden', role: 'menu', 'aria-label': 'Notifications' });
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
    h('div', { class: 'fui-nav-right' }, classicLink, searchBtn, bellWrap, profileWrap),
  );

  function renderCount(): void {
    const n = unreadCount(notes);
    count.classList.toggle('fui-hidden', n === 0);
    count.textContent = n ? badgeText(n) : '';
    bellBtn.setAttribute('aria-label', n ? `Notifications, ${n} unread` : 'Notifications');
  }

  function renderNotes(): void {
    panel.replaceChildren();
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
      panel.appendChild(item);
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
    try {
      const res = await api.notifications();
      notes = res.items || [];
      notesFailed = false;
    } catch {
      notesFailed = true; // keep old items; the panel offers a retry
      renderNotes();
      return;
    }
    renderCount();
    renderNotes();
  }

  function setOpen(which: 'notes' | 'menu' | null): void {
    const notesOpen = which === 'notes';
    const menuOpen = which === 'menu';
    panel.classList.toggle('fui-hidden', !notesOpen);
    menu.classList.toggle('fui-hidden', !menuOpen);
    bellBtn.setAttribute('aria-expanded', String(notesOpen));
    avatarBtn.setAttribute('aria-expanded', String(menuOpen));
    if (notesOpen) {
      renderNotes();
      void refreshNotes();
    }
  }
  function closeAll(): boolean {
    const was = !panel.classList.contains('fui-hidden') || !menu.classList.contains('fui-hidden');
    setOpen(null);
    return was;
  }

  bellBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    setOpen(panel.classList.contains('fui-hidden') ? 'notes' : null);
  });
  avatarBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    setOpen(menu.classList.contains('fui-hidden') ? 'menu' : null);
  });
  document.addEventListener('click', (e) => {
    if (!el.contains(e.target as Node)) closeAll();
  });
  el.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && closeAll()) {
      e.stopPropagation();
      avatarBtn.focus();
    }
  });

  // Profile menu contents
  function buildMenu(isAdmin: boolean): void {
    menu.replaceChildren();
    const link = (text: string, href: string) => h('a', { class: 'fui-menu-item', href, role: 'menuitem', text });
    menu.appendChild(link('Settings', '#/mypreferencesmenu'));
    const cl = h('button', { type: 'button', class: 'fui-menu-item', role: 'menuitem', text: 'Use classic view' });
    cl.addEventListener('click', () => opts.onClassic());
    menu.appendChild(cl);
    if (isAdmin) menu.appendChild(link('Dashboard', '#/dashboard'));
    const out = h('button', { type: 'button', class: 'fui-menu-item', role: 'menuitem', text: 'Sign out' });
    out.addEventListener('click', () => {
      const c = client();
      if (c && c.logout) void c.logout();
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
    const uid = c.getCurrentUserId ? c.getCurrentUserId() : '';
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
      if (accent) document.documentElement.style.setProperty('--fullui-accent', accent);
    },
    start() {
      loadIdentity();
      void refreshNotes();
      if (!timer) timer = setInterval(() => void refreshNotes(), POLL_MS);
    },
    stop() {
      if (timer) clearInterval(timer);
      timer = undefined;
    },
    closeMenus: closeAll,
  };
}
