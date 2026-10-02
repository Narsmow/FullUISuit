// Fail-safe player assist: a "Skip Intro / Recap / Credits" button and a "Next episode" countdown drawn on top of
// jellyfin-web's NATIVE video player. The user's rule: this must never be able to break playback.
//
// Safety contract (each point is covered by tests in playerAssist.test.ts / the e2e fault-injection suite):
//  * runs only on the native video route (#/video) and only while Status.playerAssistEnabled !== false;
//  * the <video> is located read-only; the ONLY native state we ever change is `video.currentTime = segment.end`
//    on an explicit click of our button, plus one `click()` on the native next-track button (Play now / countdown end);
//  * no native DOM node is modified, moved or removed; our overlay is a separate fixed, pointer-events:none container
//    (only our own buttons take clicks) with a low z-index so the native OSD stays on top;
//  * every handler is wrapped: after 3 caught errors the assist switches itself off for the session
//    (sessionStorage flag) and logs a single console.warn;
//  * the overlay and all listeners/timers are removed on route change, pagehide and when the video ends.
//
// Verified against jellyfin-web release-10.11.z (see docs notes in web/README.md):
//  * next episode control: `button.btnNextTrack` in controllers/playback/video/index.html (class `hide` while there is
//    no next item). `btnNextChapter` is CHAPTER navigation, not next episode, and is never used here.
//  * native skip UI: components/playback/skipsegment.ts appends `div.skip-button-container > button.skip-button`
//    to <body> (classes `hide` / `skip-button-hidden` while hidden). When it is visible ours stays hidden.
//  * native "up next" dialog is mounted in `.upNextContainer`; when it has content ours stays hidden.
import { client, hasSession, serverBase } from './api';
import { h } from './dom';
import { t, tn } from './i18n';
import { joinUrl } from './util';

export const ASSIST_OFF_KEY = 'fullui-assist-off';
export const MAX_ERRORS = 3;
const TICKS_PER_SECOND = 10_000_000;
const THROTTLE_MS = 250;
const FIND_VIDEO_EVERY_MS = 500;
const FIND_VIDEO_FOR_MS = 30000;
const FETCH_TIMEOUT_MS = 8000;
/** Show the next-episode card when this many seconds (or fewer) are left. */
export const NEXT_ZONE_SECONDS = 20;
export const COUNTDOWN_SECONDS = 10;
const NEXT_SELECTORS = ['.btnNextTrack'];

// ---- pure helpers (unit-tested) -----------------------------------------------------------
export type SegmentType = 'Intro' | 'Recap' | 'Outro' | 'Preview' | 'Commercial' | 'Unknown';
export interface Segment {
  type: SegmentType;
  start: number; // seconds
  end: number;
}

const SKIPPABLE: SegmentType[] = ['Intro', 'Recap', 'Outro', 'Preview'];

/** Strict parse of GET /MediaSegments/{id}: anything malformed is dropped (never throws). */
export function parseSegments(payload: unknown): Segment[] {
  const list = Array.isArray(payload)
    ? payload
    : payload && typeof payload === 'object' && Array.isArray((payload as { Items?: unknown }).Items)
      ? (payload as { Items: unknown[] }).Items
      : [];
  const out: Segment[] = [];
  for (const raw of list) {
    if (!raw || typeof raw !== 'object') continue;
    const r = raw as Record<string, unknown>;
    const s = Number(r.StartTicks);
    const e = Number(r.EndTicks);
    if (!Number.isFinite(s) || !Number.isFinite(e) || s < 0 || e <= s) continue;
    const ty = typeof r.Type === 'string' && (['Intro', 'Recap', 'Outro', 'Preview', 'Commercial'] as string[]).includes(r.Type) ? (r.Type as SegmentType) : 'Unknown';
    out.push({ type: ty, start: s / TICKS_PER_SECOND, end: e / TICKS_PER_SECOND });
  }
  return out.sort((a, b) => a.start - b.start);
}

/** The skippable segment containing time `t` (needs at least a second left to be worth skipping). */
export function activeSegment(segs: Segment[], t: number): Segment | null {
  if (!Number.isFinite(t)) return null;
  for (const s of segs) if (SKIPPABLE.includes(s.type) && t >= s.start && t < s.end - 1) return s;
  return null;
}

export function skipLabel(type: SegmentType): string {
  switch (type) {
    case 'Recap':
      return t('assist.skipRecap');
    case 'Outro':
      return t('assist.skipCredits');
    case 'Preview':
      return t('assist.skipPreview');
    default:
      return t('assist.skipIntro');
  }
}

export function isVideoRoute(hash: string): boolean {
  return /^#!?\/video(?:[/?]|$)/i.test(hash || '');
}

const VIDEO_ID_RE = /\/videos\/([0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12})(?:[/?]|$)/i;
export function itemIdFromUrl(url: string | null | undefined): string | null {
  const m = url ? VIDEO_ID_RE.exec(url) : null;
  return m ? m[1] : null;
}

/** Are we in the "next episode" zone? `remaining` is seconds until the end. */
export function inNextZone(t: number, duration: number, segs: Segment[]): boolean {
  if (!Number.isFinite(t) || !Number.isFinite(duration) || duration < 60) return false;
  const remaining = duration - t;
  if (remaining <= NEXT_ZONE_SECONDS && remaining > 0) return true;
  return segs.some((s) => s.type === 'Outro' && t >= s.start && t < s.end && s.end >= duration - 3);
}

export function countdownFrom(remaining: number): number {
  return Math.max(3, Math.min(COUNTDOWN_SECONDS, Math.floor(remaining - 2)));
}

// ---- the assist ------------------------------------------------------------------------------
export interface AssistDeps {
  /** Status.playerAssistEnabled !== false (and Status loaded). */
  isEnabled: () => boolean;
}

export interface Assist {
  /** Call on every route event; starts or stops the assist as the route requires. */
  sync(): void;
  /** Stop and clean up everything. */
  stop(): void;
}

function storage(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

export function assistDisabledForSession(): boolean {
  try {
    return storage()?.getItem(ASSIST_OFF_KEY) === '1';
  } catch {
    return false;
  }
}

function nativeSkipVisible(): boolean {
  const b = document.querySelector('.skip-button');
  return !!b && !b.classList.contains('hide') && !b.classList.contains('skip-button-hidden');
}
function nativeUpNextShowing(): boolean {
  const c = document.querySelector('.upNextContainer');
  return !!c && !c.classList.contains('hide') && c.childElementCount > 0;
}
function nativeNext(): HTMLElement | null {
  for (const sel of NEXT_SELECTORS) {
    const el = document.querySelector<HTMLElement>(sel);
    if (el && !el.classList.contains('hide') && !el.hasAttribute('disabled')) return el;
  }
  return null;
}

export function createPlayerAssist(deps: AssistDeps): Assist {
  let errors = 0;
  let off = assistDisabledForSession();
  let warned = false;

  let finder: ReturnType<typeof setInterval> | undefined;
  let video: HTMLVideoElement | null = null;
  let overlay: HTMLElement | null = null;
  let skipBtn: HTMLButtonElement | null = null;
  let nextCard: HTMLElement | null = null;
  let nextText: HTMLElement | null = null;
  const unbind: Array<() => void> = [];

  // per-item state
  let itemKey = '';
  let segments: Segment[] = [];
  let loadSeq = 0;
  const suppressed = new Set<string>(); // segments the native skip button already offered
  let current: Segment | null = null;
  let nextCancelled = false;
  let nextStartT = -1;
  let nextTotal = 0;
  let nextFired = false;
  let lastTick = 0;

  function trip(e: unknown): void {
    errors++;
    if (errors >= MAX_ERRORS && !off) {
      off = true;
      try {
        storage()?.setItem(ASSIST_OFF_KEY, '1');
      } catch {
        /* the in-memory flag is enough for this page */
      }
      if (!warned) {
        warned = true;
        console.warn('[FullUI] Player assist turned itself off for this session after repeated errors.', e);
      }
      teardown();
    }
  }
  /** Wrap any callback: an exception is counted, never rethrown into jellyfin-web. */
  function guard<A extends unknown[]>(fn: (...a: A) => void): (...a: A) => void {
    return (...a: A) => {
      try {
        fn(...a);
      } catch (e) {
        trip(e);
      }
    };
  }

  function teardown(): void {
    clearInterval(finder);
    finder = undefined;
    for (const f of unbind.splice(0)) {
      try {
        f();
      } catch {
        /* ignore */
      }
    }
    try {
      overlay?.remove();
    } catch {
      /* ignore */
    }
    overlay = skipBtn = nextCard = nextText = null;
    video = null;
    loadSeq++;
    itemKey = '';
    segments = [];
    suppressed.clear();
    current = null;
    nextCancelled = nextFired = false;
    nextStartT = -1;
  }

  function on(target: EventTarget, type: string, fn: EventListener, opts?: AddEventListenerOptions): void {
    target.addEventListener(type, fn, opts);
    unbind.push(() => target.removeEventListener(type, fn, opts));
  }

  // ---- data ----
  async function getJson(path: string): Promise<unknown> {
    const c = client();
    if (!c || !hasSession(c)) return null;
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), FETCH_TIMEOUT_MS);
    try {
      const res = await fetch(joinUrl(serverBase(c), path), {
        headers: { Authorization: `MediaBrowser Token="${c.accessToken()}"` },
        signal: ctl.signal,
      });
      if (!res.ok) return null; // 404 = no segments / older server: normal, not an error
      const text = await res.text();
      return text ? JSON.parse(text) : null;
    } catch {
      return null; // network trouble or malformed JSON: simply no segments
    } finally {
      clearTimeout(timer);
    }
  }

  async function resolveItemId(v: HTMLVideoElement): Promise<string | null> {
    const fromUrl = itemIdFromUrl(v.currentSrc || v.src);
    if (fromUrl) return fromUrl;
    // HLS plays from a blob: URL; ask the server what this device is playing
    const c = client() as (ReturnType<typeof client> & { deviceId?: () => string }) | undefined;
    let dev = '';
    try {
      dev = c && c.deviceId ? c.deviceId() : '';
    } catch {
      dev = '';
    }
    if (!dev) return null;
    const sessions = await getJson(`Sessions?DeviceId=${encodeURIComponent(dev)}`);
    if (!Array.isArray(sessions)) return null;
    for (const s of sessions) {
      const id = s && s.NowPlayingItem && s.NowPlayingItem.Id;
      if (typeof id === 'string' && /^[0-9a-f-]{32,36}$/i.test(id)) return id;
    }
    return null;
  }

  function loadSegments(v: HTMLVideoElement, key: string): void {
    const my = ++loadSeq;
    void (async () => {
      try {
        const id = await resolveItemId(v);
        if (!id || my !== loadSeq) return;
        const payload = await getJson(`MediaSegments/${encodeURIComponent(id)}`);
        if (my !== loadSeq || key !== itemKey) return;
        segments = parseSegments(payload);
      } catch (e) {
        trip(e);
      }
    })();
  }

  // ---- overlay ----
  function buildOverlay(): void {
    skipBtn = h('button', { type: 'button', class: 'fui-assist-btn fui-assist-skip fui-assist-hidden', text: t('assist.skipIntro') });
    on(skipBtn, 'click', guard(() => onSkip()));
    nextText = h('div', { class: 'fui-assist-next-text', text: t('assist.nextEpisode') });
    const playNow = h('button', { type: 'button', class: 'fui-assist-btn fui-assist-playnow', text: t('assist.playNow') });
    const cancel = h('button', { type: 'button', class: 'fui-assist-btn fui-assist-cancel', text: t('assist.cancel') });
    on(playNow, 'click', guard(() => playNext()));
    on(cancel, 'click', guard(() => {
      nextCancelled = true;
      hideNext();
    }));
    nextCard = h('div', { class: 'fui-assist-next fui-assist-hidden' }, nextText, h('div', { class: 'fui-assist-next-btns' }, playNow, cancel));
    overlay = h('div', { id: 'fullui-assist', class: 'fui-assist', role: 'region', 'aria-label': t('assist.region') }, skipBtn, nextCard);
    document.body.appendChild(overlay);
  }

  function hideNext(): void {
    nextCard?.classList.add('fui-assist-hidden');
  }

  function onSkip(): void {
    const v = video;
    const seg = current;
    if (!v || !seg) return;
    const target = Number.isFinite(v.duration) ? Math.min(seg.end, v.duration) : seg.end;
    v.currentTime = target; // the one mutation of native state
  }

  function playNext(): void {
    const btn = nextBtn();
    if (btn) btn.click();
    hideNext();
  }
  const nextBtn = nativeNext;

  // ---- per-tick logic (throttled timeupdate) ----
  function tick(): void {
    const v = video;
    if (!v || !skipBtn || !nextCard || !nextText) return;
    const key = (v.currentSrc || v.src || '') + '|' + (Number.isFinite(v.duration) ? Math.round(v.duration) : 0);
    if (key !== itemKey) {
      // a different item is playing in the same element (next episode): start over
      itemKey = key;
      segments = [];
      suppressed.clear();
      current = null;
      nextCancelled = nextFired = false;
      nextStartT = -1;
      skipBtn.classList.add('fui-assist-hidden');
      hideNext();
      loadSegments(v, key);
      return;
    }
    const time = v.currentTime;
    const seg = activeSegment(segments, time);
    const segKey = seg ? `${seg.type}:${seg.start}` : '';
    if (seg && nativeSkipVisible()) suppressed.add(segKey); // the native player offers its own button: stay out of the way
    current = seg && !suppressed.has(segKey) ? seg : null;
    if (current) {
      skipBtn.textContent = skipLabel(current.type);
      skipBtn.classList.remove('fui-assist-hidden');
    } else skipBtn.classList.add('fui-assist-hidden');

    // next episode
    const dur = v.duration;
    const zone = inNextZone(time, dur, segments) && !nextCancelled && !!nextBtn() && !nativeUpNextShowing();
    if (!zone) {
      hideNext();
      return;
    }
    const remaining = dur - time;
    if (nextStartT < 0) {
      if (remaining < 5) return; // too late for a useful countdown; the native end-of-episode flow handles it
      nextStartT = time;
      nextTotal = countdownFrom(remaining);
    }
    const n = Math.max(0, nextTotal - Math.floor(time - nextStartT));
    nextText.textContent = n > 0 ? tn('assist.nextIn', n) : t('assist.nextEpisode');
    nextCard.classList.remove('fui-assist-hidden');
    if (n <= 0 && !nextFired) {
      nextFired = true;
      playNext();
    }
  }

  function attach(v: HTMLVideoElement): void {
    video = v;
    buildOverlay();
    const onTime = guard(() => {
      const now = Date.now();
      if (now - lastTick < THROTTLE_MS) return;
      lastTick = now;
      tick();
    });
    on(v, 'timeupdate', onTime);
    on(v, 'loadedmetadata', guard(() => tick()));
    on(v, 'ended', guard(() => {
      // remove everything now; look again shortly in case the native player continues with the next episode
      teardown();
      setTimeout(() => self.sync(), 1500);
    }));
    on(window, 'pagehide', guard(() => teardown()));
    tick();
  }

  function shouldRun(): boolean {
    return !off && isVideoRoute(location.hash) && hasSession() && deps.isEnabled();
  }

  const self: Assist = {
    sync() {
      try {
        if (!shouldRun()) {
          if (video || finder) teardown();
          return;
        }
        if (video && !video.isConnected) teardown(); // the native player swapped its element
        if (video || finder) return;
        const t0 = Date.now();
        const look = guard(() => {
          const v = document.querySelector<HTMLVideoElement>('video.htmlvideoplayer') || document.querySelector<HTMLVideoElement>('video');
          if (v) {
            clearInterval(finder);
            finder = undefined;
            attach(v);
          } else if (Date.now() - t0 > FIND_VIDEO_FOR_MS) {
            clearInterval(finder);
            finder = undefined;
          }
        });
        finder = setInterval(look, FIND_VIDEO_EVERY_MS);
        look();
      } catch (e) {
        trip(e);
      }
    },
    stop: () => teardown(),
  };
  return self;
}
