import { describe, expect, it } from 'vitest';
import { mountBanner } from './banner';

describe('mountBanner', () => {
  it('mounts once and updates text', () => {
    const s = { serverName: 'MowFlix', accentColor: '#e50914', tmdbConfigured: false, ollamaEnabled: false };
    mountBanner(s);
    mountBanner({ ...s, serverName: 'Other' });
    expect(document.querySelectorAll('#fullui-banner')).toHaveLength(1);
    expect(document.getElementById('fullui-banner')!.textContent).toBe('Other UI active');
  });
});
