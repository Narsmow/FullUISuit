// TMDB attribution for Coming Soon rows/pages. The sentence is always shown next to TMDB data;
// the logo is added when the server's Status says `tmdbAttribution` (true, a sentence, or { text }).
// The logo is inline SVG (never hotlinked). NOTE: this is a plain wordmark stand-in; swap it for TMDB's
// official artwork once the owner has agreed to its brand terms.
import { h } from './dom';
import { t } from './i18n';
import type { PluginStatus } from './types';

let attr: PluginStatus['tmdbAttribution'] = null;

export function setAttribution(v: PluginStatus['tmdbAttribution'] | undefined): void {
  attr = v ?? null;
}

export function attributionSentence(v: PluginStatus['tmdbAttribution'] = attr): string {
  if (typeof v === 'string' && v.trim()) return v.trim();
  if (v && typeof v === 'object' && typeof v.text === 'string' && v.text.trim()) return v.text.trim();
  return t('tmdb.sentence');
}

const NS = 'http://www.w3.org/2000/svg';

function el(name: string, attrs: Record<string, string>): SVGElement {
  const e = document.createElementNS(NS, name);
  for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
  return e;
}

function logo(): SVGSVGElement {
  const svg = el('svg', { viewBox: '0 0 64 24', class: 'fui-tmdb-logo', role: 'img', 'aria-label': t('tmdb.logo') }) as SVGSVGElement;
  const grad = el('linearGradient', { id: 'fui-tmdb-g', x1: '0', x2: '1' });
  grad.append(el('stop', { offset: '0', 'stop-color': '#90cea1' }), el('stop', { offset: '1', 'stop-color': '#01b4e4' }));
  const text = el('text', {
    x: '32',
    y: '17',
    'text-anchor': 'middle',
    'font-family': 'Arial, Helvetica, sans-serif',
    'font-size': '14',
    'font-weight': '800',
    fill: '#0d253f',
  });
  text.textContent = t('tmdb.logo');
  svg.append(grad, el('rect', { width: '64', height: '24', rx: '12', fill: 'url(#fui-tmdb-g)' }), text);
  return svg;
}

/** Small footer for Coming Soon rows/pages. */
export function createAttribution(): HTMLElement {
  return h('p', { class: 'fui-tmdb' }, attr ? logo() : null, h('span', { text: attributionSentence() }));
}
