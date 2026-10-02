import { serverBase } from './api';
import { badgeChips, createCard, createComingSoonCard, metaLine, openDetails } from './card';
import { h, icon, ICONS } from './dom';
import { canonItem } from './store';
import { isTrailerMuted, playTrailer, reducedMotion, setTrailerMuted, stopTrailer } from './trailer';
import type { HomeRow, ItemCard } from './types';
import { imageUrl } from './util';

function strip(children: HTMLElement[], label: string): HTMLElement {
  const scroller = h('div', { class: 'fui-strip', role: 'list', 'aria-label': label }, ...children);
  for (const c of children) c.setAttribute('role', c.getAttribute('role') || 'listitem');
  const scrollBy = (dir: number) =>
    scroller.scrollBy({ left: dir * scroller.clientWidth * 0.85, behavior: reducedMotion() ? 'auto' : 'smooth' });
  const prev = h('button', { type: 'button', class: 'fui-arrow fui-prev', 'aria-label': 'Scroll left', tabindex: -1, text: '‹' });
  const next = h('button', { type: 'button', class: 'fui-arrow fui-next', 'aria-label': 'Scroll right', tabindex: -1, text: '›' });
  prev.addEventListener('click', () => scrollBy(-1));
  next.addEventListener('click', () => scrollBy(1));
  return h('div', { class: 'fui-strip-wrap' }, prev, scroller, next);
}

/** Never throws: a broken row shows a friendly placeholder with a retry. Returns null for empty rows. */
export function createRow(row: HomeRow): HTMLElement | null {
  try {
    const isSoon = row.type === 'comingsoon';
    const cards: HTMLElement[] = isSoon
      ? (row.comingSoon || []).map((c) => createComingSoonCard(c))
      : (row.items || []).map((i) => createCard(i, row.type === 'top10' ? 'top10' : 'landscape'));
    if (!cards.length) return null;
    return h(
      'section',
      { class: `fui-row fui-row-${row.type}`, data: { rowId: row.id, rowType: row.type } },
      h('h2', { class: 'fui-row-title', text: row.title }),
      strip(cards, row.title),
    );
  } catch (e) {
    console.warn('[FullUI] row failed', row && row.id, e);
    const box = h('section', { class: 'fui-row fui-row-failed' }, h('h2', { class: 'fui-row-title', text: (row && row.title) || '' }));
    const retry = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary', text: 'Try again' });
    retry.addEventListener('click', () => {
      const again = createRow(row);
      if (again) box.replaceWith(again);
    });
    box.append(h('p', { class: 'fui-empty', text: "This row couldn't be shown." }), retry);
    return box;
  }
}

export function createHero(raw: ItemCard): HTMLElement {
  const card = canonItem(raw);
  const base = serverBase();
  const owner = {};
  const backdrop = card.hasBackdrop
    ? h('img', { class: 'fui-hero-img', src: imageUrl(base, card.id, 'Backdrop', 1920), alt: '' })
    : h('div', { class: 'fui-hero-img fui-hero-plain' });
  if (backdrop instanceof HTMLImageElement) backdrop.addEventListener('error', () => backdrop.classList.add('fui-hidden'));
  const trailer = h('div', { class: 'fui-hero-trailer' });
  const muteBtn = h('button', { type: 'button', class: 'fui-hero-mute fui-hidden', 'aria-label': 'Unmute trailer', title: 'Unmute' });
  muteBtn.appendChild(icon(ICONS.mute));
  muteBtn.addEventListener('click', () => {
    const muted = !isTrailerMuted(owner);
    setTrailerMuted(owner, muted);
    muteBtn.replaceChildren(icon(muted ? ICONS.mute : ICONS.unmute));
    const label = muted ? 'Unmute trailer' : 'Mute trailer';
    muteBtn.setAttribute('aria-label', label);
    muteBtn.title = label;
  });
  const title = h('div', { class: 'fui-hero-title' });
  if (card.hasLogo) {
    const logo = h('img', { class: 'fui-hero-logo', src: imageUrl(base, card.id, 'Logo', 600), alt: card.name });
    logo.addEventListener('error', () => logo.replaceWith(h('h1', { text: card.name })));
    title.appendChild(logo);
  } else title.appendChild(h('h1', { text: card.name }));

  const play = h('button', { type: 'button', class: 'fui-btn fui-btn-primary fui-hero-play' }, icon(ICONS.play), 'Play');
  play.addEventListener('click', () => openDetails(card.id));
  const more = h('button', { type: 'button', class: 'fui-btn fui-btn-secondary fui-hero-more' }, icon(ICONS.info), 'More Info');
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
      metaLine(card),
      card.overview ? h('p', { class: 'fui-hero-synopsis', text: card.overview }) : null,
      h('div', { class: 'fui-hero-btns' }, play, more),
    ),
    muteBtn,
  );

  if (card.trailerKey && !reducedMotion()) {
    const t = setTimeout(() => {
      if (!hero.isConnected) return;
      playTrailer(owner, trailer, card.trailerKey!, {
        onPlaying: () => {
          hero.classList.add('trailer-on');
          muteBtn.classList.remove('fui-hidden');
        },
        onFail: () => {
          hero.classList.remove('trailer-on');
          muteBtn.classList.add('fui-hidden');
        },
      });
    }, 800);
    (hero as HTMLElement & { _stop?: () => void })._stop = () => {
      clearTimeout(t);
      stopTrailer(owner);
    };
  }
  return hero;
}

export function stopHero(hero: HTMLElement | null): void {
  (hero as (HTMLElement & { _stop?: () => void }) | null)?._stop?.();
}
