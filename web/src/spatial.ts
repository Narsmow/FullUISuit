import { collapseExpanded } from './card';
import { pickNeighbor, type Dir } from './util';

const FOCUSABLE = 'a[href], button:not([disabled]), input, .fui-card[tabindex="0"]';

const KEY_DIR: Record<string, Dir> = { ArrowLeft: 'left', ArrowRight: 'right', ArrowUp: 'up', ArrowDown: 'down' };
const KEYCODE_DIR: Record<number, Dir> = { 37: 'left', 39: 'right', 38: 'up', 40: 'down' };
// Back: Escape/Backspace (desktop), GoBack/BrowserBack, Tizen 10009, webOS 461, Android TV 4.
const BACK_CODES = new Set([10009, 461, 4]);

function visible(el: HTMLElement): boolean {
  const r = el.getBoundingClientRect();
  if (r.width < 2 || r.height < 2) return false;
  if (el.closest('.fui-hidden')) return false;
  return getComputedStyle(el).visibility !== 'hidden';
}

export function focusables(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (el) => el.tabIndex >= 0 && visible(el),
  );
}

export function move(root: HTMLElement, dir: Dir): boolean {
  const cur = document.activeElement as HTMLElement | null;
  const list = focusables(root);
  if (!list.length) return false;
  if (!cur || !root.contains(cur) || cur === document.body) {
    const target = list.find((e) => e.classList.contains('fui-card')) || list[0];
    target.focus({ preventScroll: true });
    target.scrollIntoView({ block: 'center', inline: 'nearest' });
    return true;
  }
  const others = list.filter((e) => e !== cur);
  const idx = pickNeighbor(
    cur.getBoundingClientRect(),
    others.map((e) => e.getBoundingClientRect()),
    dir,
  );
  if (idx < 0) return false;
  const next = others[idx];
  next.focus({ preventScroll: true });
  next.scrollIntoView({ block: dir === 'left' || dir === 'right' ? 'nearest' : 'center', inline: 'nearest' });
  return true;
}

export interface SpatialHooks {
  isActive(): boolean;
  /** Close dropdowns; return true if something was closed. */
  closeMenus(): boolean;
  /** Called for Back when nothing else consumed it. Return true if handled. */
  back(): boolean;
}

export function installSpatial(root: HTMLElement, hooks: SpatialHooks): () => void {
  const onKey = (e: KeyboardEvent) => {
    if (!hooks.isActive() || e.defaultPrevented) return;
    if (e.altKey || e.ctrlKey || e.metaKey) return;
    const t = e.target as HTMLElement;
    const inInput = t instanceof HTMLInputElement;
    const dir = KEY_DIR[e.key] || (e.key === 'Unidentified' ? KEYCODE_DIR[e.keyCode] : undefined);
    if (dir) {
      if (inInput && (dir === 'left' || dir === 'right')) return;
      if (move(root, dir)) e.preventDefault();
      return;
    }
    const isBack =
      e.key === 'Escape' ||
      e.key === 'GoBack' ||
      e.key === 'BrowserBack' ||
      (e.key === 'Backspace' && !inInput) ||
      BACK_CODES.has(e.keyCode);
    if (isBack) {
      if (hooks.closeMenus() || collapseExpanded() || hooks.back()) {
        e.preventDefault();
        e.stopPropagation();
      }
    }
  };
  document.addEventListener('keydown', onKey, true);
  return () => document.removeEventListener('keydown', onKey, true);
}
