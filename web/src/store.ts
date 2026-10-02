import { api } from './api';
import { t } from './i18n';
import { nextRating, nextVote } from './util';
import type { ComingSoonCard, ItemCard, RouteKind } from './types';

// Canonical per-page state so the same title shown in several rows stays in sync, plus the
// optimistic actions (rate / my list / vote) with rollback.

type Listener = () => void;

// Every rendered ItemCard object is registered per id so state changes (rating / My List) reach all
// copies of the same title. Per-row fields (rank, progress) stay on each copy.
const items = new Map<string, ItemCard[]>();
const soon = new Map<string, ComingSoonCard[]>();
const listeners = new Map<string, Set<Listener>>();

let toastFn: (msg: string) => void = () => {};
export function setToast(fn: (msg: string) => void): void {
  toastFn = fn;
}

let goFn: (kind: RouteKind, q?: string) => void = () => {};
/** main.ts registers the in-app navigator (switches FullUI pages without a hash change). */
export function setNavigator(fn: (kind: RouteKind, q?: string) => void): void {
  goFn = fn;
}
export function goTo(kind: RouteKind, q = ''): void {
  goFn(kind, q);
}

/** Call whenever a new page is rendered. */
export function resetStore(): void {
  items.clear();
  soon.clear();
  listeners.clear();
}

function join<T>(map: Map<string, T[]>, key: string, c: T, sync: (to: T, from: T) => void): T {
  const g = map.get(key);
  if (!g) map.set(key, [c]);
  else {
    sync(c, g[0]);
    g.push(c);
  }
  return c;
}

export function canonItem(c: ItemCard): ItemCard {
  return join(items, c.id, c, (to, from) => {
    to.myRating = from.myRating;
    to.inMyList = from.inMyList;
  });
}

export const soonKey = (c: Pick<ComingSoonCard, 'mediaType' | 'tmdbId'>) => `${c.mediaType}:${c.tmdbId}`;

export function canonSoon(c: ComingSoonCard): ComingSoonCard {
  return join(soon, soonKey(c), c, (to, from) => {
    to.myVote = from.myVote;
  });
}

function setItem(card: ItemCard, patch: Partial<Pick<ItemCard, 'myRating' | 'inMyList'>>): void {
  for (const c of items.get(card.id) || [card]) Object.assign(c, patch);
  Object.assign(card, patch);
  notify(card.id);
}
function setSoon(card: ComingSoonCard, myVote: number): void {
  for (const c of soon.get(soonKey(card)) || [card]) c.myVote = myVote;
  card.myVote = myVote;
  notify(soonSubKey(card));
}

export function subscribe(key: string, fn: Listener): void {
  let s = listeners.get(key);
  if (!s) listeners.set(key, (s = new Set()));
  s.add(fn);
}

function notify(key: string): void {
  listeners.get(key)?.forEach((f) => f());
}

export const itemKey = (c: ItemCard) => c.id;
export const soonSubKey = (c: ComingSoonCard) => 'soon:' + soonKey(c);

// Rapid clicks are never dropped: the UI updates optimistically on every click and the latest wanted
// value is sent once the previous request finishes (so the server always ends up with the last click).
interface Pending<T> {
  confirmed: T;
  want: T;
  running: boolean;
}
const pending = new Map<string, Pending<unknown>>();

let mutated: () => void = () => {};
/** Called after a successful change (rating / My List / vote) so cached Home data can be dropped. */
export function setOnMutate(fn: () => void): void {
  mutated = fn;
}

async function enqueue<T>(
  key: string,
  prev: T,
  next: T,
  send: (v: T) => Promise<unknown>,
  rollback: (v: T) => void,
  failMsg: string,
): Promise<void> {
  let p = pending.get(key) as Pending<T> | undefined;
  if (p) {
    p.want = next; // a request is already in flight; it will pick this up when it finishes
    return;
  }
  p = { confirmed: prev, want: next, running: true };
  pending.set(key, p as Pending<unknown>);
  try {
    while (p.want !== p.confirmed) {
      const v = p.want;
      try {
        await send(v);
        p.confirmed = v;
        mutated();
      } catch {
        p.want = p.confirmed;
        rollback(p.confirmed);
        toastFn(failMsg);
        break;
      }
    }
  } finally {
    pending.delete(key);
  }
}

/** Thumbs: -1 not for me, 1 like, 2 love; clicking the active level clears. */
export function setRating(card: ItemCard, clicked: number): Promise<void> {
  const prev = card.myRating;
  const next = nextRating(prev, clicked);
  setItem(card, { myRating: next });
  return enqueue(
    'rate:' + card.id,
    prev,
    next as number,
    (v) => api.rate(card.id, v),
    (v) => setItem(card, { myRating: v }),
    t('error.rating'),
  );
}

export function toggleMyList(card: ItemCard): Promise<void> {
  const prev = card.inMyList;
  setItem(card, { inMyList: !prev });
  return enqueue(
    'list:' + card.id,
    prev,
    !prev,
    (v) => api.myList(card.id, v),
    (v) => setItem(card, { inMyList: v }),
    t('error.myList'),
  );
}

export function setVote(card: ComingSoonCard, clicked: number): Promise<void> {
  const prev = card.myVote;
  const next = nextVote(prev, clicked);
  setSoon(card, next);
  return enqueue(
    'vote:' + soonKey(card),
    prev,
    next as number,
    (v) => api.vote(card, v),
    (v) => setSoon(card, v),
    t('error.vote'),
  );
}
