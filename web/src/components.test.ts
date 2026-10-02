import { beforeEach, describe, expect, it, vi } from 'vitest';
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

function stubClient() {
  window.ApiClient = { getUrl: (p: string) => 'http://srv/' + p, accessToken: () => 'tok' };
}

describe('untrusted strings', () => {
  beforeEach(() => {
    resetStore();
    stubClient();
  });
  it('never turns server strings into markup', () => {
    const el = createCard(mk());
    el.dispatchEvent(new Event('mouseenter'));
    // force the lazy info panel to build
    el.dispatchEvent(new FocusEvent('focusin'));
    document.body.appendChild(el);
    expect(el.querySelectorAll('script').length).toBe(0);
    expect(el.querySelectorAll('img[src="x"]').length).toBe(0);
    expect((window as unknown as { __pwned?: number }).__pwned).toBeUndefined();
    expect(el.getAttribute('aria-label')).toBe(evil);
    const soon = createComingSoonCard({ tmdbId: 1, mediaType: 'movie', title: evil, overview: evil, myVote: 0 });
    expect(soon.querySelectorAll('script, img[src="x"]').length).toBe(0);
    expect(soon.querySelector('.fui-soon-title')!.textContent).toBe(evil);
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
    const p2 = setRating(c, 1); // in flight: ignored
    expect(c.myRating).toBe(1);
    await Promise.all([p1, p2]);
    expect(calls).toEqual([{ itemId: 'i1', rating: 1 }]);
    await setRating(c, 1);
    expect(c.myRating).toBe(0);
    expect(calls[1]).toEqual({ itemId: 'i1', rating: 0 });
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
