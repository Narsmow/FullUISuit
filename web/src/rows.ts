import { serverBase } from './api';
import { createAttribution } from './attribution';
import { badgeChips, createCard, createComingSoonCard, fadeIn, metaLine, openDetails, playItem, whyLine } from './card';
import { h, icon, ICONS, setChildren } from './dom';
import { t } from './i18n';
import { canonItem, goTo } from './store';
import { isTrailerMuted, pauseTrailer, playTrailer, reducedMotion, replayTrailer, resumeTrailer, setTrailerMuted, stopTrailer } from './trailer';
import type { HomeRow, ItemCard } from './types';
import { imageUrl, pickImageWidth, routeHash } from './util';

const EXPLORE_MIN_CARDS = 4;
/** Rows beyond this many are built lazily when they get close to the viewport. */
export const EAGER_ROWS = 4;

function strip(children: HTMLElement[], label: string): HTMLElement {
  const scroller = h('div', { class: 'fui-strip', role: 'list', 'aria-label': label }, ...children);
  for (const c of children) c.setAttribute('role', 'listitem');
  // Roving tabindex: one tab stop per row (the focused card's artwork); arrow keys do the rest.
  const arts = children.map((c) => c.querySelector<HTMLElement>('.fui-art[role="button"]')).filter((a): a is HTMLElement => !!a);
  arts.forEach((a, i) => a.setAttribute('tabindex', i === 0 ? '0' : '-1'));
  scroller.addEventListener('focusin', (e) => {
    const target = e.target as HTMLElement;
    if (!arts.includes(target)) return;
    for (const a of arts) a.setAttribute('tabindex', a === target ? '0' : '-1');
  });
  const scrollBy = (dir: number) =>
    scroller.scrollBy({ left: dir * scroller.clientWidth * 0.85, behavior: reducedMotion() ? 'auto' : 'smooth' });
  const prev = h('button', { type: 'button', class: 'fui-arrow fui-prev', 'aria-label': t('common.scrollLeft'), tabindex: -1, text: '‹' });
  const next = h('button', { type: 'button', class: 'fui-arrow fui-next', 'aria-label': t('common.scrollRight'), tabindex: -1, text: '›' });
  prev.addEventListener('click', () => scrollBy(-1));
  next.addEventListener('click', () => scrollBy(1));
  return h('div', { class: 'fui-strip-wrap' }, prev, scroller, next);
}

export function rowCards(row: HomeRow, variant?: 'landscape'): HTMLElement[] {
  return row.type === 'comingsoon'
    ? (row.comingSoon || []).map((c) => createComingSoonCard(c))
    : (row.items || []).map((i) => createCard(i, row.type === 'top10' && !variant ? 'top10' : 'landscape'));
}

function rowCount(row: HomeRow): number {
  return row.type === 'comingsoon' ? (row.comingSoon || []).length : (row.items || []).length;
}

function head(row: HomeRow): HTMLElement {
  const title = h('h2', { class: 'fui-row-title', text: row.title });
  if (rowCount(row) < EXPLORE_MIN_CARDS) return h('div', { class: 'fui-row-head' }, title);
  const link = h(
    'a',
    {
      class: 'fui-row-explore',
      href: routeHash('row', row.id),
      'aria-label': t('row.exploreAllLabel', { title: row.title }),
    },
    h('span', { text: t('row.exploreAll') }),
    icon(ICONS.chevron),
  );
  link.addEventListener('click', (e) => {
    if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
    e.preventDefault();
    goTo('row', row.id); // client-side: no hash change, jellyfin-web's router is not involved
  });
  return h('div', { class: 'fui-row-head' }, title, link);
}

function body(row: HomeRow): HTMLElement[] {
  const out = [strip(rowCards(row), row.title)];
  if (row.type === 'comingsoon') out.push(createAttribution());
  return out;
}

function failedRow(row: HomeRow): HTMLElement {
  const box = h('section', { class: 'fui-row fui-row-failed' }, h('h2', { class: 'fui-row-title', text: (row && row.title) || '' }));
  const retry = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary', text: t('common.tryAgain') });
  retry.addEventListener('click', () => {
    const again = createRow(row);
    if (again) box.replaceWith(again);
  });
  box.append(h('p', { class: 'fui-empty', text: t('error.rowFailed') }), retry);
  return box;
}

/** Never throws: a broken row shows a friendly placeholder with a retry. Returns null for empty rows. */
export function createRow(row: HomeRow): HTMLElement | null {
  try {
    if (!rowCount(row)) return null;
    const cards = body(row);
    return h('section', { class: `fui-row fui-row-${row.type}`, data: { rowId: row.id, rowType: row.type } }, head(row), ...cards);
  } catch (e) {
    console.warn('[FullUI] row failed', row && row.id, e);
    return failedRow(row);
  }
}

/** Row with its title but a fixed-height placeholder instead of the strip (no layout shift when it fills in). */
function rowShell(row: HomeRow): HTMLElement {
  return h(
    'section',
    { class: `fui-row fui-row-${row.type} fui-row-lazy`, data: { rowId: row.id, rowType: row.type } },
    head(row),
    h('div', { class: 'fui-row-sk', role: 'status', 'aria-busy': 'true', 'aria-label': t('row.loading', { title: row.title }) }),
  );
}

export interface RowsHandle {
  dispose(): void;
}

/**
 * Adds all rows to `host`. The first few are built immediately; the rest get a placeholder and are
 * built when they come within 1.5 screens of the viewport, so 40 rows stay cheap.
 */
export function mountRows(host: HTMLElement, rows: HomeRow[], eager = EAGER_ROWS): RowsHandle {
  let io: IntersectionObserver | null = null;
  const pending = new Map<Element, HomeRow>();
  const build = (shell: HTMLElement, row: HomeRow) => {
    pending.delete(shell);
    io?.unobserve(shell);
    const real = createRow(row);
    if (real) shell.replaceWith(real);
    else shell.remove();
  };
  if (typeof IntersectionObserver === 'function') {
    io = new IntersectionObserver(
      (entries) => {
        for (const en of entries) {
          const row = pending.get(en.target);
          if (en.isIntersecting && row) build(en.target as HTMLElement, row);
        }
      },
      { root: document.getElementById('fullui-root'), rootMargin: '0px 0px 150% 0px' },
    );
  }
  let n = 0;
  for (const row of rows) {
    if (!rowCount(row)) continue;
    if (n++ < eager || !io) {
      const el = createRow(row);
      if (el) host.appendChild(el);
    } else {
      const shell = rowShell(row);
      pending.set(shell, row);
      host.appendChild(shell);
      io.observe(shell);
    }
  }
  return {
    dispose: () => {
      io?.disconnect();
      pending.clear();
    },
  };
}

// ---- hero ----------------------------------------------------------------------------------
function ensurePreconnect(origin: string): void {
  try {
    if (!origin || document.head.querySelector(`link[rel="preconnect"][href="${origin}"]`)) return;
    const l = document.createElement('link');
    l.rel = 'preconnect';
    l.href = origin;
    document.head.appendChild(l);
  } catch {
    /* a hint only */
  }
}

function ctlBtn(cls: string, label: string, path: string): HTMLButtonElement {
  const b = h('button', { type: 'button', class: cls, 'aria-label': label, title: label });
  b.appendChild(icon(path));
  return b;
}

export function createHero(raw: ItemCard): HTMLElement {
  const card = canonItem(raw);
  const base = serverBase();
  const owner = {};
  try {
    ensurePreconnect(new URL(base || '/', location.href).origin);
  } catch {
    /* ignore */
  }
  const heroW = pickImageWidth(Math.max(window.innerWidth || 1280, 320), window.devicePixelRatio || 1);
  const backdrop = card.hasBackdrop
    ? h('img', {
        class: 'fui-hero-img',
        src: imageUrl(base, card.id, 'Backdrop', heroW, card.imageTag),
        alt: '',
        fetchpriority: 'high',
        decoding: 'async',
      })
    : h('div', { class: 'fui-hero-img fui-hero-plain' });
  if (backdrop instanceof HTMLImageElement) {
    backdrop.addEventListener('error', () => backdrop.classList.add('fui-hidden'));
    fadeIn(backdrop);
  }
  const trailer = h('div', { class: 'fui-hero-trailer' });

  // --- controls: pause/play (WCAG 2.2.2), mute, replay ---
  const muteBtn = h('button', { type: 'button', class: 'fui-hero-mute fui-hidden', 'aria-label': t('hero.unmute'), title: t('hero.unmuteTitle') });
  muteBtn.appendChild(icon(ICONS.mute));
  muteBtn.addEventListener('click', () => {
    const muted = !isTrailerMuted(owner);
    setTrailerMuted(owner, muted);
    setChildren(muteBtn, icon(muted ? ICONS.mute : ICONS.unmute));
    const label = muted ? t('hero.unmute') : t('hero.mute');
    muteBtn.setAttribute('aria-label', label);
    muteBtn.title = label;
  });
  // aria-pressed="true" means "paused"; the label stays constant so it is not announced twice
  const pauseBtn = ctlBtn('fui-hero-pause fui-hidden', t('hero.pause'), ICONS.pause);
  pauseBtn.setAttribute('aria-pressed', 'false');
  const replayBtn = ctlBtn('fui-hero-replay fui-hidden', t('hero.replay'), ICONS.replay);
  const controls = h('div', { class: 'fui-hero-controls' }, replayBtn, pauseBtn, muteBtn);

  const title = h('div', { class: 'fui-hero-title' });
  if (card.hasLogo) {
    const logo = h('img', { class: 'fui-hero-logo', src: imageUrl(base, card.id, 'Logo', 600, card.imageTag), alt: card.name });
    logo.addEventListener('error', () => logo.replaceWith(h('h1', { text: card.name })));
    title.appendChild(logo);
  } else title.appendChild(h('h1', { text: card.name }));

  const play = h('button', { type: 'button', class: 'fui-btn fui-btn-primary fui-hero-play' }, icon(ICONS.play), t('common.play'));
  play.addEventListener('click', () => playItem(card.id));
  const more = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary fui-hero-more' }, icon(ICONS.info), t('common.moreInfo'));
  more.addEventListener('click', () => openDetails(card.id));

  const hero = h(
    'section',
    { class: 'fui-hero', 'aria-label': card.name, data: { id: card.id } },
    h('div', { class: 'fui-hero-media' }, backdrop, trailer),
    h('div', { class: 'fui-hero-shade' }),
    h(
      'div',
      { class: 'fui-hero-body' },
      title,
      badgeChips(card, 3),
      whyLine(card),
      metaLine(card),
      card.overview ? h('p', { class: 'fui-hero-synopsis', text: card.overview }) : null,
      h('div', { class: 'fui-hero-btns' }, play, more),
    ),
    controls,
  );

  if (!card.trailerKey || reducedMotion()) return hero;

  // --- lifecycle state ---
  let userPaused = false; // the viewer's explicit choice always wins
  let ended = false;
  let inView = true;
  let io: IntersectionObserver | null = null;
  let startT: ReturnType<typeof setTimeout> | undefined;
  let gone = false;

  const wantPlaying = () => !userPaused && inView && !document.hidden && !ended;
  const syncPlayback = () => {
    if (gone || !hero.isConnected) return;
    if (wantPlaying()) resumeTrailer(owner);
    else pauseTrailer(owner);
  };
  const showPlayingUi = () => {
    hero.classList.add('trailer-on');
    hero.classList.remove('trailer-ended');
    muteBtn.classList.remove('fui-hidden');
    pauseBtn.classList.remove('fui-hidden');
    replayBtn.classList.add('fui-hidden');
  };
  const hideTrailerUi = () => {
    hero.classList.remove('trailer-on');
    muteBtn.classList.add('fui-hidden');
    pauseBtn.classList.add('fui-hidden');
  };
  const setPaused = (p: boolean) => {
    userPaused = p;
    pauseBtn.setAttribute('aria-pressed', String(p));
    setChildren(pauseBtn, icon(p ? ICONS.play : ICONS.pause));
    syncPlayback();
  };

  const start = () => {
    playTrailer(owner, trailer, card.trailerKey!, {
      onPlaying: () => {
        ended = false;
        showPlayingUi();
        if (!wantPlaying()) pauseTrailer(owner); // it began while the tab was hidden or the hero scrolled away
      },
      onFail: () => {
        hideTrailerUi();
        replayBtn.classList.add('fui-hidden');
      },
      onStop: () => {
        // a card trailer took over: drop the hero's "playing" look (it resumes when the card stops)
        hideTrailerUi();
      },
      onEnded: () => {
        // fade back to the poster/backdrop; offer a replay
        ended = true;
        hero.classList.remove('trailer-on');
        hero.classList.add('trailer-ended');
        muteBtn.classList.add('fui-hidden');
        pauseBtn.classList.add('fui-hidden');
        replayBtn.classList.remove('fui-hidden');
      },
      shouldResume: () => !gone && !userPaused && !ended && inView && !document.hidden,
    });
    syncPlayback();
  };

  pauseBtn.addEventListener('click', () => setPaused(!userPaused));
  replayBtn.addEventListener('click', () => {
    ended = false;
    hero.classList.remove('trailer-ended');
    replayBtn.classList.add('fui-hidden');
    userPaused = false;
    pauseBtn.setAttribute('aria-pressed', 'false');
    setChildren(pauseBtn, icon(ICONS.pause));
    if (!replayTrailer(owner)) start();
    else syncPlayback();
  });

  const onVis = () => syncPlayback();
  document.addEventListener('visibilitychange', onVis);
  if (typeof IntersectionObserver === 'function') {
    io = new IntersectionObserver(
      (entries) => {
        for (const en of entries) inView = en.isIntersecting && en.intersectionRatio >= 0.3;
        syncPlayback();
      },
      { threshold: [0, 0.3, 0.6, 1] },
    );
    io.observe(hero);
  }

  startT = setTimeout(() => {
    if (!hero.isConnected) return;
    start();
  }, 800);
  (hero as HTMLElement & { _stop?: () => void })._stop = () => {
    gone = true;
    clearTimeout(startT);
    document.removeEventListener('visibilitychange', onVis);
    io?.disconnect();
    stopTrailer(owner);
  };
  return hero;
}

export function stopHero(hero: HTMLElement | null): void {
  (hero as (HTMLElement & { _stop?: () => void }) | null)?._stop?.();
}
