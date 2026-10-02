import { api } from './api';
import { createCard } from './card';
import { h, icon, ICONS } from './dom';
import { createHero, createRow, stopHero } from './rows';
import { resetStore } from './store';
import type { HomeResponse, HomeRow, MyServerResponse, RouteKind } from './types';
import { debounce, filterRowsByType, pickHeroItem, rowHasContent } from './util';

export interface Page {
  el: HTMLElement;
  dispose(): void;
  focusFirst?(): void;
}

export function skeleton(): HTMLElement {
  return h(
    'div',
    { class: 'fui-skeleton', 'aria-busy': 'true', 'aria-label': 'Loading' },
    h('div', { class: 'fui-sk-hero' }),
    h('div', { class: 'fui-sk-row' }),
    h('div', { class: 'fui-sk-row' }),
  );
}

export function errorState(message: string, onClassic: () => void, onRetry: () => void): HTMLElement {
  const retry = h('button', { type: 'button', class: 'fui-btn fui-btn-primary', text: 'Try again' });
  retry.addEventListener('click', onRetry);
  const classic = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary', text: 'Use classic view' });
  classic.addEventListener('click', onClassic);
  return h('div', { class: 'fui-error', role: 'alert' }, h('h2', { text: message }), h('div', { class: 'fui-error-btns' }, retry, classic));
}

function rowsInto(host: HTMLElement, rows: HomeRow[]): void {
  for (const r of rows) {
    if (!rowHasContent(r)) continue;
    const el = createRow(r);
    if (el) host.appendChild(el);
  }
}

export function homePage(data: HomeResponse, kind: RouteKind): Page {
  resetStore();
  let rows = data.rows || [];
  if (kind === 'shows') rows = filterRowsByType(rows, 'Series');
  else if (kind === 'movies') rows = filterRowsByType(rows, 'Movie');
  const page = h('div', { class: 'fui-page fui-page-' + kind });
  let hero: HTMLElement | null = null;
  const heroItem = pickHeroItem(rows);
  if (heroItem) {
    hero = createHero(heroItem);
    page.appendChild(hero);
  } else {
    page.appendChild(h('div', { class: 'fui-hero-spacer' }));
  }
  const host = h('div', { class: 'fui-rows' });
  rowsInto(host, rows);
  if (!host.firstChild) {
    const label = kind === 'shows' ? 'shows' : kind === 'movies' ? 'movies' : 'titles';
    host.appendChild(h('p', { class: 'fui-empty', text: `Nothing to show here yet. Check back soon for ${label}.` }));
  }
  page.appendChild(host);
  return { el: page, dispose: () => stopHero(hero) };
}

export function myServerPage(serverName: string, data: MyServerResponse): Page {
  resetStore();
  const page = h('div', { class: 'fui-page fui-page-myserver' }, h('h1', { class: 'fui-page-title', text: `My ${serverName}` }));
  const host = h('div', { class: 'fui-rows' });
  const rows: HomeRow[] = [
    { id: 'ms-continue', title: 'Continue Watching', type: 'continue', items: data.continueWatching || [] },
    { id: 'ms-list', title: 'My List', type: 'mylist', items: data.myList || [] },
    { id: 'ms-wanted', title: 'I Want This', type: 'comingsoon', items: [], comingSoon: data.wanted || [] },
  ];
  rowsInto(host, rows);
  if (!host.firstChild) {
    host.appendChild(
      h('p', { class: 'fui-empty', text: 'Your list is empty. Add titles with the + button, or tell us what you want to see next.' }),
    );
  }
  page.appendChild(host);
  return { el: page, dispose: () => {} };
}

export function searchPage(initialQ: string, onQuery: (q: string) => void): Page {
  resetStore();
  const input = h('input', {
    type: 'search',
    class: 'fui-search-input',
    placeholder: 'Search titles, genres, or describe what you feel like watching',
    'aria-label': 'Search',
    autocomplete: 'off',
    value: initialQ,
  });
  const mode = h('div', { class: 'fui-search-mode', 'aria-live': 'polite' });
  const results = h('div', { class: 'fui-grid', role: 'list' });
  const status = h('p', { class: 'fui-empty fui-search-status' });
  const page = h(
    'div',
    { class: 'fui-page fui-page-search' },
    h('div', { class: 'fui-search-bar' }, icon(ICONS.search), input),
    mode,
    status,
    results,
  );
  let seq = 0;
  const run = async (q: string) => {
    const my = ++seq;
    q = q.trim();
    onQuery(q);
    if (q.length < 2) {
      results.replaceChildren();
      mode.textContent = '';
      status.textContent = q ? 'Keep typing...' : 'Type to search your library.';
      return;
    }
    status.textContent = 'Searching...';
    try {
      const res = await api.search(q);
      if (my !== seq) return;
      resetStore();
      results.replaceChildren();
      const items = res.items || [];
      for (const it of items) {
        const c = createCard(it);
        c.setAttribute('role', 'listitem');
        results.appendChild(c);
      }
      mode.textContent = res.mode === 'semantic' ? 'Smart search (matches meaning, not just words)' : 'Keyword search';
      status.textContent = items.length ? '' : `No results for "${q}".`;
    } catch {
      if (my !== seq) return;
      results.replaceChildren();
      mode.textContent = '';
      status.textContent = "Search isn't working right now. Please try again in a moment.";
      status.appendChild(
        h('button', { type: 'button', class: 'fui-btn fui-btn-secondary fui-retry', text: 'Try again', on: { click: () => void run(q) } }),
      );
    }
  };
  const debounced = debounce((q: string) => void run(q), 300);
  input.addEventListener('input', () => debounced(input.value));
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      debounced.cancel();
      void run(input.value);
    }
  });
  void run(initialQ);
  return {
    el: page,
    dispose: () => {
      debounced.cancel();
      seq++;
    },
    focusFirst: () => input.focus({ preventScroll: true }),
  };
}
