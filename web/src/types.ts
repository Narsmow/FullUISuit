export interface ItemCard {
  id: string;
  name: string;
  type: string;
  year?: number | null;
  rating?: number | null;
  rated?: string | null;
  overview?: string | null;
  genres: string[];
  runtimeMinutes?: number | null;
  badges: string[];
  trailerKey?: string | null;
  tmdbId?: number | null;
  progress?: number | null;
  hasBackdrop: boolean;
  hasLogo: boolean;
  /** Optional Primary/Backdrop image tag, when the server provides one (enables long-lived image caching). */
  imageTag?: string | null;
  myRating: number;
  inMyList: boolean;
  rank?: number | null;
}

export interface ComingSoonCard {
  tmdbId: number;
  mediaType: string;
  title: string;
  overview?: string | null;
  posterPath?: string | null;
  backdropPath?: string | null;
  releaseDate?: string | null;
  trailerKey?: string | null;
  myVote: number;
}

export interface HomeRow {
  id: string;
  title: string;
  type: string;
  items: ItemCard[];
  comingSoon?: ComingSoonCard[] | null;
}

export interface HomeResponse {
  serverName: string;
  accentColor: string;
  rows: HomeRow[];
}

export interface MyServerResponse {
  continueWatching: ItemCard[];
  myList: ItemCard[];
  wanted: ComingSoonCard[];
}

export interface SearchResponse {
  mode: string;
  items: ItemCard[];
}

export interface NotificationDto {
  id: string;
  text: string;
  at: string;
  read: boolean;
  itemId?: string | null;
}

export interface PluginStatus {
  serverName: string;
  accentColor: string;
  tmdbConfigured?: boolean;
  ollamaEnabled?: boolean;
  /** Admin switch (server setting). Missing means enabled. */
  trailersEnabled?: boolean;
}

export type RouteKind = 'home' | 'shows' | 'movies' | 'myserver' | 'search' | 'native';
export interface Route {
  kind: RouteKind;
  q: string;
}
