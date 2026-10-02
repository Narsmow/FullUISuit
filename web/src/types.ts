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
  /** 1..99 calibrated match, when the server knows it (cold start: absent). */
  matchPercent?: number | null;
  /** Short plain-English why, e.g. "Because you watched Dark". */
  reason?: string | null;
  /** Continue Watching label such as "S2:E5" (movies: absent). */
  seriesLabel?: string | null;
  /** Minutes left for resumable titles. */
  minutesLeft?: number | null;
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

export interface SearchGroup {
  /** 'people' | 'genres' | 'titles' (anything else is shown with its own label) */
  type: string;
  label?: string | null;
  items: ItemCard[];
}

export interface SearchResponse {
  mode: string;
  items: ItemCard[];
  /** Optional grouped results (people / genres), when the server provides them. */
  groups?: SearchGroup[] | null;
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
  /** Admin switch for the player assist (skip intro / next episode). Missing means enabled. */
  playerAssistEnabled?: boolean;
  /** Present when the server wants the TMDB attribution shown: true, a sentence, or { text }. */
  tmdbAttribution?: boolean | string | { text?: string } | null;
}

export type RouteKind = 'home' | 'shows' | 'movies' | 'myserver' | 'search' | 'row' | 'native';
export interface Route {
  kind: RouteKind;
  q: string;
}
