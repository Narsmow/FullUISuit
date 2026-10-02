import type { PluginStatus } from './api';

const ID = 'fullui-banner';

/** Idempotent: safe to call on every SPA navigation. */
export function mountBanner(status: PluginStatus, doc: Document = document): HTMLElement {
  let el = doc.getElementById(ID);
  if (!el) {
    el = doc.createElement('div');
    el.id = ID;
    doc.body.appendChild(el);
  }
  el.textContent = `${status.serverName} UI active`;
  el.style.setProperty('--fullui-accent', status.accentColor);
  return el;
}
