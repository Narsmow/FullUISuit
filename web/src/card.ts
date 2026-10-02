import { serverBase } from './api';
import { h, icon, ICONS } from './dom';
import { canonItem, canonSoon, itemKey, setRating, setVote, soonSubKey, subscribe, toggleMyList } from './store';
import { playTrailer, reducedMotion, stopTrailer } from './trailer';
import type { ComingSoonCard, ItemCard } from './types';
import { detailsHash, formatRelease, imageUrl, metaParts, tmdbImage } from './util';

const HOVER_DELAY = 350;
const FOCUS_DELAY = 200;
const COLLAPSE_DELAY = 120;
const TRAILER_DELAY = 800;
const LONG_PRESS = 600;

const HEART =
  'M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z';

let quiet = false;
let expanded: { el: HTMLElement; collapse: () => void } | null = null;

export function collapseExpanded(): boolean {
  if (!expanded) return false;
  const el = expanded.el;
  if (el.contains(document.activeElement) && document.activeElement !== el) {
    quiet = true;
    el.focus({ preventScroll: true });
    quiet = false;
  }
  expanded.collapse();
  return true;
}

export function openDetails(id: string): void {
  location.hash = detailsHash(id);
}

function artFallback(art: HTMLElement, text: string): void {
  art.classList.add('noimg');
  if (!art.querySelector('.fui-art-title')) art.appendChild(h('span', { class: 'fui-art-title', text }));
}

function artImage(src: string, alt: string, art: HTMLElement): HTMLImageElement {
  const img = h('img', { src, alt: '', loading: 'lazy', decoding: 'async', draggable: false });
  img.addEventListener('error', () => {
    img.remove();
    artFallback(art, alt);
  });
  return img;
}

function iconBtn(label: string, path: string, cls: string, onClick: () => void, pressed?: boolean): HTMLButtonElement {
  const b = h('button', { type: 'button', class: 'fui-ibtn ' + cls, 'aria-label': label, title: label });
  b.appendChild(icon(path));
  if (pressed !== undefined) b.setAttribute('aria-pressed', String(pressed));
  b.addEventListener('click', (e) => {
    e.stopPropagation();
    onClick();
  });
  return b;
}

/** Thumbs group + My List toggle bound to the canonical card state. Reused by hero. */
export function buildActions(card: ItemCard, opts: { withInfo?: boolean } = {}): HTMLElement {
  const bar = h('div', { class: 'fui-actions' });
  const play = h('button', { type: 'button', class: 'fui-ibtn fui-play', 'aria-label': 'Play', title: 'Play' });
  play.appendChild(icon(ICONS.play));
  play.addEventListener('click', (e) => {
    e.stopPropagation();
    openDetails(card.id);
  });
  const list = iconBtn('Add to My List', ICONS.plus, 'fui-list', () => void toggleMyList(card), card.inMyList);
  const down = iconBtn('Not for me', ICONS.down, 'fui-rate fui-rate-down', () => void setRating(card, -1), false);
  const up = iconBtn('I like this', ICONS.up, 'fui-rate fui-rate-up', () => void setRating(card, 1), false);
  const love = iconBtn('Love this', HEART, 'fui-rate fui-rate-love', () => void setRating(card, 2), false);
  const group = h('span', { class: 'fui-thumbs', role: 'group', 'aria-label': 'Rate' }, down, up, love);
  bar.append(play, list, group);
  if (opts.withInfo !== false) {
    const more = iconBtn('More info', ICONS.info, 'fui-more', () => openDetails(card.id));
    more.classList.add('fui-more');
    bar.append(more);
  }
  const refresh = () => {
    list.setAttribute('aria-pressed', String(card.inMyList));
    list.setAttribute('aria-label', card.inMyList ? 'Remove from My List' : 'Add to My List');
    list.title = list.getAttribute('aria-label')!;
    list.replaceChildren(icon(card.inMyList ? ICONS.check : ICONS.plus));
    down.setAttribute('aria-pressed', String(card.myRating === -1));
    up.setAttribute('aria-pressed', String(card.myRating === 1));
    love.setAttribute('aria-pressed', String(card.myRating === 2));
    down.classList.toggle('on', card.myRating === -1);
    up.classList.toggle('on', card.myRating === 1);
    love.classList.toggle('on', card.myRating === 2);
  };
  refresh();
  subscribe(itemKey(card), refresh);
  return bar;
}

export function badgeChips(card: ItemCard, max = 3): HTMLElement {
  return h('div', { class: 'fui-chips' }, ...card.badges.slice(0, max).map((b) => h('span', { class: 'fui-chip', text: b })));
}

export function metaLine(card: ItemCard): HTMLElement {
  return h('div', { class: 'fui-meta' }, ...metaParts(card).map((p) => h('span', { text: p })));
}

function buildInfo(card: ItemCard): HTMLElement {
  const base = serverBase();
  const title = h('div', { class: 'fui-ititle' });
  if (card.hasLogo) {
    const img = h('img', { class: 'fui-logo', src: imageUrl(base, card.id, 'Logo', 300), alt: card.name, loading: 'lazy' });
    img.addEventListener('error', () => img.replaceWith(h('strong', { text: card.name })));
    title.appendChild(img);
  } else title.appendChild(h('strong', { text: card.name }));
  return h(
    'div',
    { class: 'fui-info' },
    title,
    buildActions(card),
    badgeChips(card),
    metaLine(card),
    card.overview ? h('p', { class: 'fui-synopsis', text: card.overview }) : null,
    card.genres.length ? h('div', { class: 'fui-genres', text: card.genres.slice(0, 3).join(' • ') }) : null,
  );
}

export function createCard(raw: ItemCard, variant: 'landscape' | 'top10' = 'landscape'): HTMLElement {
  const card = canonItem(raw);
  const base = serverBase();
  const top10 = variant === 'top10';
  const root = h('div', {
    class: 'fui-card' + (top10 ? ' top10' : ''),
    tabindex: 0,
    role: 'button',
    'aria-label': card.name,
    data: { id: card.id },
  });
  const art = h('div', { class: 'fui-art' });
  const stage = h('div', { class: 'fui-stage' });
  const src =
    top10 || !card.hasBackdrop ? imageUrl(base, card.id, 'Primary', top10 ? 300 : 480) : imageUrl(base, card.id, 'Backdrop', 480);
  art.append(artImage(src, card.name, art), stage);
  if (card.badges.length) art.appendChild(h('span', { class: 'fui-ribbon', text: card.badges[0] }));
  if (card.progress && card.progress > 0) {
    art.appendChild(
      h('div', { class: 'fui-progress' }, h('i', { style: `width:${Math.min(100, Math.round(card.progress * 100))}%` })),
    );
  }
  if (top10 && card.rank) root.appendChild(h('span', { class: 'fui-rank', 'aria-hidden': 'true', text: String(card.rank) }));
  root.appendChild(art);

  let info: HTMLElement | null = null;
  let hoverT: ReturnType<typeof setTimeout> | undefined;
  let collapseT: ReturnType<typeof setTimeout> | undefined;
  let trailerT: ReturnType<typeof setTimeout> | undefined;
  let lastPointer: string = 'mouse';
  let longPressT: ReturnType<typeof setTimeout> | undefined;
  let suppressClick = false;

  const collapse = () => {
    clearTimeout(hoverT);
    clearTimeout(trailerT);
    root.classList.remove('expanded');
    root.parentElement?.closest('.fui-row')?.classList.remove('has-expanded');
    art.classList.remove('playing');
    stopTrailer(root);
    if (expanded && expanded.el === root) expanded = null;
  };
  const expand = () => {
    clearTimeout(collapseT);
    if (root.classList.contains('expanded')) return;
    if (expanded && expanded.el !== root) expanded.collapse();
    if (!info) {
      info = buildInfo(card);
      root.appendChild(info);
    }
    root.classList.add('expanded');
    root.closest('.fui-row')?.classList.add('has-expanded');
    expanded = { el: root, collapse };
    if (card.trailerKey && !top10 && !reducedMotion()) {
      trailerT = setTimeout(() => {
        playTrailer(root, stage, card.trailerKey!, { onPlaying: () => art.classList.add('playing') });
      }, TRAILER_DELAY);
    }
  };

  root.addEventListener('pointerdown', (e) => {
    lastPointer = e.pointerType || 'mouse';
    if (lastPointer === 'touch') {
      clearTimeout(longPressT);
      longPressT = setTimeout(() => {
        suppressClick = true;
        openDetails(card.id);
      }, LONG_PRESS);
    }
  });
  for (const ev of ['pointerup', 'pointercancel', 'pointermove', 'pointerleave'] as const) {
    root.addEventListener(ev, () => clearTimeout(longPressT));
  }
  root.addEventListener('contextmenu', (e) => {
    if (lastPointer === 'touch') e.preventDefault();
  });
  root.addEventListener('mouseenter', () => {
    clearTimeout(collapseT);
    clearTimeout(hoverT);
    hoverT = setTimeout(expand, HOVER_DELAY);
  });
  root.addEventListener('mouseleave', () => {
    clearTimeout(hoverT);
    collapseT = setTimeout(() => {
      if (!root.querySelector(':focus-visible') && !root.matches(':focus-visible')) collapse();
    }, COLLAPSE_DELAY);
  });
  root.addEventListener('focusin', () => {
    clearTimeout(collapseT);
    if (quiet) return;
    if (root.matches(':focus-visible') || root.querySelector(':focus-visible')) {
      clearTimeout(hoverT);
      hoverT = setTimeout(expand, FOCUS_DELAY);
    }
  });
  root.addEventListener('focusout', (e) => {
    const next = e.relatedTarget as Node | null;
    if (next && root.contains(next)) return;
    clearTimeout(hoverT);
    collapseT = setTimeout(() => {
      if (!root.matches(':hover')) collapse();
    }, COLLAPSE_DELAY);
  });
  root.addEventListener('click', () => {
    if (suppressClick) {
      suppressClick = false;
      return;
    }
    if (lastPointer === 'touch') {
      if (!root.classList.contains('expanded')) expand();
      return;
    }
    openDetails(card.id);
  });
  root.addEventListener('keydown', (e) => {
    if (e.target !== root) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      openDetails(card.id);
    }
  });
  return root;
}

// ---- Coming Soon poster card -------------------------------------------------------------
export function createComingSoonCard(raw: ComingSoonCard): HTMLElement {
  const card = canonSoon(raw);
  const root = h('div', { class: 'fui-card fui-soon', role: 'group', 'aria-label': card.title });
  const art = h('div', { class: 'fui-art' });
  const poster = tmdbImage(card.posterPath, 'w342');
  if (poster) art.appendChild(artImage(poster, card.title, art));
  else artFallback(art, card.title);
  art.appendChild(h('span', { class: 'fui-ribbon', text: formatRelease(card.releaseDate) }));
  const want = h('button', { type: 'button', class: 'fui-btn fui-want', text: 'I want this', 'aria-pressed': 'false' });
  const nope = h('button', { type: 'button', class: 'fui-btn fui-nope', text: 'Not for me', 'aria-pressed': 'false' });
  want.addEventListener('click', () => void setVote(card, 1));
  nope.addEventListener('click', () => void setVote(card, -1));
  const refresh = () => {
    want.setAttribute('aria-pressed', String(card.myVote === 1));
    nope.setAttribute('aria-pressed', String(card.myVote === -1));
    want.classList.toggle('on', card.myVote === 1);
    nope.classList.toggle('on', card.myVote === -1);
    root.classList.toggle('voted-no', card.myVote === -1);
  };
  refresh();
  subscribe(soonSubKey(card), refresh);
  root.append(
    art,
    h('div', { class: 'fui-soon-body' }, h('div', { class: 'fui-soon-title', text: card.title }), h('div', { class: 'fui-soon-btns' }, want, nope)),
  );
  return root;
}
