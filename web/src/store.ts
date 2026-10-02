import { api } from './api';
import { nextRating, nextVote } from './util';
import type { ComingSoonCard, ItemCard } from './types';

// Canonical per-page state so the same title shown in several rows stays in sync, plus the
// optimistic actions (rate / my list / vote) with rollback.

type Listener = () => void;

// Every rendered ItemCard object is registered per id so state changes (rating / My List) reach all
// copies of the same title. Per-row fields (rank, progress) stay on each copy.
const items = new Map<string, ItemCard[]>();
const soon = new Map<string, ComingSoonCard[]>();
const listeners = new Map<string, Set<Listener>>();
const busy = new Set<string>();

let toastFn: (msg: string) => void = () => {};
export function setToast(fn: (msg: string) => void): void {
  toastFn = fn;
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

/** Thumbs: -1 not for me, 1 like, 2 love; clicking the active level clears. */
export async function setRating(card: ItemCard, clicked: number): Promise<void> {
  const k = 'rate:' + card.id;
  if (busy.has(k)) return;
  const prev = card.myRating;
  const next = nextRating(prev, clicked);
  busy.add(k);
  setItem(card, { myRating: next });
  try {
    await api.rate(card.id, next);
  } catch {
    setItem(card, { myRating: prev });
    toastFn("Couldn't save your rating. Try again.");
  } finally {
    busy.delete(k);
  }
}

export async function toggleMyList(card: ItemCard): Promise<void> {
  const k = 'list:' + card.id;
  if (busy.has(k)) return;
  const prev = card.inMyList;
  busy.add(k);
  setItem(card, { inMyList: !prev });
  try {
    await api.myList(card.id, !prev);
  } catch {
    setItem(card, { inMyList: prev });
    toastFn("Couldn't update My List. Try again.");
  } finally {
    busy.delete(k);
  }
}

export async function setVote(card: ComingSoonCard, clicked: number): Promise<void> {
  const k = 'vote:' + soonKey(card);
  if (busy.has(k)) return;
  const prev = card.myVote;
  const next = nextVote(prev, clicked);
  busy.add(k);
  setSoon(card, next);
  try {
    await api.vote(card, next);
  } catch {
    setSoon(card, prev);
    toastFn("Couldn't save your vote. Try again.");
  } finally {
    busy.delete(k);
  }
}
