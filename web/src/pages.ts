import { api } from './api';
import { createAttribution } from './attribution';
import { createCard, disposeCards } from './card';
import { clear, h, icon, ICONS } from './dom';
import { t } from './i18n';
import { addRecent, currentUserId, loadRecent, removeRecent, saveRecent } from './recent';
import { createHero, createRow, mountRows, rowCards, stopHero } from './rows';
import { goTo, resetStore } from './store';
import type { HomeResponse, HomeRow, ItemCard, MyServerResponse, RouteKind, SearchGroup } from './types';
import { debounce, filterRowsByType, pickHeroItem } from './util';

export interface Page {
  el: HTMLElement;
  dispose(): void;
  focusFirst?(): void;
}

export function skeleton(): HTMLElement {
  return h(
    'div',
    { class: 'fui-skeleton', 'aria-busy': 'true', 'aria-label': t('common.loading') },
    h('div', { class: 'fui-sk-hero' }),
    h('div', { class: 'fui-sk-row' }),
    h('div', { class: 'fui-sk-row' }),
  );
}

export function errorState(message: string, onClassic: () => void, onRetry: () => void): HTMLElement {
  const retry = h('button', { type: 'button', class: 'fui-btn fui-btn-primary', text: t('common.tryAgain') });
  retry.addEventListener('click', onRetry);
  const classic = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary', text: t('common.useClassic') });
  classic.addEventListener('click', onClassic);
  return h('div', { class: 'fui-error', role: 'alert' }, h('h2', { text: message }), h('div', { class: 'fui-error-btns' }, retry, classic));
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
  page.appendChild(host);
  const handle = mountRows(host, rows);
  if (!host.firstChild) {
    const msg = kind === 'shows' ? t('page.emptyShows') : kind === 'movies' ? t('page.emptyMovies') : t('page.emptyTitles');
    host.appendChild(h('p', { class: 'fui-empty', text: msg }));
  }
  return {
    el: page,
    dispose: () => {
      stopHero(hero);
      handle.dispose();
    },
  };
}

export function myServerPage(serverName: string, data: MyServerResponse): Page {
  resetStore();
  const page = h('div', { class: 'fui-page fui-page-myserver' }, h('h1', { class: 'fui-page-title', text: t('nav.my', { server: serverName }) }));
  const host = h('div', { class: 'fui-rows' });
  const rows: HomeRow[] = [
    { id: 'ms-continue', title: t('page.continue'), type: 'continue', items: data.continueWatching || [] },
    { id: 'ms-list', title: t('page.myList'), type: 'mylist', items: data.myList || [] },
    { id: 'ms-wanted', title: t('page.wanted'), type: 'comingsoon', items: [], comingSoon: data.wanted || [] },
  ];
  page.appendChild(host);
  const handle = mountRows(host, rows);
  if (!host.firstChild) host.appendChild(h('p', { class: 'fui-empty', text: t('page.myEmpty') }));
  return { el: page, dispose: () => handle.dispose() };
}

/** "Explore all": every title of one row in a grid, from the cached Home payload (no extra request). */
export function rowPage(row: HomeRow | undefined, onHome: () => void, onClassic: () => void): Page {
  resetStore();
  if (!row) {
    return {
      el: h('div', { class: 'fui-page fui-page-row' }, errorState(t('page.rowNotFound'), onClassic, onHome)),
      dispose: () => {},
    };
  }
  const grid = h('div', { class: 'fui-grid', role: 'list' });
  for (const c of rowCards(row, 'landscape')) {
    c.setAttribute('role', 'listitem');
    grid.appendChild(c);
  }
  const back = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary fui-row-back', text: t('page.back') });
  back.addEventListener('click', onHome);
  const page = h(
    'div',
    { class: 'fui-page fui-page-row' },
    h('div', { class: 'fui-row-pagehead' }, h('h1', { class: 'fui-page-title', text: row.title }), back),
    grid,
    row.type === 'comingsoon' ? createAttribution() : null,
  );
  return { el: page, dispose: () => disposeCards(), focusFirst: () => back.focus({ preventScroll: true }) };
}

// ---- search ---------------------------------------------------------------------------------
/** Placeholder never promises more than the current mode can do. `people` is true once the server has sent person groups. */
export function searchPlaceholder(aiSearch: boolean, people = false): string {
  if (aiSearch) return t('search.placeholderAi');
  return people ? t('search.placeholderPeople') : t('search.placeholderKeyword');
}

export interface SearchContext {
  /** Rows of the cached Home payload (may be empty if Home was never loaded). */
  rows?: HomeRow[];
}

const SKIP_SUGGEST = new Set(['continue', 'mylist', 'comingsoon', 'again']);

/** Genres by popularity in the cached rows. */
export function topGenres(rows: HomeRow[], max = 6): string[] {
  const n = new Map<string, number>();
  for (const r of rows) {
    if (SKIP_SUGGEST.has(r.type)) continue;
    for (const i of r.items || []) for (const g of i.genres || []) if (g) n.set(g, (n.get(g) || 0) + 1);
  }
  return [...n.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).slice(0, max).map((e) => e[0]);
}

/** Unique titles from the cached rows, optionally limited to one genre. */
export function localTitles(rows: HomeRow[], genre?: string, max = 24): ItemCard[] {
  const seen = new Set<string>();
  const out: ItemCard[] = [];
  for (const r of rows) {
    if (SKIP_SUGGEST.has(r.type)) continue;
    for (const i of r.items || []) {
      if (seen.has(i.id) || i.myRating === -1) continue;
      if (genre && !(i.genres || []).includes(genre)) continue;
      seen.add(i.id);
      out.push(i);
      if (out.length >= max) return out;
    }
  }
  return out;
}

function groupLabel(g: SearchGroup): string {
  if (g.label && g.label.trim()) return g.label.trim();
  if (g.type === 'people') return t('search.groupPeople');
  if (g.type === 'genres') return t('search.groupGenres');
  return t('search.groupTitles');
}

function validGroups(res: { groups?: SearchGroup[] | null }): SearchGroup[] {
  if (!Array.isArray(res.groups)) return [];
  return res.groups.filter((g) => g && typeof g === 'object' && Array.isArray(g.items) && g.items.length > 0);
}

export function searchPage(initialQ: string, onQuery: (q: string) => void, aiSearch = false, ctx: SearchContext = {}): Page {
  resetStore();
  const rows = ctx.rows || [];
  const input = h('input', {
    type: 'search',
    class: 'fui-search-input',
    placeholder: searchPlaceholder(aiSearch),
    'aria-label': t('search.label'),
    autocomplete: 'off',
    value: initialQ,
  });
  const extras = h('div', { class: 'fui-search-extras' });
  const mode = h('div', { class: 'fui-search-mode', 'aria-live': 'polite' });
  const results = h('div', { class: 'fui-search-results' });
  const status = h('p', { class: 'fui-empty fui-search-status' });
  const page = h(
    'div',
    { class: 'fui-page fui-page-search' },
    h('div', { class: 'fui-search-bar' }, icon(ICONS.search), input),
    extras,
    mode,
    status,
    results,
  );
  const uid = currentUserId();
  let recents = loadRecent(uid);
  let seq = 0;
  let sawPeople = false;

  const grid = (items: ItemCard[]): HTMLElement => {
    const g = h('div', { class: 'fui-grid', role: 'list' });
    for (const it of items) {
      const c = createCard(it);
      c.setAttribute('role', 'listitem');
      g.appendChild(c);
    }
    return g;
  };
  const clearResults = () => {
    resetStore();
    clear(results);
    disposeCards(true);
  };
  const submit = (q: string) => {
    input.value = q;
    debounced.cancel();
    void run(q);
  };

  const renderExtras = (show: boolean) => {
    clear(extras);
    if (!show) return;
    if (recents.length) {
      const list = h('div', { class: 'fui-chiprow', role: 'list', 'aria-label': t('search.recent') });
      for (const q of recents) {
        const chip = h('span', { class: 'fui-qchip', role: 'listitem' });
        chip.append(
          h('button', { type: 'button', class: 'fui-qchip-q', text: q, on: { click: () => submit(q) } }),
          h('button', {
            type: 'button',
            class: 'fui-qchip-x',
            'aria-label': t('search.removeRecent', { q }),
            text: '×',
            on: {
              click: () => {
                recents = removeRecent(recents, q);
                saveRecent(recents, uid);
                renderExtras(true);
              },
            },
          }),
        );
        list.appendChild(chip);
      }
      extras.append(
        h(
          'div',
          { class: 'fui-search-block' },
          h('span', { class: 'fui-search-label', text: t('search.recent') }),
          list,
          h('button', {
            type: 'button',
            class: 'fui-link fui-recent-clear',
            text: t('search.clearRecent'),
            on: {
              click: () => {
                recents = [];
                saveRecent(recents, uid);
                renderExtras(true);
              },
            },
          }),
        ),
      );
    }
    const genres = topGenres(rows);
    if (genres.length) {
      const list = h('div', { class: 'fui-chiprow', role: 'list', 'aria-label': t('search.try') });
      for (const g of genres) {
        list.appendChild(
          h('button', {
            type: 'button',
            class: 'fui-qchip fui-qchip-try',
            role: 'listitem',
            text: g,
            on: { click: () => showGenre(g) },
          }),
        );
      }
      extras.append(h('div', { class: 'fui-search-block' }, h('span', { class: 'fui-search-label', text: t('search.try') }), list));
    }
  };

  /** A genre chip filters the titles already on Home: instant, offline, and honest about what it is. */
  const showGenre = (g: string) => {
    seq++;
    input.value = '';
    onQuery('');
    clearResults();
    mode.textContent = '';
    status.textContent = '';
    results.appendChild(h('h2', { class: 'fui-group-title', text: g }));
    results.appendChild(grid(localTitles(rows, g)));
  };

  const suggestRow = (): HTMLElement | null => {
    const items = localTitles(rows, undefined, 14);
    if (!items.length) return null;
    return createRow({ id: 'search-suggest', title: t('search.mightLike'), type: 'toppicks', items });
  };

  const renderResults = (res: { groups?: SearchGroup[] | null; items?: ItemCard[]; mode?: string }, q: string) => {
    clearResults();
    const groups = validGroups(res);
    const items = res.items || [];
    if (groups.length) {
      for (const g of groups) {
        if (g.type === 'people' && !sawPeople) {
          sawPeople = true;
          input.placeholder = searchPlaceholder(aiSearch, true);
        }
        results.appendChild(h('h2', { class: 'fui-group-title', text: groupLabel(g) }));
        results.appendChild(grid(g.items));
      }
    } else if (items.length) results.appendChild(grid(items));
    mode.textContent = res.mode === 'semantic' ? t('search.modeSemantic') : t('search.modeKeyword');
    if (groups.length || items.length) {
      status.textContent = '';
      recents = addRecent(recents, q);
      saveRecent(recents, uid);
    } else {
      status.textContent = t('search.noResults', { q });
      status.appendChild(h('span', { class: 'fui-search-hint', text: ' ' + t('search.noResultsHint') }));
      const more = suggestRow();
      if (more) results.appendChild(more);
    }
  };

  const run = async (raw: string) => {
    const my = ++seq;
    const q = raw.trim();
    onQuery(q);
    if (q.length < 2) {
      clearResults();
      mode.textContent = '';
      status.textContent = q ? t('search.keepTyping') : t('search.prompt');
      renderExtras(true);
      return;
    }
    renderExtras(false);
    status.textContent = t('search.searching');
    try {
      const res = await api.search(q);
      if (my !== seq) return;
      renderResults(res || { items: [] }, q);
    } catch {
      if (my !== seq) return;
      clearResults();
      mode.textContent = '';
      status.textContent = t('error.search');
      status.appendChild(
        h('button', { type: 'button', class: 'fui-btn fui-btn-secondary fui-retry', text: t('common.tryAgain'), on: { click: () => void run(q) } }),
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
