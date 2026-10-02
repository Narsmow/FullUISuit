import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, hasSession, serverBase, sessionKey } from './api';
import { searchPlaceholder } from './pages';
import { createCard, createComingSoonCard } from './card';
import { createRow } from './rows';
import { resetStore, setRating, setVote, toggleMyList } from './store';
import type { ItemCard } from './types';

const evil = '<img src=x onerror="window.__pwned=1"><script>window.__pwned=1</script>';
const mk = (o: Partial<ItemCard> = {}): ItemCard => ({
  id: 'i1',
  name: evil,
  type: 'Movie',
  genres: [evil],
  badges: [evil],
  overview: evil,
  hasBackdrop: true,
  hasLogo: false,
  myRating: 0,
  inMyList: false,
  ...o,
});

/** Behaves like jellyfin-apiclient 1.11: getUrl('') throws "Url name cannot be empty". */
function stubClient(base = 'http://srv') {
  window.ApiClient = {
    serverAddress: () => base,
    getUrl: (p: string) => {
      if (!p) throw new Error('Url name cannot be empty');
      return base + (p.startsWith('/') ? '' : '/') + p;
    },
    accessToken: () => 'tok',
    getCurrentUserId: () => 'u1',
  };
}

describe('untrusted strings', () => {
  beforeEach(() => {
    resetStore();
    stubClient();
  });
  afterEach(() => vi.useRealTimers());
  it('never turns server strings into markup (card, expanded .fui-info panel, coming soon)', () => {
    vi.useFakeTimers();
    document.body.replaceChildren();
    const el = createCard(mk({ hasLogo: true }));
    document.body.appendChild(el);
    el.dispatchEvent(new Event('mouseenter')); // hover opens the lazy info panel
    vi.advanceTimersByTime(500);
    const info = el.querySelector('.fui-info');
    expect(info).not.toBeNull(); // the XSS-prone panel really was built
    expect(info!.textContent).toContain(evil); // shown as text...
    expect(el.querySelectorAll('script').length).toBe(0); // ...never as markup
    expect(el.querySelectorAll('img[src="x"]').length).toBe(0);
    expect(info!.querySelectorAll('b, img[onerror], [onerror]').length).toBe(0);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
    expect(el.querySelector('.fui-art')!.getAttribute('aria-label')).toBe(evil);
    const soon = createComingSoonCard({ tmdbId: 1, mediaType: 'movie', title: evil, overview: evil, myVote: 0 });
    expect(soon.querySelectorAll('script, img[src="x"]').length).toBe(0);
    expect(soon.querySelector('.fui-soon-title')!.textContent).toBe(evil);
  });
});

describe('server base (B-01)', () => {
  it('works with a client whose getUrl rejects an empty name', () => {
    stubClient('http://srv/jellyfin/');
    expect(serverBase()).toBe('http://srv/jellyfin');
    window.ApiClient = {
      getUrl: (p: string) => {
        if (!p) throw new Error('Url name cannot be empty');
        return 'http://x/base' + p;
      },
      accessToken: () => 't',
    };
    expect(serverBase()).toBe('http://x/base'); // falls back to getUrl('/')
  });
  it('the stub really throws on an empty name', () => {
    stubClient();
    expect(() => window.ApiClient!.getUrl('')).toThrow('Url name cannot be empty');
  });
  it('identity key needs a token and includes the user', () => {
    stubClient();
    expect(sessionKey()).toBe('u1|tok');
    window.ApiClient = { getUrl: (p: string) => p, accessToken: () => '' };
    expect(hasSession()).toBe(false);
    expect(sessionKey()).toBe('');
  });
  it('makes no network request without a session', async () => {
    const f = vi.fn();
    vi.stubGlobal('fetch', f);
    window.ApiClient = { getUrl: (p: string) => p, accessToken: () => '' };
    const { api } = await import('./api');
    await expect(api.status()).rejects.toBeInstanceOf(ApiError);
    expect(f).not.toHaveBeenCalled();
  });
});

describe('search placeholder (B-60)', () => {
  it('only promises AI search when it is on', () => {
    expect(searchPlaceholder(true)).toContain('describe');
    expect(searchPlaceholder(false)).not.toContain('describe');
  });
});

describe('optimistic actions', () => {
  beforeEach(() => {
    resetStore();
    stubClient();
  });
  it('rates once, optimistically, and clears on second click', async () => {
    const calls: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_u: string, init: RequestInit) => {
        calls.push(JSON.parse(String(init.body)));
        return new Response('{}', { status: 200 });
      }),
    );
    const c = mk({ name: 'X' });
    const p1 = setRating(c, 1);
    expect(c.myRating).toBe(1); // optimistic
    await p1;
    expect(calls).toEqual([{ itemId: 'i1', rating: 1 }]);
    await setRating(c, 1);
    expect(c.myRating).toBe(0);
    expect(calls[1]).toEqual({ itemId: 'i1', rating: 0 });
  });
  it('does not drop a second click while the first request is in flight (B-52)', async () => {
    const calls: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_u: string, init: RequestInit) => {
        calls.push(JSON.parse(String(init.body)));
        await new Promise((r) => setTimeout(r, 10));
        return new Response(null, { status: 204 });
      }),
    );
    const c = mk({ name: 'X' });
    const p1 = setRating(c, 1);
    const p2 = setRating(c, 2); // switches to love while "like" is still being saved
    expect(c.myRating).toBe(2);
    await Promise.all([p1, p2]);
    await new Promise((r) => setTimeout(r, 50));
    expect(calls).toEqual([
      { itemId: 'i1', rating: 1 },
      { itemId: 'i1', rating: 2 },
    ]);
    expect(c.myRating).toBe(2);
  });
  it('a quick double toggle ends in the original state on the server too', async () => {
    const calls: unknown[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (_u: string, init: RequestInit) => {
        calls.push(JSON.parse(String(init.body)));
        await new Promise((r) => setTimeout(r, 10));
        return new Response(null, { status: 204 });
      }),
    );
    const c = mk({ name: 'X' });
    const p1 = toggleMyList(c);
    const p2 = toggleMyList(c); // back to the original state before the first reply
    await Promise.all([p1, p2]);
    await new Promise((r) => setTimeout(r, 50));
    expect(calls).toEqual([{ itemId: 'i1', add: true }, { itemId: 'i1', add: false }]);
    expect(c.inMyList).toBe(false);
  });
  it('rolls back on failure', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 500 })));
    const c = mk({ name: 'X' });
    await toggleMyList(c);
    expect(c.inMyList).toBe(false);
    const s = { tmdbId: 3, mediaType: 'tv', title: 'T', myVote: 0 };
    await setVote(s, 1);
    expect(s.myVote).toBe(0);
  });
});

describe('rows', () => {
  it('shows a friendly placeholder with a retry when a row cannot be built', () => {
    stubClient();
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const bad = { id: 'x', title: 'Broken', type: 'toppicks', items: [null as unknown as ItemCard] };
    const el = createRow(bad)!;
    expect(el.classList.contains('fui-row-failed')).toBe(true);
    expect(el.textContent).toContain("This row couldn't be shown.");
    expect(el.querySelector('button')!.textContent).toBe('Try again');
    warn.mockRestore();
  });
  it('skips empty rows', () => {
    expect(createRow({ id: 'e', title: 'E', type: 'recent', items: [] })).toBeNull();
  });
});
