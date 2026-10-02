// Recent searches: per signed-in user, in localStorage, newest first, at most 8.
// Another user's list is never read (the key contains the user id) and nothing is stored without a user id.
import { client } from './api';

export const RECENT_MAX = 8;
const PREFIX = 'fullui-recent:';

/** Pure: add `q` to the front, drop case-insensitive duplicates and shorter prefixes of it ("bre" before "breaking"). */
export function addRecent(list: string[], q: string): string[] {
  const term = q.trim();
  if (term.length < 2) return list.slice(0, RECENT_MAX);
  const low = term.toLowerCase();
  const rest = list.filter((x) => {
    const l = x.toLowerCase();
    return l !== low && !low.startsWith(l);
  });
  return [term, ...rest].slice(0, RECENT_MAX);
}

export function removeRecent(list: string[], q: string): string[] {
  const low = q.toLowerCase();
  return list.filter((x) => x.toLowerCase() !== low);
}

/** The current user's id, or '' (signed out / unknown). */
export function currentUserId(): string {
  try {
    const c = client();
    return (c && c.getCurrentUserId ? c.getCurrentUserId() : '') || '';
  } catch {
    return '';
  }
}

function storeKey(uid: string): string {
  return PREFIX + uid;
}

export function loadRecent(uid = currentUserId()): string[] {
  if (!uid) return [];
  try {
    const raw = localStorage.getItem(storeKey(uid));
    const v = raw ? JSON.parse(raw) : [];
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string' && x.length > 0 && x.length <= 100).slice(0, RECENT_MAX) : [];
  } catch {
    return [];
  }
}

export function saveRecent(list: string[], uid = currentUserId()): void {
  if (!uid) return;
  try {
    if (list.length) localStorage.setItem(storeKey(uid), JSON.stringify(list.slice(0, RECENT_MAX)));
    else localStorage.removeItem(storeKey(uid));
  } catch {
    /* storage unavailable: recents just do not persist */
  }
}
