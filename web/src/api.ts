export interface PluginStatus {
  serverName: string;
  accentColor: string;
  tmdbConfigured: boolean;
  ollamaEnabled: boolean;
}

interface JellyfinApiClient {
  getUrl(path: string): string;
  accessToken(): string;
}

declare global {
  interface Window {
    ApiClient?: JellyfinApiClient;
  }
}

export async function fetchStatus(client = window.ApiClient): Promise<PluginStatus | null> {
  if (!client) return null;
  const res = await fetch(client.getUrl('FullUI/Status'), {
    headers: { Authorization: `MediaBrowser Token="${client.accessToken()}"` },
  });
  return res.ok ? ((await res.json()) as PluginStatus) : null;
}
