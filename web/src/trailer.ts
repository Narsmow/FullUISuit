// Single-instance YouTube trailer player. We create the <iframe> ourselves (so we control its referrer
// policy; jellyfin-web sends `Referrer-Policy: no-referrer`, which makes YouTube refuse many embeds with
// errors 150/152/153) and then attach the IFrame API's YT.Player to it. The API script loads lazily.

interface YTPlayer {
  mute(): void;
  unMute(): void;
  playVideo(): void;
  destroy(): void;
}
interface YTNamespace {
  Player: new (el: HTMLElement, opts: Record<string, unknown>) => YTPlayer;
}
declare global {
  interface Window {
    YT?: YTNamespace;
    onYouTubeIframeAPIReady?: () => void;
  }
}

export const YT_API_URL = 'https://www.youtube.com/iframe_api';
export const YT_EMBED_HOST = 'https://www.youtube-nocookie.com';
const READY_TIMEOUT_MS = 8000;
const RESUME_DELAY_MS = 600;

let enabled = true;
/** Admin switch from /FullUI/Status (`trailersEnabled`); missing means on. */
export function setTrailersEnabled(on: boolean): void {
  enabled = on;
  if (!on) stopTrailer();
}
export function trailersEnabled(): boolean {
  return enabled;
}

let apiPromise: Promise<YTNamespace> | null = null;
let readyHooked = false;
const readyWaiters: Array<() => void> = [];

function hookReady(): void {
  if (readyHooked) return;
  readyHooked = true;
  const prev = window.onYouTubeIframeAPIReady;
  window.onYouTubeIframeAPIReady = () => {
    try {
      prev?.();
    } catch {
      /* someone else's callback */
    }
    readyWaiters.splice(0).forEach((f) => f());
  };
}

function loadApi(): Promise<YTNamespace> {
  if (window.YT && window.YT.Player) return Promise.resolve(window.YT);
  if (apiPromise) return apiPromise;
  apiPromise = new Promise<YTNamespace>((resolve, reject) => {
    let done = false;
    const finish = (ok: boolean) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      if (ok && window.YT && window.YT.Player) resolve(window.YT);
      else {
        apiPromise = null; // allow a later retry; the <script> tag stays (never added twice)
        reject(new Error('YT unavailable'));
      }
    };
    const timer = setTimeout(() => finish(false), READY_TIMEOUT_MS);
    hookReady();
    readyWaiters.push(() => finish(true));
    let s = document.querySelector<HTMLScriptElement>(`script[src="${YT_API_URL}"]`);
    if (!s) {
      s = document.createElement('script');
      s.src = YT_API_URL;
      s.async = true;
      document.head.appendChild(s);
    }
    s.addEventListener('error', () => finish(false));
  });
  return apiPromise;
}

export function reducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export interface TrailerCallbacks {
  onPlaying?: () => void;
  /** The trailer could not play (or was disabled). */
  onFail?: () => void;
  /** Another owner's trailer took over; the owner should drop its "trailer is showing" state. */
  onStop?: () => void;
}

interface Active {
  owner: unknown;
  container: HTMLElement;
  key: string;
  cb: TrailerCallbacks;
  player: YTPlayer | null;
  dead: boolean;
  muted: boolean;
}

let active: Active | null = null;
/** A trailer (the hero's) that another owner pushed aside; resumes when that owner stops. */
let displaced: Active | null = null;
let resumeT: ReturnType<typeof setTimeout> | undefined;

export function activeOwner(): unknown {
  return active ? active.owner : null;
}

function kill(a: Active): void {
  a.dead = true;
  try {
    a.player?.destroy();
  } catch {
    /* ignore */
  }
  while (a.container.firstChild) a.container.removeChild(a.container.firstChild);
}

export function stopTrailer(owner?: unknown): void {
  if (owner === undefined) {
    // stop everything, including anything waiting to resume
    displaced = null;
    clearTimeout(resumeT);
  } else if (displaced && displaced.owner === owner) {
    displaced = null;
  }
  if (!active || (owner !== undefined && active.owner !== owner)) return;
  const a = active;
  active = null;
  kill(a);
  if (owner !== undefined && displaced && displaced.container.isConnected && !document.hidden) {
    const d = displaced;
    displaced = null;
    clearTimeout(resumeT);
    resumeT = setTimeout(() => {
      if (!d.container.isConnected) return;
      if (active) displaced = d; // someone else started meanwhile: wait for them to finish
      else playTrailer(d.owner, d.container, d.key, d.cb, true);
    }, RESUME_DELAY_MS);
  }
}

function embedSrc(key: string): string {
  const q = [
    'enablejsapi=1',
    'autoplay=1',
    'mute=1',
    'controls=0',
    'rel=0',
    'playsinline=1',
    'modestbranding=1',
    'disablekb=1',
    'loop=1',
    'playlist=' + encodeURIComponent(key),
    'iv_load_policy=3',
    'origin=' + encodeURIComponent(location.origin),
  ];
  return `${YT_EMBED_HOST}/embed/${encodeURIComponent(key)}?${q.join('&')}`;
}

/** Starts a muted autoplaying trailer in `container`, displacing any other active one. */
export function playTrailer(owner: unknown, container: HTMLElement, key: string, cb: TrailerCallbacks = {}, resumed = false): void {
  const prev = active;
  if (prev) {
    active = null;
    kill(prev);
    if (prev.owner !== owner) {
      // remember the first thing we displaced so it can come back afterwards
      if (!displaced && !resumed) displaced = prev;
      prev.cb.onStop?.();
    }
  }
  if (!enabled || reducedMotion() || !/^[\w-]{6,20}$/.test(key)) {
    cb.onFail?.();
    return;
  }
  const a: Active = { owner, container, key, cb, player: null, dead: false, muted: true };
  active = a;
  const fail = () => {
    if (a.dead) return;
    stopTrailer(owner);
    cb.onFail?.();
  };
  let timer = setTimeout(fail, READY_TIMEOUT_MS);
  loadApi().then(
    (YT) => {
      if (a.dead) {
        clearTimeout(timer);
        return;
      }
      const frame = document.createElement('iframe');
      frame.className = 'fui-yt-frame';
      frame.setAttribute('referrerpolicy', 'strict-origin-when-cross-origin');
      frame.setAttribute('allow', 'autoplay; encrypted-media; picture-in-picture');
      frame.setAttribute('title', 'Trailer');
      frame.setAttribute('tabindex', '-1');
      frame.setAttribute('aria-hidden', 'true');
      frame.setAttribute('frameborder', '0');
      frame.src = embedSrc(key);
      container.appendChild(frame);
      try {
        a.player = new YT.Player(frame, {
          events: {
            onReady: (e: { target: YTPlayer }) => {
              if (a.dead) return;
              e.target.mute();
              e.target.playVideo();
            },
            onStateChange: (e: { data: number }) => {
              if (a.dead) return;
              if (e.data === 1) {
                clearTimeout(timer);
                cb.onPlaying?.();
              } else if (e.data === 3) {
                // buffering: give it a fresh full window before giving up
                clearTimeout(timer);
                timer = setTimeout(fail, READY_TIMEOUT_MS);
              }
            },
            onError: () => {
              clearTimeout(timer);
              fail();
            },
          },
        });
      } catch {
        clearTimeout(timer);
        fail();
      }
    },
    () => {
      clearTimeout(timer);
      fail();
    },
  );
}

export function setTrailerMuted(owner: unknown, muted: boolean): void {
  if (!active || active.owner !== owner || !active.player) return;
  active.muted = muted;
  try {
    if (muted) active.player.mute();
    else active.player.unMute();
  } catch {
    /* ignore */
  }
}

export function isTrailerMuted(owner: unknown): boolean {
  return !active || active.owner !== owner || active.muted;
}
