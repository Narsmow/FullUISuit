import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { EN, lang, localeCandidates, pickLang, refreshLocale, setUserCulture, t, tn } from './i18n';
import { LOCALES } from './locales';
import { formatRelease, timeAgo } from './util';

function setLang(l: string) {
  document.documentElement.lang = l;
  refreshLocale();
}

describe('i18n', () => {
  beforeEach(() => {
    localStorage.clear();
    setUserCulture('');
    setLang('');
  });
  afterEach(() => {
    document.documentElement.lang = '';
    localStorage.clear();
    setUserCulture('');
    refreshLocale();
  });

  it('English base with {placeholders}', () => {
    expect(t('nav.my', { server: 'MowFlix' })).toBe('My MowFlix');
    expect(t('card.match', { n: 97 })).toBe('97% Match');
  });

  it('uses the language jellyfin-web put on <html>, with region tags', () => {
    setLang('es-MX');
    expect(lang()).toBe('es');
    expect(t('nav.home')).toBe('Inicio');
    setLang('de');
    expect(t('common.tryAgain')).toBe('Erneut versuchen');
    setLang('fr');
    expect(t('nav.shows')).toBe('Séries');
  });

  it('falls back through localStorage language, then English', () => {
    localStorage.setItem('language', 'de-DE');
    refreshLocale();
    expect(lang()).toBe('de');
    localStorage.clear();
    setLang('zz'); // unknown language: English
    expect(lang()).toBe('en');
    expect(t('nav.home')).toBe('Home');
  });

  it('candidate order: <html lang>, stored language, user culture, browser', () => {
    document.documentElement.lang = 'fr';
    localStorage.setItem('language', 'es');
    setUserCulture('de');
    const c = localeCandidates();
    expect(c.slice(0, 3)).toEqual(['fr', 'es', 'de']);
    expect(pickLang(['xx', 'de-AT']).lang).toBe('de');
  });

  it('a missing key in a locale falls back to English', () => {
    setLang('es');
    LOCALES.es['nav.home'] = undefined as unknown as string;
    try {
      expect(t('nav.home')).toBe('Home');
    } finally {
      LOCALES.es['nav.home'] = 'Inicio';
    }
  });

  it('plural helper follows the locale', () => {
    expect(tn('assist.nextIn', 1)).toBe('Next episode in 1 second');
    expect(tn('assist.nextIn', 10)).toBe('Next episode in 10 seconds');
    setLang('de');
    expect(tn('assist.nextIn', 1)).toBe('Nächste Folge in 1 Sekunde');
    expect(tn('assist.nextIn', 2)).toBe('Nächste Folge in 2 Sekunden');
  });

  it('dates and relative times use Intl for the locale', () => {
    expect(formatRelease('2027-03-05')).toBe('Mar 5, 2027');
    setLang('de');
    expect(formatRelease('2027-03-05')).toMatch(/5\. März 2027|5\. Mär\.? 2027/);
    setLang('en');
    expect(timeAgo('2020-01-01T00:00:00Z', Date.parse('2020-01-01T03:00:00Z'))).toMatch(/3 hr\. ago|3 hours ago/);
    setLang('fr');
    expect(timeAgo('2020-01-01T00:00:00Z', Date.parse('2020-01-01T03:00:00Z'))).toMatch(/il y a 3\sh/);
    expect(timeAgo('2020-01-01T00:00:10Z', Date.parse('2020-01-01T00:00:20Z'))).toBe("à l'instant");
  });

  const placeholders = (s: string) => (s.match(/\{\w+\}/g) || []).sort().join(',');
  it('every translated id exists in English and keeps the same placeholders', () => {
    const en = EN as Record<string, string>;
    for (const [l, table] of Object.entries(LOCALES)) {
      for (const [id, text] of Object.entries(table)) {
        expect(en[id], `${l}:${id} has no English source`).toBeDefined();
        expect(placeholders(text), `${l}:${id} placeholders`).toBe(placeholders(en[id]));
      }
    }
  });

  it('the main UI strings are translated in es, fr and de', () => {
    const exempt = new Set(['nav.defaultName', 'tmdb.logo', 'card.top10Glyph', 'time.h', 'time.m', 'time.hm']);
    const main = Object.keys(EN).filter((k) => !exempt.has(k));
    for (const l of ['es', 'fr', 'de']) {
      const missing = main.filter((k) => !(k in LOCALES[l]));
      expect(missing, `${l} is missing`).toEqual([]);
    }
  });
});

// ---- dev check: no user-visible literal may stay in component code --------------------------------
describe('no hard-coded user-visible strings in components', () => {
  const dir = __dirname;
  const files = readdirSync(dir).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts') && !['i18n.ts', 'locales.ts', 'types.ts'].includes(f));
  const rules: Array<[string, RegExp]> = [
    ['text/title/placeholder/alt property', /\b(?:text|title|placeholder|alt)\s*:\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
    ['aria-label', /['"]aria-label['"]\s*:\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
    ['textContent assignment', /\.textContent\s*=\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
    ['setAttribute label/title', /setAttribute\(\s*['"](?:aria-label|title|placeholder)['"]\s*,\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
    ['literal child after icon()', /icon\([^)]*\)\s*,\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
    ['.title assignment', /\.title\s*=\s*(['"`])(?:(?!\1).)*[A-Za-z]{2}(?:(?!\1).)*\1/],
  ];
  it('finds the source files', () => {
    expect(files.length).toBeGreaterThan(8);
  });
  for (const f of files) {
    it(f, () => {
      const lines = readFileSync(join(dir, f), 'utf8').split('\n');
      const bad: string[] = [];
      lines.forEach((line, i) => {
        if (/^\s*(\/\/|\*|\/\*)/.test(line) || line.includes('i18n-ok')) return;
        for (const [name, re] of rules) if (re.test(line)) bad.push(`${f}:${i + 1} (${name}): ${line.trim()}`);
      });
      expect(bad).toEqual([]);
    });
  }
});
