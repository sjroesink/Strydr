export type Activity = 'running' | 'walking' | 'cycling';
export type Location = { latitude: number; longitude: number; elevationM?: number | null };
export type Place = { label: string; location: Location };
export type RouteRequest = {
  start: Location; end: Location | null; radiusKm: number | null;
  targetDistanceKm: number; activity: Activity; candidateCount: number;
  preferGreen?: boolean; preferUnpaved?: boolean; avoidBacktracking?: boolean;
  distanceFlexibilityPercent?: number;
  drawnRouteId?: string | null;
  isEditedRoute?: boolean;
};
export type RoutePreviewRequest = {
  start: Location; points: Location[]; activity: Activity; preferGreen: boolean; preferUnpaved: boolean;
};
export type EndpointInfo = { requested: Location; snapped: Location; snapDistanceM: number };
export type RouteCandidate = {
  id: string; expiresAt: string | null; searchId?: string | null; geometry: { type: 'LineString'; coordinates: number[][] };
  distanceKm: number; elevationGainM: number | null; maxDistanceFromStartKm: number; score: number;
  status: 'qualified' | 'bestEffort' | 'diagnostic'; start: EndpointInfo; end: EndpointInfo;
  warnings: RouteWarning[];
  greenScore?: number | null; unpavedPercent?: number | null; surfaceKnownPercent?: number | null;
  woodlandPercent?: number | null;
  snappedPoints?: Location[] | null;
};
export type Suggestion = { id: string; label: string; location: Location };
export type SavedRouteSearch = {
  id: string; createdAt: string; request: RouteRequest; candidates: RouteCandidate[];
  drawnPoints: Location[]; drawnPreview: RouteCandidate | null;
};
export type RouteEditSession = { preview: RouteCandidate; request: RouteRequest; points: Location[]; isLoop: boolean };
export type RouteWarning = { code: string; message: string; params?: Record<string, number> | null };
export type ApiProblem = {
  code?: string; params?: Record<string, number>;
  title?: string; detail?: string; status?: number; errors?: Record<string, string[]>;
  minimumDistanceKm?: number; traceId?: string;
};

export type ServiceKind = 'garmin';
export type ServiceConnectionStatus = { service: ServiceKind; connected: boolean };
export type SentRoute = { externalId: string; viewUrl: string | null };
export type SentRouteRecord = SentRoute & { routeId: string; service: ServiceKind };
