// Single-instance YouTube trailer player. The IFrame API is loaded lazily on first use.

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
const READY_TIMEOUT_MS = 8000;

let apiPromise: Promise<YTNamespace> | null = null;

function loadApi(): Promise<YTNamespace> {
  if (window.YT && window.YT.Player) return Promise.resolve(window.YT);
  if (apiPromise) return apiPromise;
  apiPromise = new Promise<YTNamespace>((resolve, reject) => {
    const prev = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = () => {
      prev?.();
      if (window.YT) resolve(window.YT);
      else reject(new Error('YT missing'));
    };
    const s = document.createElement('script');
    s.src = YT_API_URL;
    s.async = true;
    s.onerror = () => {
      apiPromise = null;
      reject(new Error('YT load failed'));
    };
    document.head.appendChild(s);
    setTimeout(() => {
      if (!(window.YT && window.YT.Player)) {
        apiPromise = null;
        reject(new Error('YT timeout'));
      }
    }, READY_TIMEOUT_MS);
  });
  return apiPromise;
}

export function reducedMotion(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
}

export interface TrailerCallbacks {
  onPlaying?: () => void;
  onFail?: () => void;
}

interface Active {
  owner: unknown;
  container: HTMLElement;
  player: YTPlayer | null;
  dead: boolean;
  muted: boolean;
}

let active: Active | null = null;

export function activeOwner(): unknown {
  return active ? active.owner : null;
}

export function stopTrailer(owner?: unknown): void {
  if (!active || (owner !== undefined && active.owner !== owner)) return;
  const a = active;
  active = null;
  a.dead = true;
  try {
    a.player?.destroy();
  } catch {
    /* ignore */
  }
  while (a.container.firstChild) a.container.removeChild(a.container.firstChild);
}

/** Starts a muted autoplaying trailer in `container`, destroying any other active one. */
export function playTrailer(owner: unknown, container: HTMLElement, key: string, cb: TrailerCallbacks = {}): void {
  stopTrailer();
  if (reducedMotion() || !/^[\w-]{6,20}$/.test(key)) {
    cb.onFail?.();
    return;
  }
  const a: Active = { owner, container, player: null, dead: false, muted: true };
  active = a;
  const fail = () => {
    if (a.dead) return;
    stopTrailer(owner);
    cb.onFail?.();
  };
  const timer = setTimeout(fail, READY_TIMEOUT_MS);
  loadApi().then(
    (YT) => {
      if (a.dead) {
        clearTimeout(timer);
        return;
      }
      const mount = document.createElement('div');
      container.appendChild(mount);
      try {
        a.player = new YT.Player(mount, {
          videoId: key,
          playerVars: {
            autoplay: 1,
            mute: 1,
            controls: 0,
            rel: 0,
            playsinline: 1,
            modestbranding: 1,
            disablekb: 1,
            loop: 1,
            playlist: key,
            iv_load_policy: 3,
          },
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
