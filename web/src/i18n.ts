// String table + locale helpers. EVERY user-visible string in the bundle (text, aria-labels, titles,
// placeholders, error texts) lives here under an id; components call t('id'). English is the base and the
// fallback for any missing key. Other languages live in locales.ts.
//
// Placeholders use {name}. Plurals use `<id>.one` / `<id>.other` (plus optional .zero/.two/.few/.many) and tn().

import { LOCALES } from './locales';

export type Vars = Record<string, string | number>;

export const EN = {
  // ---- nav ----
  'nav.home': 'Home',
  'nav.shows': 'Shows',
  'nav.movies': 'Movies',
  'nav.my': 'My {server}',
  'nav.main': 'Main',
  'nav.libraries': 'Libraries',
  'nav.classic': 'Use classic view',
  'nav.classicTitle': 'Switch back to the standard Jellyfin home',
  'nav.search': 'Search',
  'nav.notifications': 'Notifications',
  'nav.notificationsUnread': 'Notifications, {n} unread',
  'nav.notificationList': 'Notification list',
  'nav.markAllRead': 'Mark all read',
  'nav.notificationsFailed': "We couldn't load your notifications.",
  'nav.notificationsEmpty': 'No notifications yet.',
  'nav.profile': 'Profile menu',
  'nav.settings': 'Settings',
  'nav.dashboard': 'Dashboard',
  'nav.signOut': 'Sign out',
  'lib.movies': 'Movies library',
  'lib.tv': 'TV Shows library',
  'lib.music': 'Music',
  'lib.livetv': 'Live TV',
  'lib.favorites': 'Favorites',
  'nav.defaultName': 'FullUI',
  'nav.serverFallback': 'Server',

  // ---- common ----
  'common.tryAgain': 'Try again',
  'common.useClassic': 'Use classic view',
  'common.loading': 'Loading',
  'common.play': 'Play',
  'common.moreInfo': 'More Info',
  'common.scrollLeft': 'Scroll left',
  'common.scrollRight': 'Scroll right',

  // ---- banner / errors ----
  'banner.classic': "You're using the classic view.",
  'banner.failed': "The new home couldn't load right now, so you're seeing the classic view.",
  'banner.switchBack': 'Switch to the new view',
  'error.page': "We couldn't load this page. This is usually temporary, so please try again.",
  'error.rowFailed': "This row couldn't be shown.",
  'error.rating': "Couldn't save your rating. Try again.",
  'error.myList': "Couldn't update My List. Try again.",
  'error.vote': "Couldn't save your vote. Try again.",
  'error.search': "Search isn't working right now. Please try again in a moment.",

  // ---- pages ----
  'page.emptyTitles': 'Nothing to show here yet. Check back soon for titles.',
  'page.emptyShows': 'Nothing to show here yet. Check back soon for shows.',
  'page.emptyMovies': 'Nothing to show here yet. Check back soon for movies.',
  'page.myEmpty': 'Your list is empty. Add titles with the + button, or tell us what you want to see next.',
  'page.continue': 'Continue Watching',
  'page.myList': 'My List',
  'page.wanted': 'I Want This',
  'page.rowNotFound': "We couldn't find that row any more. Head back to Home to see what's new.",
  'page.back': 'Back to Home',

  // ---- rows ----
  'row.exploreAll': 'Explore all',
  'row.exploreAllLabel': 'Explore all: {title}',
  'row.loading': 'Loading {title}',

  // ---- cards ----
  'card.play': 'Play',
  'card.addToList': 'Add to My List',
  'card.removeFromList': 'Remove from My List',
  'card.notForMe': 'Not for me',
  'card.like': 'I like this',
  'card.love': 'Love this',
  'card.rate': 'Rate',
  'card.moreInfo': 'More info',
  'card.match': '{n}% Match',
  'card.top10Glyph': 'TOP 10',
  'card.top10Today': '#{n} today',
  'card.rankedName': '{name}, number {n} today',
  'card.timeLeft': '{time} left',
  'soon.want': 'I want this',
  'soon.nope': 'Not for me',
  'soon.comingSoon': 'Coming soon',

  // ---- hero ----
  'hero.pause': 'Pause trailer',
  'hero.replay': 'Replay trailer',
  'hero.mute': 'Mute trailer',
  'hero.unmute': 'Unmute trailer',
  'hero.muteTitle': 'Mute',
  'hero.unmuteTitle': 'Unmute',
  'hero.trailer': 'Trailer',

  // ---- time ----
  'time.justNow': 'just now',
  'time.h': '{h}h',
  'time.m': '{m}m',
  'time.hm': '{h}h {m}m',

  // ---- search ----
  'search.placeholderKeyword': 'Search by title or keyword',
  'search.placeholderPeople': 'Search by title, person, or genre',
  'search.placeholderAi': 'Search titles, genres, or describe what you feel like watching',
  'search.label': 'Search',
  'search.keepTyping': 'Keep typing...',
  'search.prompt': 'Type to search your library.',
  'search.searching': 'Searching...',
  'search.noResults': 'No results for "{q}".',
  'search.modeSemantic': 'Smart search (matches meaning, not just words)',
  'search.modeKeyword': 'Keyword search',
  'search.recent': 'Recent searches',
  'search.clearRecent': 'Clear',
  'search.removeRecent': 'Remove {q} from recent searches',
  'search.try': 'Try:',
  'search.mightLike': 'You might like',
  'search.noResultsHint': 'Check the spelling or try a shorter word.',
  'search.groupPeople': 'People',
  'search.groupGenres': 'Genres',
  'search.groupTitles': 'Titles',

  // ---- TMDB ----
  'tmdb.sentence': 'This product uses the TMDB API but is not endorsed or certified by TMDB.',
  'tmdb.logo': 'TMDB',

  // ---- player assist ----
  'assist.skipIntro': 'Skip Intro',
  'assist.skipRecap': 'Skip Recap',
  'assist.skipCredits': 'Skip Credits',
  'assist.skipPreview': 'Skip Preview',
  'assist.nextIn.one': 'Next episode in {n} second',
  'assist.nextIn.other': 'Next episode in {n} seconds',
  'assist.nextEpisode': 'Next episode',
  'assist.playNow': 'Play now',
  'assist.cancel': 'Cancel',
  'assist.region': 'Playback shortcuts',
} as const;

export type MsgId = keyof typeof EN;
export type Table = Partial<Record<string, string>>;

const tables: Record<string, Table> = { en: EN, ...LOCALES };

/** Languages with a table (besides English); registered by locales.ts. */
export function registerLocale(lang: string, table: Table): void {
  tables[lang] = table;
}

// ---- locale detection ---------------------------------------------------------------------
let cachedTag = '';
let cachedLang = 'en';

function norm(tag: string | null | undefined): string {
  return (tag || '').trim().replace(/_/g, '-');
}

/** Candidates, best first: jellyfin-web's own choice, then the browser. */
export function localeCandidates(): string[] {
  const out: string[] = [];
  const push = (v: unknown) => {
    const s = typeof v === 'string' ? norm(v) : '';
    if (s) out.push(s);
  };
  try {
    push(document.documentElement.lang); // jellyfin-web's globalize sets <html lang>
  } catch {
    /* ignore */
  }
  try {
    push(localStorage.getItem('language')); // legacy/global setting
    const c = window.ApiClient;
    const uid = c && c.getCurrentUserId ? c.getCurrentUserId() : '';
    if (uid) push(localStorage.getItem(`${uid}-language`)); // per-user display language
  } catch {
    /* ignore */
  }
  push(userCulture);
  try {
    push(navigator.language);
  } catch {
    /* ignore */
  }
  return out;
}

let userCulture = '';
/** Optional: a culture string from the signed-in user's profile/display preferences. */
export function setUserCulture(c: string | null | undefined): void {
  userCulture = norm(c);
  refreshLocale();
}

export function pickLang(candidates: string[]): { tag: string; lang: string } {
  for (const c of candidates) {
    const lang = c.split('-')[0].toLowerCase();
    if (tables[lang]) {
      try {
        Intl.DateTimeFormat.supportedLocalesOf(c);
        return { tag: c, lang };
      } catch {
        return { tag: lang, lang };
      }
    }
  }
  return { tag: candidates[0] || 'en', lang: 'en' };
}

/** Re-read the locale (call on login, language change, page render). Cheap. */
export function refreshLocale(): void {
  const r = pickLang(localeCandidates());
  cachedTag = r.tag;
  cachedLang = r.lang;
}

export function lang(): string {
  if (!cachedTag) refreshLocale();
  return cachedLang;
}

/** BCP 47 tag for Intl (falls back to 'en'). */
export function locale(): string {
  if (!cachedTag) refreshLocale();
  try {
    Intl.DateTimeFormat.supportedLocalesOf(cachedTag);
    return cachedTag;
  } catch {
    return 'en';
  }
}

// ---- lookup ---------------------------------------------------------------------------------
function fill(s: string, vars?: Vars): string {
  return vars ? s.replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m)) : s;
}

function lookup(id: string): string {
  const l = lang();
  const own = tables[l] && tables[l][id];
  if (own !== undefined) return own;
  return (EN as Record<string, string>)[id] ?? id; // missing everywhere: show the id rather than nothing
}

export function t(id: MsgId, vars?: Vars): string {
  return fill(lookup(id), vars);
}

/** Plural-aware lookup: uses `<id>.<category>` per Intl.PluralRules, falling back to `.other`. */
export function tn(id: string, n: number, vars?: Vars): string {
  let cat = 'other';
  try {
    cat = new Intl.PluralRules(locale()).select(n);
  } catch {
    /* keep other */
  }
  const l = lang();
  const own = tables[l] || {};
  const s = own[`${id}.${cat}`] ?? own[`${id}.other`] ?? (EN as Record<string, string>)[`${id}.${cat}`] ?? (EN as Record<string, string>)[`${id}.other`] ?? id;
  return fill(s, { n, ...vars });
}

// ---- Intl-based formatting ------------------------------------------------------------------
export function formatDate(d: Date, opts: Intl.DateTimeFormatOptions): string {
  try {
    return new Intl.DateTimeFormat(locale(), opts).format(d);
  } catch {
    return new Intl.DateTimeFormat('en', opts).format(d);
  }
}

export function formatRelative(seconds: number): string {
  // seconds > 0 means "in the past"; returns e.g. "5 minutes ago"
  const abs = Math.abs(seconds);
  let value: number;
  let unit: Intl.RelativeTimeFormatUnit;
  if (abs < 3600) {
    value = Math.floor(abs / 60);
    unit = 'minute';
  } else if (abs < 86400) {
    value = Math.floor(abs / 3600);
    unit = 'hour';
  } else {
    value = Math.floor(abs / 86400);
    unit = 'day';
  }
  const signed = seconds >= 0 ? -value : value;
  try {
    return new Intl.RelativeTimeFormat(locale(), { numeric: 'auto', style: 'short' }).format(signed, unit);
  } catch {
    return new Intl.RelativeTimeFormat('en', { numeric: 'auto', style: 'short' }).format(signed, unit);
  }
}
