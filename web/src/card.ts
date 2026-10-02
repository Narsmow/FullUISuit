import { serverBase } from './api';
import { h, hasFocusVisible, icon, ICONS, setChildren } from './dom';
import { t } from './i18n';
import { canonItem, canonSoon, itemKey, setRating, setVote, soonSubKey, subscribe, toggleMyList } from './store';
import { playTrailer, reducedMotion, stopTrailer } from './trailer';
import type { ComingSoonCard, ItemCard } from './types';
import { chooseOrigin, detailsHash, formatRelease, formatTimeLeft, imageUrl, metaParts, pickImageWidth, tmdbImage } from './util';

const HOVER_DELAY = 350;
const FOCUS_DELAY = 200;
const COLLAPSE_DELAY = 120;
const TRAILER_DELAY = 800;
const LONG_PRESS = 600;
/** Expanded cards are scaled up from their own position (transform only: neighbours never reflow). */
export const EXPAND_SCALE = 1.35;

/** Pixel size of 1em inside the overlay (card sizes are in em, so the 10-foot layout is bigger). */
export function unitPx(): number {
  try {
    const r = document.getElementById('fullui-root');
    const v = r ? parseFloat(getComputedStyle(r).fontSize) : 16;
    return v > 0 ? v : 16;
  } catch {
    return 16;
  }
}
export function devicePx(): number {
  return (typeof window !== 'undefined' && window.devicePixelRatio) || 1;
}
/** Server image width for something that is `em` wide on screen (and may grow by `grow` when expanded). */
export function imgWidth(em: number, grow = 1): number {
  return pickImageWidth(em * unitPx() * grow, devicePx());
}

/** Choose the transform-origin so the scaled card (and its info panel) stays inside its strip/page: edge cards grow inward. */
function placeExpansion(root: HTMLElement, info: HTMLElement | null): void {
  try {
    const box = root.closest('.fui-strip') || root.closest('.fui-root') || document.documentElement;
    const strip = root.closest<HTMLElement>('.fui-strip');
    if (strip) {
      // a card that is only partly scrolled into view is nudged fully into view first
      const sr0 = strip.getBoundingClientRect();
      const r0 = root.getBoundingClientRect();
      if (r0.left < sr0.left) strip.scrollLeft += r0.left - sr0.left - 8;
      else if (r0.right > sr0.right) strip.scrollLeft += r0.right - sr0.right + 8;
    }
    const br = box.getBoundingClientRect();
    const r = root.getBoundingClientRect();
    const extent = info ? Math.max(root.offsetWidth, info.offsetLeft + info.offsetWidth) : root.offsetWidth;
    // a Top 10 info panel is wider than its card: with origin 'right' (CSS) it is anchored to the right edge
    const flip = info && root.classList.contains('top10') ? Math.max(0, info.offsetWidth - root.offsetWidth) : 0;
    const right = box === document.documentElement ? window.innerWidth : br.right;
    root.dataset.origin = chooseOrigin(r.left, root.offsetWidth, extent, EXPAND_SCALE, br.left, right, flip);
  } catch {
    root.dataset.origin = 'center';
  }
}

const HEART =
  'M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z';

let quiet = false;
// Cards that may still have timers pending; page teardown disposes them (B-63).
const live = new Map<HTMLElement, () => void>();

/** Cancel hover/trailer timers of every card (call when a page is torn down). */
export function disposeCards(onlyDetached = false): void {
  live.forEach((dispose, el) => {
    if (!onlyDetached || !el.isConnected) dispose();
  });
  if (!onlyDetached) {
    live.clear();
    expanded = null;
  } else if (expanded && !expanded.el.isConnected) expanded = null;
}
let expanded: { el: HTMLElement; collapse: () => void } | null = null;

export function collapseExpanded(): boolean {
  if (!expanded) return false;
  const el = expanded.el;
  const art = el.querySelector<HTMLElement>('.fui-art') || el;
  if (el.contains(document.activeElement) && document.activeElement !== art) {
    quiet = true;
    art.focus({ preventScroll: true });
    quiet = false;
  }
  expanded.collapse();
  return true;
}

// A hidden tab must not keep a card trailer running.
document.addEventListener('visibilitychange', () => {
  if (document.hidden) collapseExpanded();
});

export function openDetails(id: string): void {
  location.hash = detailsHash(id);
}

// ---- Play -----------------------------------------------------------------------------------
// jellyfin-web has no "autoplay" URL parameter, and its playback manager is not reachable from a
// plugin. So Play opens the native details page and then presses that page's own Play/Resume button
// as soon as it appears. If it never appears (older/newer jellyfin-web), the user simply lands on the
// details page, which has the real Play button.
let playTimer: ReturnType<typeof setInterval> | undefined;
const PLAY_WAIT_MS = 6000;

function visibleBtn(sel: string): HTMLElement | null {
  const list = document.querySelectorAll<HTMLElement>(sel);
  for (let i = 0; i < list.length; i++) {
    const b = list[i];
    if (b.offsetParent !== null && !b.classList.contains('hide') && !b.hasAttribute('disabled')) return b;
  }
  return null;
}

export function playItem(id: string): void {
  clearInterval(playTimer);
  openDetails(id);
  const want = detailsHash(id);
  const t0 = Date.now();
  playTimer = setInterval(() => {
    if (location.hash !== want || Date.now() - t0 > PLAY_WAIT_MS) {
      clearInterval(playTimer);
      return;
    }
    const b = visibleBtn('.btnResume') || visibleBtn('.btnPlay');
    if (b) {
      clearInterval(playTimer);
      b.click();
    }
  }, 150);
}

function artFallback(art: HTMLElement, text: string): void {
  art.classList.add('noimg');
  if (!art.querySelector('.fui-art-title')) art.appendChild(h('span', { class: 'fui-art-title', text }));
}

/** Images fade in once decoded (the art box has a fixed aspect ratio, so nothing shifts). */
export function fadeIn(img: HTMLImageElement): void {
  const done = () => img.classList.add('loaded');
  img.addEventListener('load', done);
  if (img.complete && img.naturalWidth > 0) done();
}

function artImage(src: string, alt: string, art: HTMLElement): HTMLImageElement {
  const img = h('img', { src, alt: '', loading: 'lazy', decoding: 'async', draggable: false });
  img.addEventListener('error', () => {
    img.remove();
    artFallback(art, alt);
  });
  fadeIn(img);
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
  const play = h('button', { type: 'button', class: 'fui-ibtn fui-play', 'aria-label': t('card.play'), title: t('card.play') });
  play.appendChild(icon(ICONS.play));
  play.addEventListener('click', (e) => {
    e.stopPropagation();
    playItem(card.id);
  });
  const list = iconBtn(t('card.addToList'), ICONS.plus, 'fui-list', () => void toggleMyList(card), card.inMyList);
  const down = iconBtn(t('card.notForMe'), ICONS.down, 'fui-rate fui-rate-down', () => void setRating(card, -1), false);
  const up = iconBtn(t('card.like'), ICONS.up, 'fui-rate fui-rate-up', () => void setRating(card, 1), false);
  const love = iconBtn(t('card.love'), HEART, 'fui-rate fui-rate-love', () => void setRating(card, 2), false);
  const group = h('span', { class: 'fui-thumbs', role: 'group', 'aria-label': t('card.rate') }, down, up, love);
  bar.append(play, list, group);
  if (opts.withInfo !== false) {
    const more = iconBtn(t('card.moreInfo'), ICONS.info, 'fui-more', () => openDetails(card.id));
    more.classList.add('fui-more');
    bar.append(more);
  }
  const refresh = () => {
    list.setAttribute('aria-pressed', String(card.inMyList));
    list.setAttribute('aria-label', card.inMyList ? t('card.removeFromList') : t('card.addToList'));
    list.title = list.getAttribute('aria-label')!;
    setChildren(list, icon(card.inMyList ? ICONS.check : ICONS.plus));
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
  // The maturity rating is drawn as a boxed badge; the text content stays "2021PG-131h 52m".
  return h(
    'div',
    { class: 'fui-meta' },
    ...metaParts(card).map((p) => h('span', { class: card.rated && p === card.rated ? 'fui-rated' : '', text: p })),
  );
}

/** "97% Match" (green) and the one-line reason; null when the server sent neither (cold start). */
export function whyLine(card: ItemCard): HTMLElement | null {
  const m = card.matchPercent;
  const hasMatch = typeof m === 'number' && m >= 1 && m <= 100;
  const reason = typeof card.reason === 'string' ? card.reason.trim() : '';
  if (!hasMatch && !reason) return null;
  return h(
    'div',
    { class: 'fui-why' },
    hasMatch ? h('span', { class: 'fui-match', text: t('card.match', { n: Math.round(m as number) }) }) : null,
    reason ? h('span', { class: 'fui-reason', text: reason }) : null,
  );
}

/** Top 10 caption, e.g. "#3 today". */
export function rankCaption(card: ItemCard): HTMLElement | null {
  return card.rank ? h('div', { class: 'fui-rankcap', text: t('card.top10Today', { n: card.rank }) }) : null;
}

function buildInfo(card: ItemCard): HTMLElement {
  const base = serverBase();
  const title = h('div', { class: 'fui-ititle' });
  if (card.hasLogo) {
    const img = h('img', { class: 'fui-logo', src: imageUrl(base, card.id, 'Logo', 300, card.imageTag), alt: card.name, loading: 'lazy' });
    fadeIn(img);
    img.addEventListener('error', () => img.replaceWith(h('strong', { text: card.name })));
    title.appendChild(img);
  } else title.appendChild(h('strong', { text: card.name }));
  return h(
    'div',
    { class: 'fui-info' },
    title,
    buildActions(card),
    rankCaption(card),
    whyLine(card),
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
  // The card container has no role; the artwork is the single focusable button (roving tabindex is
  // managed per row in rows.ts). The expanded info panel's buttons are separate controls.
  const root = h('div', { class: 'fui-card' + (top10 ? ' top10' : ''), data: { id: card.id } });
  const art = h('div', { class: 'fui-art', tabindex: 0, role: 'button', 'aria-label': card.name, data: { id: card.id } });
  const stage = h('div', { class: 'fui-stage' });
  const src =
    top10 || !card.hasBackdrop
      ? imageUrl(base, card.id, 'Primary', imgWidth(top10 ? 9 : 16, EXPAND_SCALE), card.imageTag)
      : imageUrl(base, card.id, 'Backdrop', imgWidth(16, EXPAND_SCALE), card.imageTag);
  art.append(artImage(src, card.name, art), stage);
  if (top10) art.appendChild(h('span', { class: 'fui-top10-glyph', 'aria-hidden': 'true', text: t('card.top10Glyph') }));
  if (card.badges.length) art.appendChild(h('span', { class: 'fui-ribbon', text: card.badges[0] }));
  if (card.progress && card.progress > 0) {
    const left = formatTimeLeft(card.minutesLeft);
    const label = [card.seriesLabel, left].filter((x) => !!x).join(' \u00b7 ');
    if (label) art.appendChild(h('span', { class: 'fui-cwlabel', text: label }));
    art.appendChild(
      h('div', { class: 'fui-progress' }, h('i', { style: `width:${Math.min(100, Math.round(card.progress * 100))}%` })),
    );
  }
  if (top10 && card.rank) root.appendChild(h('span', { class: 'fui-rank', 'aria-hidden': 'true', text: String(card.rank) }));
  if (card.rank) art.setAttribute('aria-label', t('card.rankedName', { name: card.name, n: card.rank }));
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
    clearTimeout(collapseT);
    clearTimeout(trailerT);
    root.classList.remove('expanded');
    root.parentElement?.closest('.fui-row')?.classList.remove('has-expanded');
    art.classList.remove('playing');
    stopTrailer(root);
    if (expanded && expanded.el === root) expanded = null;
  };
  const disposeCard = () => {
    clearTimeout(hoverT);
    clearTimeout(collapseT);
    clearTimeout(trailerT);
    clearTimeout(longPressT);
    stopTrailer(root);
    if (expanded && expanded.el === root) expanded = null;
    live.delete(root);
  };
  live.set(root, disposeCard);
  const expand = () => {
    clearTimeout(collapseT);
    if (root.classList.contains('expanded')) return;
    if (expanded && expanded.el !== root) expanded.collapse();
    if (!info) {
      info = buildInfo(card);
      root.appendChild(info);
    }
    placeExpansion(root, info);
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
      if (!hasFocusVisible(root)) collapse();
    }, COLLAPSE_DELAY);
  });
  root.addEventListener('focusin', () => {
    clearTimeout(collapseT);
    if (quiet) return;
    if (hasFocusVisible(root)) {
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
  art.addEventListener('keydown', (e) => {
    if (e.target !== art) return;
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
  const root = h('div', { class: 'fui-card fui-soon', 'aria-label': card.title });
  const art = h('div', { class: 'fui-art' });
  const poster = tmdbImage(card.posterPath, 'w342');
  if (poster) art.appendChild(artImage(poster, card.title, art));
  else artFallback(art, card.title);
  art.appendChild(h('span', { class: 'fui-ribbon', text: formatRelease(card.releaseDate) }));
  const want = h('button', { type: 'button', class: 'fui-btn fui-want', text: t('soon.want'), 'aria-pressed': 'false' });
  const nope = h('button', { type: 'button', class: 'fui-btn fui-nope', text: t('soon.nope'), 'aria-pressed': 'false' });
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
