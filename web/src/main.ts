import './style.css';
import { fetchStatus } from './api';
import { mountBanner } from './banner';

async function boot(): Promise<void> {
  const status = await fetchStatus();
  if (status) mountBanner(status);
}

// jellyfin-web is a SPA; ApiClient appears after startup.
const timer = window.setInterval(() => {
  if (window.ApiClient) {
    window.clearInterval(timer);
    void boot();
  }
}, 500);
