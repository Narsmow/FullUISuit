import type {
  HomeResponse,
  ItemCard,
  MyServerResponse,
  NotificationDto,
  PluginStatus,
  SearchResponse,
} from './types';
import { joinUrl, sessionKeyOf, voteBody } from './util';
import type { ComingSoonCard } from './types';

export interface JellyfinApiClient {
  /** Real jellyfin-apiclient 1.11 throws "Url name cannot be empty" for an empty name. */
  getUrl(path: string): string;
  serverAddress?(): string;
  accessToken(): string;
  getCurrentUserId?(): string;
  getCurrentUser?(): Promise<{ Name?: string; Policy?: { IsAdministrator?: boolean } }>;
  logout?(): Promise<unknown>;
}

declare global {
  interface Window {
    ApiClient?: JellyfinApiClient;
    Dashboard?: { logout?: () => unknown };
  }
}

export class ApiError extends Error {
  constructor(public status: number, message?: string) {
    super(message || `HTTP ${status}`);
  }
}

export const REQUEST_TIMEOUT_MS = 15000;

export function client(): JellyfinApiClient | undefined {
  return window.ApiClient;
}

/** Server root as seen by the browser (works with a base path such as /jellyfin). */
export function serverBase(c = client()): string {
  if (!c) return '';
  let base = '';
  try {
    base = (c.serverAddress ? c.serverAddress() : '') || '';
  } catch {
    base = '';
  }
  if (!base) {
    try {
      // never getUrl(''): the real client throws for an empty name
      base = c.getUrl('/');
    } catch {
      base = '';
    }
  }
  return base.replace(/\/+$/, '');
}

export function hasSession(c = client()): boolean {
  try {
    return !!c && !!c.accessToken();
  } catch {
    return false;
  }
}

/** Identity of the current session (user id + token); '' when signed out. Caches are keyed on this. */
export function sessionKey(c = client()): string {
  if (!c || !hasSession(c)) return '';
  let uid = '';
  try {
    uid = c.getCurrentUserId ? c.getCurrentUserId() || '' : '';
  } catch {
    uid = '';
  }
  return sessionKeyOf(uid, c.accessToken());
}

export async function request<T>(method: 'GET' | 'POST', path: string, body?: unknown, c = client()): Promise<T> {
  if (!c) throw new ApiError(0, 'no ApiClient');
  // No session (login page, signed out): do not send a doomed request with Token="null".
  if (!hasSession(c)) throw new ApiError(401, 'no session');
  const ctl = new AbortController();
  const timer = setTimeout(() => ctl.abort(), REQUEST_TIMEOUT_MS);
  try {
    const headers: Record<string, string> = { Authorization: `MediaBrowser Token="${c.accessToken()}"` };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const res = await fetch(joinUrl(serverBase(c), 'FullUI/' + path), {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: ctl.signal,
    });
    if (!res.ok) throw new ApiError(res.status);
    if (res.status === 204) return undefined as T;
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  } catch (e) {
    if (e instanceof ApiError) throw e;
    if (e instanceof DOMException && e.name === 'AbortError') throw new ApiError(0, 'timeout');
    throw new ApiError(0, e instanceof Error ? e.message : 'network error');
  } finally {
    clearTimeout(timer);
  }
}

export const api = {
  status: () => request<PluginStatus>('GET', 'Status'),
  home: () => request<HomeResponse>('GET', 'Home'),
  myServer: () => request<MyServerResponse>('GET', 'MyServer'),
  item: (id: string) => request<ItemCard>('GET', `Item/${encodeURIComponent(id)}`),
  search: (q: string) => request<SearchResponse>('GET', `Search?q=${encodeURIComponent(q)}`),
  rate: (itemId: string, rating: number) => request<unknown>('POST', 'Rate', { itemId, rating }),
  myList: (itemId: string, add: boolean) => request<unknown>('POST', 'MyList', { itemId, add }),
  vote: (c: ComingSoonCard, vote: number) => request<unknown>('POST', 'Vote', voteBody(c, vote)),
  notifications: () => request<{ items: NotificationDto[] }>('GET', 'Notifications'),
  markRead: (ids: string[] | null) => request<unknown>('POST', 'Notifications/Read', { ids }),
};
