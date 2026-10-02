// Tiny DOM builder. Strings are only ever added as text nodes / attributes (never innerHTML),
// because titles and overviews coming from the server are untrusted.

type Child = Node | string | null | undefined | false;
export type Props = {
  class?: string;
  text?: string;
  on?: Partial<{ [K in keyof HTMLElementEventMap]: (e: HTMLElementEventMap[K]) => void }>;
  data?: Record<string, string>;
  [attr: string]: unknown;
};

export function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: Props = {},
  ...children: Child[]
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null || v === false) continue;
    if (k === 'class') el.className = String(v);
    else if (k === 'text') el.textContent = String(v);
    else if (k === 'on') {
      for (const [ev, fn] of Object.entries(v as Record<string, EventListener>)) el.addEventListener(ev, fn);
    } else if (k === 'data') {
      for (const [dk, dv] of Object.entries(v as Record<string, string>)) el.dataset[dk] = dv;
    } else el.setAttribute(k, v === true ? '' : String(v));
  }
  append(el, children);
  return el;
}

export function append(el: Element, children: Child[]): void {
  for (const c of children) {
    if (c === null || c === undefined || c === false) continue;
    el.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
  }
}

export function clear(el: Element): void {
  while (el.firstChild) el.removeChild(el.firstChild);
}

const SVG_NS = 'http://www.w3.org/2000/svg';

/** Icon from a fixed, trusted path string (never server data). */
export function icon(path: string, cls = 'fui-icon'): SVGSVGElement {
  const svg = document.createElementNS(SVG_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('class', cls);
  svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(SVG_NS, 'path');
  p.setAttribute('d', path);
  p.setAttribute('fill', 'currentColor');
  svg.appendChild(p);
  return svg;
}

export const ICONS = {
  play: 'M8 5v14l11-7z',
  info: 'M11 7h2v2h-2zm0 4h2v6h-2zm1-9a10 10 0 100 20 10 10 0 000-20z',
  plus: 'M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6z',
  check: 'M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z',
  up: 'M1 21h4V9H1v12zm22-11c0-1.1-.9-2-2-2h-6.3l1-4.6v-.3c0-.4-.2-.8-.4-1.1L14.2 1 7.6 7.6C7.2 8 7 8.500 7 9v10c0 1.100.900 2 2 2h9c.8 0 1.500-.5 1.800-1.200l3-7c.1-.2.2-.5.2-.8v-2z',
  down: 'M15 3H6c-.8 0-1.500.5-1.800 1.200l-3 7c-.1.200-.2.500-.2.800v2c0 1.100.900 2 2 2h6.300l-1 4.600v.3c0 .4.2.8.4 1.100l1.100 1 6.600-6.600c.4-.4.600-.9.600-1.400V5c0-1.100-.900-2-2-2zm4 0v12h4V3h-4z',
  bell: 'M12 22c1.100 0 2-.9 2-2h-4c0 1.100.9 2 2 2zm6-6v-5c0-3.100-1.600-5.600-4.500-6.300V4c0-.8-.7-1.500-1.500-1.500s-1.500.7-1.500 1.500v.7C7.600 5.400 6 7.900 6 11v5l-2 2v1h16v-1l-2-2z',
  search: 'M10 4a6 6 0 104.5 10l5 5 1.5-1.5-5-5A6 6 0 0010 4zm0 2a4 4 0 110 8 4 4 0 010-8z',
  mute: 'M3 9v6h4l5 5V4L7 9H3zm13.3-.1l-1.4 1.4 1.7 1.7-1.7 1.7 1.4 1.4 1.7-1.7 1.7 1.7 1.4-1.4-1.7-1.7 1.7-1.7-1.4-1.4-1.7 1.7z',
  unmute: 'M3 9v6h4l5 5V4L7 9H3zm11 3a4 4 0 00-2-3.500v7A4 4 0 0014 12zm-2-8.800v2.100a7 7 0 010 13.400v2.100a9 9 0 000-17.600z',
};
