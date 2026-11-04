import { useEffect, useLayoutEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { ApiError, connectUrl, downloadFit, downloadGpx, editRoute, generate, loadSearch, previewRoute, saveRoute, sendRoute } from './api';
import { LocationSearch } from './components/LocationSearch';
import RouteMap, { ROUTE_COLORS, type MapMarkerTarget, type MapPadding } from './components/RouteMap';
import type { Activity, Location, Place, RouteCandidate, ServiceKind } from './types';
import { ConnectionsPanel } from './components/ConnectionsPanel';
import { readConnectReturn } from './connectReturn';
import { SERVICE_NAMES, useConnections } from './useConnections';
import { insertionIndex as findInsertionIndex } from './routeEditing';
import { Icon } from './components/Icon';
import { useTheme, type ThemePreference } from './useTheme';
import { I18nProvider, useI18n, type Message, type MessageKey } from './i18n';
import { MessageError, failureMessages, warningMessage } from './apiText';

const MAX_DISTANCE = Number(import.meta.env.VITE_MAX_DISTANCE_KM) || 60;
const QUICK_DISTANCES = [5, 8, 10, 15, 21.1];
const EDIT_HINT_KEY = 'meander.editHintDismissed';
const coordinateLabel = (point: Location) => `${point.latitude.toFixed(5)}, ${point.longitude.toFixed(5)}`;
const hiddenRouteWarningCodes = new Set(['backtrackingRemoved', 'surfaceDataIncomplete', 'distanceMiss', 'qualityDataUnavailable', 'woodlandUnavailable']);
const savedSearchPath = (id: string, routeId: string) => `/routes/${encodeURIComponent(id)}?route=${encodeURIComponent(routeId)}`;
const drawingKey = (start: Location, points: Location[], activity: Activity, green: boolean, unpaved: boolean) =>
  JSON.stringify([start, points, activity, green, unpaved]);
type DrawingSnapshot = {
  start: Place | null; end: Place | null; startText: string; endText: string; drawnPoints: Location[];
  completeDrawing: boolean; preview: RouteCandidate | null; previewKey: string | null;
};
// The wizard asks one thing per step. Editing a complete route is its own full-map view.
type Step = 'start' | 'distance' | 'preferences' | 'routes';
const QUESTION_STEPS: { step: Step; label: MessageKey }[] = [
  { step: 'start', label: 'step.start' }, { step: 'distance', label: 'step.distance' }, { step: 'preferences', label: 'step.preferences' },
];
const noPadding: MapPadding = { top: 0, right: 0, bottom: 0, left: 0 };

function showRouteWarning(warning: RouteCandidate['warnings'][number]) {
  return !hiddenRouteWarningCodes.has(warning.code) && !(warning.code === 'providerWarning' &&
    warning.message.toLowerCase().includes('extra info requested but not available: roadaccessrestrictions'));
}

function PathIcon({ small = false }: { small?: boolean }) {
  return <svg width={small ? 24 : 40} height={small ? 24 : 40} viewBox="0 0 40 40" fill="none" aria-hidden="true">
    <path d="M7 30V15c0-8 7-9 10-3l3 6 4-7c4-5 10-3 10 4v15" stroke="currentColor" strokeWidth="3.5" strokeLinecap="round" strokeLinejoin="round" />
    <circle cx="7" cy="31" r="3" fill="currentColor" />
  </svg>;
}

function readHintDismissed() {
  try { return window.localStorage.getItem(EDIT_HINT_KEY) === '1'; } catch { return false; }
}

// Keeps the map camera clear of whatever floats over it: the planner sheet or
// panel, and the editing bars. Overlays declare which edge they cover.
function useMapPadding(deps: unknown[]) {
  const [padding, setPadding] = useState<MapPadding>(noPadding);
  useLayoutEffect(() => {
    const overlays = [...document.querySelectorAll<HTMLElement>('[data-map-overlay]')];
    function measure() {
      const next = { ...noPadding };
      const height = window.innerHeight;
      for (const element of overlays) {
        const rect = element.getBoundingClientRect();
        if (!rect.width || !rect.height) continue;
        const wide = window.matchMedia('(min-width: 760px)').matches;
        const kind = element.dataset.mapOverlay;
        // On wide screens the planner is a side panel that also holds the top bar.
        const side = kind === 'sheet' ? wide ? 'left' : 'bottom' : kind === 'topbar' ? wide ? null : 'top' : kind;
        if (side === 'top') next.top = Math.max(next.top, Math.round(rect.bottom));
        if (side === 'bottom') next.bottom = Math.max(next.bottom, Math.round(height - rect.top));
        if (side === 'left') next.left = Math.max(next.left, Math.round(rect.right));
      }
      setPadding(current => current.top === next.top && current.bottom === next.bottom && current.left === next.left
        && current.right === next.right ? current : next);
    }
    measure();
    const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(measure);
    overlays.forEach(element => observer?.observe(element));
    window.addEventListener('resize', measure);
    return () => { observer?.disconnect(); window.removeEventListener('resize', measure); };
  }, deps);
  return padding;
}

// On phones the planner sheet follows a vertical swipe: down to step back for
// the map, up to bring it back. Content scrolling keeps priority over the swipe.
function useSheetSwipe(collapsed: boolean, setCollapsed: (collapsed: boolean) => void) {
  const sheet = useRef<HTMLElement>(null);
  const swipe = useRef<{ x: number; y: number; dy: number; active: boolean } | null>(null);
  const COLLAPSED_HEIGHT = 140;

  function reset() {
    const element = sheet.current;
    if (!element) return;
    element.classList.remove('is-swiping');
    element.style.transform = ''; element.style.maxHeight = '';
  }
  function onTouchStart(event: React.TouchEvent) {
    swipe.current = null;
    const element = sheet.current;
    if (!element || event.touches.length !== 1 || window.matchMedia('(min-width: 760px)').matches) return;
    const target = event.target as Element;
    if (target.closest('input[type="range"]')) return;
    const body = element.querySelector('.sheet-body');
    const fromGrabber = target.closest('.sheet-grabber') !== null;
    if (!fromGrabber && !collapsed && (body?.scrollTop ?? 0) > 0) return;
    const touch = event.touches[0];
    swipe.current = { x: touch.clientX, y: touch.clientY, dy: 0, active: false };
  }
  function onTouchMove(event: React.TouchEvent) {
    const state = swipe.current, element = sheet.current;
    if (!state || !element) return;
    const touch = event.touches[0];
    const dx = touch.clientX - state.x, dy = touch.clientY - state.y;
    if (!state.active) {
      if (Math.abs(dx) < 8 && Math.abs(dy) < 8) return;
      // Sideways swipes belong to the route carousel, the other direction to scrolling.
      if (Math.abs(dx) > Math.abs(dy) || (collapsed ? dy > 0 : dy < 0)) { swipe.current = null; return; }
      state.active = true;
      element.classList.add('is-swiping');
    }
    state.dy = dy;
    if (collapsed) element.style.maxHeight = `${COLLAPSED_HEIGHT - Math.min(0, dy)}px`;
    else element.style.transform = `translateY(${Math.max(0, dy)}px)`;
  }
  function onTouchEnd() {
    const state = swipe.current;
    swipe.current = null;
    if (!state?.active) return;
    reset();
    if (collapsed ? state.dy < -50 : state.dy > 70) setCollapsed(!collapsed);
  }
  return { ref: sheet, onTouchStart, onTouchMove, onTouchEnd, onTouchCancel: onTouchEnd };
}

function Planner() {
  const { t, say, number, language, setLanguage } = useI18n();
  const { preference: themePreference, colorScheme, changePreference } = useTheme();
  const [step, setStep] = useState<Step>('start');
  const [sheetCollapsed, setSheetCollapsed] = useState(false);
  const sheetSwipe = useSheetSwipe(sheetCollapsed, setSheetCollapsed);
  const [hintDismissed, setHintDismissed] = useState(readHintDismissed);
  const [fitRequest, setFitRequest] = useState(0);
  const [start, setStart] = useState<Place | null>(null);
  const [end, setEnd] = useState<Place | null>(null);
  const [startText, setStartText] = useState('');
  const [endText, setEndText] = useState('');
  const [hasEnd, setHasEnd] = useState(false);
  const [mode, setMode] = useState<'browse' | 'start' | 'end' | 'draw'>('start');
  const [drawnPoints, setDrawnPoints] = useState<Location[]>([]);
  const [completeDrawing, setCompleteDrawing] = useState(false);
  const [isEditingRoute, setIsEditingRoute] = useState(false);
  const [selectedPoint, setSelectedPoint] = useState<number | null>(null);
  const [editPhase, setEditPhase] = useState<'idle' | 'opening' | 'saving'>('idle');
  const editController = useRef<AbortController | null>(null);
  const [draft, setDraft] = useState<RouteCandidate | null>(null);
  const [lastGoodDraft, setLastGoodDraft] = useState<RouteCandidate | null>(null);
  const [draftPhase, setDraftPhase] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [draftError, setDraftError] = useState<Message[]>([]);
  const [draftRevision, setDraftRevision] = useState(0);
  const [activity, setActivity] = useState<Activity>('running');
  const [distance, setDistance] = useState('5');
  const [hasRadius, setHasRadius] = useState(false);
  const [radius, setRadius] = useState('2');
  const [hasFlexibleDistance, setHasFlexibleDistance] = useState(false);
  const [distanceMargin, setDistanceMargin] = useState('15');
  const [preferGreen, setPreferGreen] = useState(false);
  const [preferUnpaved, setPreferUnpaved] = useState(false);
  const [candidates, setCandidates] = useState<RouteCandidate[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [highlightedId, setHighlightedId] = useState<string | null>(null);
  const [phase, setPhase] = useState<'idle' | 'loading' | 'done'>('idle');
  const [error, setError] = useState<Message[]>([]);
  const [notice, setNotice] = useState<Message | null>(null);
  const [locating, setLocating] = useState(false);
  const [exporting, setExporting] = useState<{ id: string; format: 'gpx' | 'fit' } | null>(null);
  const { connections, setConnected, disconnect } = useConnections();
  const [sending, setSending] = useState<string | null>(null);
  // Read before the saved-search restore rewrites the URL.
  const [connectReturn, setConnectReturn] = useState(() => readConnectReturn(window.location.search));
  const [restoring, setRestoring] = useState(false);
  const generation = useRef<AbortController | null>(null);
  const restoration = useRef<AbortController | null>(null);
  const reusableDraft = useRef<{ key: string; candidate: RouteCandidate } | null>(null);
  const exportingController = useRef<AbortController | null>(null);
  const revision = useRef(0);
  const geoRevision = useRef(0);
  const resultsHeading = useRef<HTMLHeadingElement>(null);
  const stepHeading = useRef<HTMLHeadingElement>(null);
  const carousel = useRef<HTMLUListElement>(null);
  const drawingRef = useRef<HTMLElement>(null);
  const carouselTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const drawingHistory = useRef<DrawingSnapshot[]>([]);
  const [hasDrawingHistory, setHasDrawingHistory] = useState(false);
  const markerDragging = useRef(false);
  const undoDrawingAction = useRef<() => boolean>(() => false);
  const deletePointAction = useRef<() => boolean>(() => false);
  undoDrawingAction.current = undoDrawnPoint;
  deletePointAction.current = () => {
    if (selectedPoint === null || !drawnPoints[selectedPoint] || markerDragging.current || restoring) return false;
    removePoint(selectedPoint);
    return true;
  };

  // Results that were cleared by a change fall back to the last question.
  const view: Step | 'edit' = isEditingRoute ? 'edit'
    : step === 'routes' && phase === 'idle' && candidates.length === 0 ? 'preferences' : step;
  const padding = useMapPadding([view, sheetCollapsed, mode === 'browse']);

  useEffect(() => {
    function onKeyDown(event: KeyboardEvent) {
      if (event.defaultPrevented || event.isComposing) return;
      if (event.target instanceof Element && event.target.closest('input, textarea, select, [contenteditable]:not([contenteditable="false"]), [role="textbox"]')) return;
      if (event.key === 'Delete' && !event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey) {
        if (deletePointAction.current()) event.preventDefault();
        return;
      }
      if (event.altKey || event.shiftKey || !(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== 'z') return;
      if (undoDrawingAction.current()) event.preventDefault();
    }
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  useEffect(() => {
    function openSavedSearch() {
      resetDrawingHistory();
      setLastGoodDraft(null);
      const id = /^\/routes\/([^/]+)\/?$/.exec(window.location.pathname)?.[1];
      if (!id) {
        invalidate(); setDrawnPoints([]); setCompleteDrawing(false); setIsEditingRoute(false); setDraft(null); setMode('start'); setStep('start');
        return;
      }
      invalidate(); setRestoring(true); setPhase('loading'); setMode('browse'); setStep('routes');
      const controller = new AbortController(); restoration.current = controller;
      const current = revision.current;
      void loadSearch(id, controller.signal).then(saved => {
        if (controller.signal.aborted || current !== revision.current) return;
        const request = saved.request;
        if (!request.start || saved.candidates.length === 0) throw new MessageError({ key: 'error.savedNoRoutes' });
        const fullPoints = saved.drawnPoints ?? [];
        const points = request.isEditedRoute ? fullPoints.slice(0, -1) : fullPoints;
        reusableDraft.current = saved.drawnPreview ? {
          key: drawingKey(request.start, fullPoints, request.activity, !!request.preferGreen, !!request.preferUnpaved),
          candidate: saved.drawnPreview,
        } : null;
        const startPlace = { label: coordinateLabel(request.start), location: request.start };
        const endPlace = request.end ? { label: coordinateLabel(request.end), location: request.end } : null;
        setStart(startPlace); setStartText(startPlace.label);
        setEnd(endPlace); setEndText(endPlace?.label ?? ''); setHasEnd(!!endPlace);
        setActivity(request.activity); setDistance(String(request.targetDistanceKm));
        setHasRadius(request.radiusKm !== null); setRadius(String(request.radiusKm ?? 2));
        setHasFlexibleDistance(!!request.distanceFlexibilityPercent); setDistanceMargin(String(request.distanceFlexibilityPercent || 15));
        setPreferGreen(!!request.preferGreen); setPreferUnpaved(!!request.preferUnpaved);
        setDrawnPoints(points); setDraft(saved.drawnPreview); setLastGoodDraft(saved.drawnPreview);
        setCompleteDrawing(!!request.isEditedRoute); setIsEditingRoute(false);
        setDraftPhase(saved.drawnPreview ? 'ready' : 'idle'); setDraftError([]);
        setCandidates(saved.candidates);
        const routeId = new URLSearchParams(window.location.search).get('route');
        const selected = saved.candidates.find(route => route.id === routeId) ?? saved.candidates[0];
        setSelectedId(selected.id); setPhase('done'); setRestoring(false); setStep('routes');
        window.history.replaceState(null, '', savedSearchPath(saved.id, selected.id));
      }).catch((failure: unknown) => {
        if (controller.signal.aborted || current !== revision.current) return;
        setRestoring(false); setPhase('idle'); setDrawnPoints([]); setDraft(null); setStep('start'); setMode('start');
        setError(failureMessages(failure, 'error.openSaved', number));
      });
    }
    if (window.location.pathname.startsWith('/routes/')) openSavedSearch();
    window.addEventListener('popstate', openSavedSearch);
    return () => {
      window.removeEventListener('popstate', openSavedSearch);
      generation.current?.abort(); restoration.current?.abort(); exportingController.current?.abort(); editController.current?.abort(); geoRevision.current++;
    };
  }, []);

  useEffect(() => {
    if (restoring) return;
    setDraft(null); setDraftError([]);
    if (!start || drawnPoints.length === 0 && !completeDrawing) { setLastGoodDraft(null); setDraftPhase('idle'); return; }
    if (completeDrawing && !hasEnd && drawnPoints.length === 0) {
      setDraftPhase('error'); setDraftError([{ key: 'error.addLoopPoint' }]); return;
    }
    const points = completeDrawing ? [...drawnPoints, hasEnd && end ? end.location : start.location] : drawnPoints;
    const key = drawingKey(start.location, points, activity, preferGreen, preferUnpaved);
    const cached = reusableDraft.current;
    if (cached?.key === key && (!cached.candidate.expiresAt || Date.parse(cached.candidate.expiresAt) > Date.now())) {
      setDraft(cached.candidate); setLastGoodDraft(cached.candidate); setDraftPhase('ready'); return;
    }
    const controller = new AbortController();
    setDraftPhase('loading');
    const timer = setTimeout(() => {
      void previewRoute({ start: start.location, points, activity, preferGreen, preferUnpaved }, controller.signal)
        .then(route => { if (!controller.signal.aborted) {
          reusableDraft.current = { key, candidate: route }; setDraft(route); setLastGoodDraft(route); setDraftPhase('ready');
        } })
        .catch((failure: unknown) => {
          if (!controller.signal.aborted) {
            setDraftPhase('error');
            setDraftError(failureMessages(failure, 'error.connectPoints', number));
          }
        });
    }, 500);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [start, drawnPoints, activity, preferGreen, preferUnpaved, draftRevision, restoring, completeDrawing, hasEnd, end]);

  // On a phone the routes are a swipeable row; keep the chosen route in view.
  useEffect(() => {
    const row = carousel.current;
    if (!row || !selectedId || row.scrollWidth <= row.clientWidth + 1) return;
    row.querySelector<HTMLElement>(`[data-route-id="${CSS.escape(selectedId)}"]`)
      ?.scrollIntoView?.({ behavior: 'smooth', inline: 'center', block: 'nearest' });
  }, [selectedId, view]);

  // Drawing happens on the map; keep its controls in sight in the shrunken sheet.
  useEffect(() => {
    if (mode === 'draw' && view === 'preferences') drawingRef.current?.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
  }, [mode, view]);

  function invalidate() {
    revision.current++; geoRevision.current++;
    generation.current?.abort(); restoration.current?.abort(); exportingController.current?.abort(); setRestoring(false);
    editController.current?.abort(); setEditPhase('idle'); setSelectedPoint(null);
    setPhase('idle'); setCandidates([]); setSelectedId(null); setHighlightedId(null);
    setError([]); setNotice(null); setLocating(false); setExporting(null);
  }
  function goTo(next: Step) {
    setError([]); setSheetCollapsed(false); setStep(next);
    setMode(current => next === 'start' && !start ? 'start' : current === 'start' || current === 'end' || next !== 'preferences' && current === 'draw' ? 'browse' : current);
    setTimeout(() => (next === 'routes' ? resultsHeading : stepHeading).current?.focus({ preventScroll: true }), 0);
  }
  function next() {
    if (view === 'start') {
      if (!start) { setError([{ key: 'error.chooseStart' }]); return; }
      goTo('distance');
    } else if (view === 'distance') {
      const km = Number(distance);
      if (!Number.isFinite(km) || km < .5 || km > MAX_DISTANCE) { setError([{ key: 'error.distanceRange', params: { min: .5, max: MAX_DISTANCE } }]); return; }
      if (hasRadius && (!Number.isFinite(Number(radius)) || Number(radius) <= 0)) { setError([{ key: 'error.radius' }]); return; }
      goTo('preferences');
    }
  }
  function back() {
    goTo(view === 'preferences' ? 'distance' : 'start');
  }
  function changeDistance(value: string) {
    invalidate(); setDistance(value);
  }
  function nudgeDistance(delta: number) {
    const current = Number(distance) || 0;
    changeDistance(String(Math.max(.5, Math.min(MAX_DISTANCE, Math.round((current + delta) * 2) / 2))));
  }
  function pick(which: 'start' | 'end', place: Place) {
    if (mode === 'draw' && which === 'start' && !start) rememberDrawing();
    else resetDrawingHistory();
    invalidate();
    if (which === 'start') { setStart(place); setStartText(place.label); }
    else { setEnd(place); setEndText(place.label); }
    if (mode !== 'draw') setMode('browse');
  }
  function pickMapPoint(location: Location) {
    if (mode === 'browse') return;
    if (mode !== 'draw') { pick(mode, { label: coordinateLabel(location), location }); return; }
    if (!start) { pick('start', { label: coordinateLabel(location), location }); return; }
    const last = drawnPoints.at(-1) ?? start.location;
    if (Math.abs(location.latitude - last.latitude) < .00001 && Math.abs(location.longitude - last.longitude) < .00001) return;
    const index = completeDrawing
      ? findInsertionIndex(location, start.location, drawnPoints, hasEnd && end ? end.location : start.location, draft?.geometry.coordinates)
      : drawnPoints.length;
    insertPoint(index, location);
    // On a complete route each tap is one deliberate addition, so panning stays safe.
    if (completeDrawing) setMode('browse');
  }
  function insertPoint(index: number, location: Location) {
    if (drawnPoints.length >= (completeDrawing ? 19 : 20)) { setError([{ key: 'error.tooManyPoints' }]); return; }
    rememberDrawing(); invalidate(); setDraft(null);
    setDrawnPoints(points => [...points.slice(0, index), location, ...points.slice(index)]);
    setSelectedPoint(index); if (completeDrawing) setIsEditingRoute(true);
  }
  function rememberDrawing() {
    const fullPoints = completeDrawing && start ? [...drawnPoints, hasEnd && end ? end.location : start.location] : drawnPoints;
    drawingHistory.current.push({ start, end, startText, endText, drawnPoints, completeDrawing, preview: draft,
      previewKey: start && draft ? drawingKey(start.location, fullPoints, activity, preferGreen, preferUnpaved) : null });
    if (drawingHistory.current.length > 100) drawingHistory.current.shift();
    setHasDrawingHistory(true);
  }
  function resetDrawingHistory() {
    drawingHistory.current = []; setHasDrawingHistory(false); markerDragging.current = false;
  }
  function undoDrawnPoint() {
    if (markerDragging.current || restoring || (!drawingHistory.current.length && !drawnPoints.length)) return false;
    const previous = drawingHistory.current.pop();
    invalidate(); setDraft(null);
    if (previous) {
      setStart(previous.start); setEnd(previous.end); setStartText(previous.startText); setEndText(previous.endText);
      setDrawnPoints(previous.drawnPoints);
      setCompleteDrawing(previous.completeDrawing); setIsEditingRoute(previous.completeDrawing);
      if (previous.preview && previous.previewKey) reusableDraft.current = { key: previous.previewKey, candidate: previous.preview };
    } else { setDrawnPoints(points => points.slice(0, -1)); if (completeDrawing) setIsEditingRoute(true); }
    setHasDrawingHistory(drawingHistory.current.length > 0);
    return true;
  }
  function moveMarker(target: MapMarkerTarget, location: Location) {
    markerDragging.current = false;
    if (typeof target === 'number' && !drawnPoints[target]) return;
    const previous = typeof target === 'number' ? drawnPoints[target] : target === 'start' ? start?.location : end?.location;
    if (!previous || Math.abs(previous.latitude - location.latitude) < .000001 && Math.abs(previous.longitude - location.longitude) < .000001) return;
    rememberDrawing(); invalidate(); setDraft(null);
    if (completeDrawing) setIsEditingRoute(true);
    if (typeof target === 'number') {
      setDrawnPoints(points => points.map((point, index) => index === target ? location : point)); setSelectedPoint(target);
    }
    else {
      const place = { label: coordinateLabel(location), location };
      if (target === 'start') { setStart(place); setStartText(place.label); }
      else { setEnd(place); setEndText(place.label); }
    }
  }
  function removePoint(index: number) {
    if (!drawnPoints[index]) return;
    rememberDrawing(); invalidate(); setDraft(null); setDrawnPoints(points => points.filter((_, i) => i !== index));
    if (completeDrawing) setIsEditingRoute(true);
  }
  function clearDrawing() {
    rememberDrawing(); invalidate(); setDraft(null); setLastGoodDraft(null); setDrawnPoints([]); setCompleteDrawing(false); setIsEditingRoute(false); setMode('browse');
  }
  function refreshDrawing() {
    invalidate(); reusableDraft.current = null; setDraftRevision(n => n + 1);
  }
  function toggleEnd() {
    resetDrawingHistory();
    invalidate(); setHasEnd(value => !value); setEnd(null); setEndText('');
    setMode(hasEnd ? 'browse' : 'end');
  }
  function locate() {
    resetDrawingHistory();
    invalidate();
    if (!navigator.geolocation) { setError([{ key: 'error.noGeolocation' }]); return; }
    setLocating(true);
    const current = geoRevision.current;
    navigator.geolocation.getCurrentPosition(position => {
      if (current !== geoRevision.current) return;
      const location = { latitude: position.coords.latitude, longitude: position.coords.longitude };
      const label = t('start.currentLocation'); setStart({ label, location }); setStartText(label); setLocating(false);
      setMode('browse');
    }, failure => {
      if (current !== geoRevision.current) return;
      setLocating(false);
      setError([{ key: failure.code === 1 ? 'error.locationDenied' : 'error.locationUnavailable' }]);
    }, { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 });
  }
  async function submit(event: FormEvent) {
    event.preventDefault();
    // Enter on an earlier question moves on instead of searching.
    if (view === 'start' || view === 'distance') { next(); return; }
    setError([]); setNotice(null);
    if (isEditingRoute) { await saveEditedRoute(); return; }
    if (!start) { setError([{ key: 'error.chooseStart' }]); return; }
    if (hasEnd && !end) { setError([{ key: 'error.chooseEnd' }]); return; }
    if (drawnPoints.length > 0 && (!draft || draftPhase !== 'ready')) { setError([{ key: 'error.waitForDrawing' }]); return; }
    const km = Number(distance);
    const radiusKm = hasRadius ? Number(radius) : null;
    if (!Number.isFinite(km) || km < .5 || km > MAX_DISTANCE) { setError([{ key: 'error.distanceRange', params: { min: .5, max: MAX_DISTANCE } }]); return; }
    if (radiusKm !== null && (!Number.isFinite(radiusKm) || radiusKm <= 0)) { setError([{ key: 'error.radius' }]); return; }
    invalidate(); setMode('browse');
    const current = revision.current;
    const controller = new AbortController(); generation.current = controller;
    setPhase('loading');
    try {
      const routes = await generate({ start: start.location, end: hasEnd && end ? end.location : null,
        radiusKm, targetDistanceKm: km, activity, candidateCount: 3,
        preferGreen, preferUnpaved, avoidBacktracking: true,
        distanceFlexibilityPercent: hasFlexibleDistance ? Number(distanceMargin) : 0,
        drawnRouteId: drawnPoints.length > 0 || completeDrawing ? draft!.id : null,
        isEditedRoute: completeDrawing }, controller.signal);
      if (controller.signal.aborted || revision.current !== current) return;
      setCandidates(routes); setSelectedId(routes[0]?.id ?? null); setPhase('done'); setStep('routes'); setSheetCollapsed(false);
      if (routes[0]?.searchId) {
        window.history.pushState(null, '', savedSearchPath(routes[0].searchId, routes[0].id));
        // The drawn prefix was persisted together with the search; it no longer expires.
        if (draft && reusableDraft.current?.candidate.id === draft.id) {
          const savedDraft = { ...draft, expiresAt: null };
          reusableDraft.current = { ...reusableDraft.current, candidate: savedDraft }; setDraft(savedDraft);
        }
      }
      setTimeout(() => resultsHeading.current?.focus({ preventScroll: true }), 0);
    } catch (ex) {
      if (controller.signal.aborted || revision.current !== current) return;
      setPhase('idle');
      setError(failureMessages(ex, 'error.unreachable', number));
    }
  }
  function cancel() { invalidate(); setNotice({ key: 'notice.cancelled' }); }
  async function beginEditing(candidate: RouteCandidate) {
    editController.current?.abort();
    const controller = new AbortController(); editController.current = controller;
    const current = ++revision.current;
    setEditPhase('opening'); setError([]);
    try {
      const session = await editRoute(candidate.id, controller.signal);
      if (controller.signal.aborted || current !== revision.current) return;
      const request = session.request;
      const startPlace = { label: coordinateLabel(request.start), location: request.start };
      const endPlace = !session.isLoop && request.end ? { label: coordinateLabel(request.end), location: request.end } : null;
      const fullPoints = [...session.points, endPlace?.location ?? startPlace.location];
      invalidate(); resetDrawingHistory();
      setStart(startPlace); setStartText(startPlace.label); setEnd(endPlace); setEndText(endPlace?.label ?? ''); setHasEnd(!!endPlace);
      setActivity(request.activity); setDistance(String(request.targetDistanceKm));
      setHasRadius(request.radiusKm !== null); setRadius(String(request.radiusKm ?? 2));
      setHasFlexibleDistance(!!request.distanceFlexibilityPercent); setDistanceMargin(String(request.distanceFlexibilityPercent || 15));
      setPreferGreen(!!request.preferGreen); setPreferUnpaved(!!request.preferUnpaved);
      setDrawnPoints(session.points); setCompleteDrawing(true); setIsEditingRoute(true); setMode('browse');
      reusableDraft.current = { candidate: session.preview,
        key: drawingKey(request.start, fullPoints, request.activity, !!request.preferGreen, !!request.preferUnpaved) };
      setDraft(session.preview); setLastGoodDraft(session.preview); setDraftPhase('ready'); setDraftError([]);
    } catch (failure) {
      if (!controller.signal.aborted && current === revision.current) {
        setEditPhase('idle'); setError(failureMessages(failure, 'error.openEditor', number));
      }
    }
  }
  async function saveEditedRoute() {
    if (!start || !draft || draftPhase !== 'ready') return;
    editController.current?.abort();
    const controller = new AbortController(); editController.current = controller;
    const current = revision.current;
    setEditPhase('saving'); setError([]);
    try {
      const routes = await saveRoute({ start: start.location, end: hasEnd && end ? end.location : null,
        radiusKm: hasRadius ? Number(radius) : null, targetDistanceKm: Number(distance), activity, candidateCount: 1,
        preferGreen, preferUnpaved, avoidBacktracking: true, distanceFlexibilityPercent: hasFlexibleDistance ? Number(distanceMargin) : 0,
        drawnRouteId: draft.id, isEditedRoute: true }, controller.signal);
      if (controller.signal.aborted || current !== revision.current) return;
      setCandidates(routes); setSelectedId(routes[0]?.id ?? null); setPhase('done'); setMode('browse'); setStep('routes');
      setIsEditingRoute(false); setEditPhase('idle'); setSelectedPoint(null);
      const savedDraft = { ...draft, expiresAt: null }; setDraft(savedDraft);
      if (reusableDraft.current?.candidate.id === draft.id) reusableDraft.current = { ...reusableDraft.current, candidate: savedDraft };
      if (routes[0]?.searchId) window.history.pushState(null, '', savedSearchPath(routes[0].searchId, routes[0].id));
      setNotice({ key: 'notice.saved' });
      setTimeout(() => resultsHeading.current?.focus({ preventScroll: true }), 0);
    } catch (failure) {
      if (!controller.signal.aborted && current === revision.current) {
        setEditPhase('idle'); setError(failureMessages(failure, 'error.saveEdited', number));
      }
    }
  }
  function cancelEditing() {
    window.dispatchEvent(new PopStateEvent('popstate'));
  }
  function dismissHint() {
    setHintDismissed(true);
    try { window.localStorage.setItem(EDIT_HINT_KEY, '1'); } catch { /* the tip just returns next time */ }
  }
  function selectRoute(id: string) {
    setSelectedId(id);
    const searchId = candidates[0]?.searchId;
    if (searchId) window.history.replaceState(null, '', savedSearchPath(searchId, id));
  }
  function onCarouselScroll() {
    const row = carousel.current;
    if (!row || row.scrollWidth <= row.clientWidth + 1) return;
    clearTimeout(carouselTimer.current);
    carouselTimer.current = setTimeout(() => {
      const middle = row.getBoundingClientRect().left + row.clientWidth / 2;
      let nearest: HTMLElement | null = null, best = Infinity;
      row.querySelectorAll<HTMLElement>('[data-route-id]').forEach(card => {
        const rect = card.getBoundingClientRect();
        const offset = Math.abs(rect.left + rect.width / 2 - middle);
        if (offset < best) { best = offset; nearest = card; }
      });
      const id = (nearest as HTMLElement | null)?.dataset.routeId;
      if (id && id !== selectedId) selectRoute(id);
    }, 140);
  }
  async function exportRoute(candidate: RouteCandidate, format: 'gpx' | 'fit' = 'gpx') {
    exportingController.current?.abort();
    const controller = new AbortController(); exportingController.current = controller;
    setExporting({ id: candidate.id, format }); setError([]);
    try { await (format === 'fit' ? downloadFit : downloadGpx)(candidate, controller.signal); }
    catch (ex) {
      if (!controller.signal.aborted) setError(ex instanceof ApiError && ex.problem.status === 404
        ? [{ key: 'errors.exportExpired' }] : failureMessages(ex, 'error.exportFailed', number));
    } finally { if (!controller.signal.aborted) setExporting(null); }
  }
  async function sendToService(candidate: RouteCandidate, service: ServiceKind) {
    const controller = new AbortController();
    setSending(candidate.id); setError([]); setNotice(null);
    try {
      await sendRoute(candidate.id, service, controller.signal);
      setConnected(service, true);
      setNotice({ key: 'notice.sent', params: { service: SERVICE_NAMES[service] } });
    } catch (ex) {
      if (ex instanceof ApiError && ex.problem.status === 401) setConnected(service, false);
      setError(failureMessages(ex, 'error.sendFailed', number));
    } finally { setSending(null); }
  }
  async function disconnectService(service: ServiceKind) {
    try { await disconnect(service); } catch (ex) { setError(failureMessages(ex, 'error.disconnectFailed', number)); }
  }

  useEffect(() => {
    if (!connectReturn || phase !== 'done') return;
    setConnectReturn(null);
    if (connectReturn.failed) { setError([{ key: 'errors.connectDenied' }]); return; }
    const route = candidates.find(item => item.id === selectedId);
    if (!connectReturn.send) return;
    if (route) void sendToService(route, connectReturn.service);
    else setError([{ key: 'errors.routeNotFound' }]);
  }, [connectReturn, phase, candidates, selectedId]);

  const km = Number(distance);
  const distanceValid = Number.isFinite(km) && km >= .5 && km <= MAX_DISTANCE;
  const margin = Number(distanceMargin) / 100;
  const distanceWindow = distanceValid
    ? t('distance.window', { min: number(Math.max(.5, km * (1 - margin)), 2), max: number(Math.min(MAX_DISTANCE, km * (1 + margin)), 2) }) : '';
  const sliderFill = `${(Math.max(.5, Math.min(MAX_DISTANCE, km || .5)) - .5) / (MAX_DISTANCE - .5) * 100}%`;
  const questionIndex = QUESTION_STEPS.findIndex(item => item.step === view);
  const preferenceSummary = [preferGreen && t('summary.green'), preferUnpaved && t('summary.unpaved'), hasEnd && t('summary.oneWay'),
    drawnPoints.length > 0 && !completeDrawing && t('summary.drawn')].filter(Boolean).join(', ') || t('summary.none');
  const closestId = candidates.length > 1 && distanceValid
    ? candidates.reduce((best, route) => Math.abs(route.distanceKm - km) < Math.abs(best.distanceKm - km) ? route : best).id : null;
  const alerts = <>
    {error.length > 0 && <div className="error-message" role="alert"><p>{say(error)}</p></div>}
    {notice && <p role="status" className="notice">{say(notice)}</p>}
  </>;

  // A complete edited route is the result itself, so it has no separate section.
  const drawingSection = (drawnPoints.length > 0 || mode === 'draw') && !(view === 'routes' && completeDrawing) && <section className="drawing-controls" aria-label={t('drawing.section')} ref={drawingRef}>
    <div className="drawing-heading"><strong>{t('drawing.points', { count: drawnPoints.length })}</strong>
      {draft && <span>{t(completeDrawing ? 'drawing.km' : 'drawing.drawnKm', { km: number(draft.distanceKm, 2) })}</span>}</div>
    {draftPhase === 'loading' && <p role="status">{t('drawing.connecting')}</p>}
    {draftError.length > 0 && <p className="field-error" role="alert">{say(draftError)}{lastGoodDraft && ` ${t('drawing.lastValid')}`}</p>}
    {view !== 'routes' && <div className="drawing-actions">
      <button type="button" className="chip-button" disabled={!hasDrawingHistory && drawnPoints.length === 0}
        title={t('drawing.undoTitle')} aria-keyshortcuts="Control+Z Meta+Z" onClick={undoDrawnPoint}><Icon name="undo" />{t('drawing.undo')}</button>
      {selectedPoint !== null && drawnPoints[selectedPoint] && <button type="button" className="chip-button"
        aria-keyshortcuts="Delete" onClick={() => removePoint(selectedPoint)}><Icon name="trash" />{t('drawing.removePoint', { n: selectedPoint + 1 })}</button>}
      <button type="button" className="chip-button" disabled={drawnPoints.length === 0} onClick={clearDrawing}>{t('drawing.clear')}</button>
      {drawnPoints.length > 0 && <button type="button" className="chip-button" onClick={refreshDrawing}>{t('drawing.refresh')}</button>}
    </div>}
    {draft && <button type="button" className="text-link drawn-download" disabled={exporting !== null}
      onClick={() => void exportRoute(draft)}><Icon name="download" />{completeDrawing ? t('drawing.downloadEdited') : t('drawing.downloadDrawn')}</button>}
    {draft && draft.warnings.filter(showRouteWarning).length > 0 && <ul className="route-warnings">
      {draft.warnings.filter(showRouteWarning).map(warning => <li key={warning.code}>{say(warningMessage(warning, number))}</li>)}
    </ul>}
  </section>;

  function stepHeader(title: ReactNode, subtitle?: string) {
    return <div className="step-header">
      <div className="step-progress">
        {questionIndex > 0 && <button type="button" className="round-button" aria-label={t(questionIndex === 1 ? 'step.back.start' : 'step.back.distance')} onClick={back}>
          <Icon name="back" /></button>}
        <div className="progress-track">
          <div className="progress-label"><span>{t('step.progress', { current: questionIndex + 1, total: 3 })}</span><span>{t(QUESTION_STEPS[questionIndex].label)}</span></div>
          <div className="progress-bar" aria-hidden="true">{QUESTION_STEPS.map((item, index) =>
            <span key={item.step} className={index <= questionIndex ? 'done' : ''} />)}</div>
        </div>
      </div>
      <h1 className="step-title" ref={stepHeading} tabIndex={-1}>{title}</h1>
      {subtitle && <p className="step-subtitle">{subtitle}</p>}
    </div>;
  }

  function renderQuestion() {
    if (view === 'start') return <>
      {stepHeader(t('start.title'))}
      <fieldset className="activity-picker"><legend className="visually-hidden">{t('activity.legend')}</legend>
        <div className="segmented-control">
          {(['running', 'walking'] as const).map(value => <label key={value} className={activity === value ? 'selected' : ''}>
            <input type="radio" name="activity" value={value} checked={activity === value}
              onChange={() => { invalidate(); setActivity(value); }} />
            <Icon name={value === 'running' ? 'run' : 'walk'} /> {t(value === 'running' ? 'activity.running' : 'activity.walking')}
          </label>)}
        </div>
      </fieldset>
      <LocationSearch label={t('start.label')} placeholder={t('start.placeholder')} value={startText} place={start}
        onTextChange={value => { resetDrawingHistory(); invalidate(); setStart(null); setStartText(value); }} onPick={place => pick('start', place)} />
      <div className="chip-row">
        <button type="button" className="chip-button" onClick={locate} disabled={locating}><Icon name="locate" />{locating ? t('start.locating') : t('start.useLocation')}</button>
        <button type="button" className="chip-button" aria-pressed={mode === 'start'} onClick={() => setMode(current => current === 'start' ? 'browse' : 'start')}>
          <Icon name="pin" />{t('start.pickOnMap')}</button>
      </div>
    </>;
    if (view === 'distance') return <>
      {stepHeader(<label htmlFor="distance">{t('distance.title')}</label>)}
      <div className="distance-dial">
        <button type="button" className="round-button large" aria-label={t('distance.shorter')} onClick={() => nudgeDistance(-.5)}><Icon name="minus" /></button>
        <div className="distance-number"><input id="distance" type="number" min="0.5" max={MAX_DISTANCE} step="0.1" inputMode="decimal"
          value={distance} onChange={e => changeDistance(e.target.value)} /><span>km</span></div>
        <button type="button" className="round-button large" aria-label={t('distance.longer')} onClick={() => nudgeDistance(.5)}><Icon name="plus" /></button>
      </div>
      <input className="distance-slider" type="range" aria-label={t('distance.slider')} min="0.5" max={MAX_DISTANCE} step="0.1"
        style={{ '--fill': sliderFill } as React.CSSProperties}
        value={Math.max(.5, Math.min(MAX_DISTANCE, km || .5))} onChange={e => changeDistance(e.target.value)} />
      <div className="quick-distances" role="group" aria-label={t('distance.common')}>
        {QUICK_DISTANCES.filter(value => value <= MAX_DISTANCE).map(value => <button type="button" key={value}
          aria-pressed={km === value} aria-label={t('distance.quick', { km: number(value) })} onClick={() => changeDistance(String(value))}>{number(value)}</button>)}
      </div>
      <div className="option-list">
        <div className="option-row">
          <label className="toggle-label"><span className="option-text"><span>{t('distance.flexible')}</span>
            {hasFlexibleDistance && distanceWindow && <span className="option-detail" aria-live="polite">{t('distance.between')} <strong>{distanceWindow}</strong></span>}</span>
            <input type="checkbox" checked={hasFlexibleDistance} onChange={event => { invalidate(); setHasFlexibleDistance(event.target.checked); }} />
            <span className="toggle-track" aria-hidden="true" />
          </label>
          {hasFlexibleDistance && <div className="option-setting"><label htmlFor="distance-margin">{t('distance.margin')}</label>
            <select id="distance-margin" value={distanceMargin} onChange={event => { invalidate(); setDistanceMargin(event.target.value); }}>
              {[10, 15, 20, 25, 30].map(value => <option key={value} value={value}>±{value}%</option>)}
            </select>
          </div>}
        </div>
        <div className="option-row">
          <label className="toggle-label"><span className="option-text"><span>{t('distance.stayClose')}</span></span>
            <input type="checkbox" checked={hasRadius} onChange={e => { invalidate(); setHasRadius(e.target.checked); }} />
            <span className="toggle-track" aria-hidden="true" />
          </label>
          {hasRadius && <div className="option-setting"><label htmlFor="radius">{t('distance.maxRadius')}</label><div className="unit-input">
            <input id="radius" type="number" min="0.01" step="0.1" value={radius} inputMode="decimal"
              onChange={e => { invalidate(); setRadius(e.target.value); }} /><span>km</span></div></div>}
        </div>
      </div>
    </>;
    return <>
      {stepHeader(t('preferences.title'), t('preferences.subtitle'))}
      <fieldset className="preference-cards"><legend className="visually-hidden">{t('step.preferences')}</legend>
        <label className={preferGreen ? 'selected' : ''}>
          <input type="checkbox" checked={preferGreen} onChange={event => { invalidate(); setPreferGreen(event.target.checked); }} />
          <Icon name="green" /><span>{t('preferences.green')}</span><span className="card-check" aria-hidden="true"><Icon name="check" /></span></label>
        <label className={preferUnpaved ? 'selected' : ''}>
          <input type="checkbox" checked={preferUnpaved} onChange={event => { invalidate(); setPreferUnpaved(event.target.checked); }} />
          <Icon name="unpaved" /><span>{t('preferences.unpaved')}</span><span className="card-check" aria-hidden="true"><Icon name="check" /></span></label>
      </fieldset>
      <div className="option-list">
        <span className="option-heading">{t('shape.heading')}</span>
        <div className="option-row">
          <button type="button" className="option-button" aria-expanded={hasEnd} onClick={toggleEnd}>
            <span>{t('shape.finishElsewhere')}</span><span className="option-value">{hasEnd ? t('shape.makeLoop') : t('shape.sameAsStart')}<Icon name="chevron" /></span></button>
          {hasEnd && <div className="end-section">
            <LocationSearch label={t('end.label')} placeholder={t('end.placeholder')} value={endText} place={end}
              onTextChange={value => { resetDrawingHistory(); invalidate(); setEnd(null); setEndText(value); }} onPick={place => pick('end', place)} />
            <button type="button" className="chip-button" aria-pressed={mode === 'end'} onClick={() => setMode(current => current === 'end' ? 'browse' : 'end')}>
              <Icon name="pin" />{t('end.pickOnMap')}</button>
          </div>}
        </div>
        <div className="option-row">
          <button type="button" className="option-button" aria-pressed={mode === 'draw'}
            onClick={() => setMode(current => current === 'draw' ? 'browse' : 'draw')}>
            <span>{t('shape.passThrough')}</span><span className="option-value">{mode === 'draw' ? t('shape.doneDrawing') : drawnPoints.length ? t('shape.editOnMap') : t('shape.drawOnMap')}<Icon name="chevron" /></span></button>
          {drawingSection}
        </div>
      </div>
    </>;
  }

  function renderRoutes() {
    return <>
      <div className="step-summary">
        <button type="button" aria-label={t('summary.changeStart')} onClick={() => goTo('start')}>
          <Icon name="check" /><span><small>{t('step.start')}</small>{t('summary.start', { activity: t(activity === 'running' ? 'activity.running' : 'activity.walking'), place: start?.label ?? '…' })}</span><span className="summary-change">{t('summary.change')}</span></button>
        <button type="button" aria-label={t('summary.changeDistance')} onClick={() => goTo('distance')}>
          <Icon name="check" /><span><small>{t('step.distance')}</small>{t('drawing.km', { km: number(km || 0) })}{hasFlexibleDistance ? t('summary.margin', { margin: distanceMargin }) : ''}{hasRadius ? t('summary.within', { km: number(Number(radius)) }) : ''}</span><span className="summary-change">{t('summary.change')}</span></button>
        <button type="button" aria-label={t('summary.changePreferences')} onClick={() => goTo('preferences')}>
          <Icon name="check" /><span><small>{t('step.preferences')}</small>{preferenceSummary}</span><span className="summary-change">{t('summary.change')}</span></button>
      </div>
      <section className="results" aria-label={t('results.label')} aria-busy={phase === 'loading'}>
        {candidates.length > 0 ? <>
          <div className="results-heading"><h2 ref={resultsHeading} tabIndex={-1}>{candidates.length === 1 ? t('results.one') : t('results.many', { count: candidates.length })}</h2>
            {candidates.length > 1 && <div className="carousel-dots" aria-hidden="true">{candidates.map(route =>
              <span key={route.id} className={route.id === selectedId ? 'active' : ''} />)}</div>}</div>
          <ul className="candidate-list" ref={carousel} onScroll={onCarouselScroll}>{candidates.map((candidate, index) => {
            const warnings = candidate.warnings.filter(showRouteWarning);
            return <li key={candidate.id} data-route-id={candidate.id}
              className={`route-card ${candidate.id === selectedId ? 'selected' : ''} ${candidate.status === 'diagnostic' ? 'diagnostic' : ''}`}
              style={{ '--route-color': ROUTE_COLORS[index % ROUTE_COLORS.length] } as React.CSSProperties}
              onPointerEnter={event => { if (event.pointerType === 'mouse') setHighlightedId(candidate.id); }}
              onPointerLeave={event => { if (event.pointerType === 'mouse') setHighlightedId(null); }}
              onFocus={() => setHighlightedId(candidate.id)} onBlur={event => { if (!event.currentTarget.contains(event.relatedTarget)) setHighlightedId(null); }}>
              <button type="button" className="route-select" aria-pressed={selectedId === candidate.id} onClick={() => selectRoute(candidate.id)}>
                <span className="route-swatch" aria-hidden="true" /><strong>{t('route.name', { n: index + 1 })}</strong>
                {candidate.id === closestId && <span className="route-badge">{t('route.closest', { km: number(km) })}</span>}
              </button>
              <dl className="route-stats">
                <div><dt>{t('route.distance')}</dt><dd>{number(candidate.distanceKm, 2)}<small> km</small></dd></div>
                <div><dt>{t('route.ascent')}</dt><dd>{candidate.elevationGainM === null ? <small>{t('route.noElevation')}</small> : <>{Math.round(candidate.elevationGainM)}<small> m</small></>}</dd></div>
              </dl>
              {candidate.status !== 'qualified' && <span className="route-status">{candidate.status === 'diagnostic' ? t('route.diagnostic') : t('route.outside')}</span>}
              {(candidate.woodlandPercent != null || candidate.unpavedPercent != null) && <div className="route-quality">
                {candidate.woodlandPercent != null && <span title={t('route.natureTitle')}><Icon name="green" />{t('route.nature', { percent: Math.round(candidate.woodlandPercent) })}</span>}
                {candidate.unpavedPercent != null && <span><Icon name="unpaved" />{t('route.unpaved', { percent: Math.round(candidate.unpavedPercent) })}</span>}
              </div>}
              {warnings.length > 0 && <ul className="route-warnings">{warnings.map(w => <li key={w.code}>{say(warningMessage(w, number))}</li>)}</ul>}
              <div className="route-card-actions">
                <button type="button" className="route-action route-action-edit" disabled={editPhase !== 'idle'}
                  onClick={() => void beginEditing(candidate)} aria-label={t('route.editLabel', { n: index + 1 })}>
                  <Icon name="edit" />{t('route.edit')}</button>
                <button type="button" className="route-action route-action-download" disabled={exporting !== null} onClick={() => void exportRoute(candidate)}>
                  <Icon name="download" />{exporting?.id === candidate.id && exporting.format === 'gpx' ? t('route.preparing') : t('route.download')}</button>
              </div>
              <div className="route-card-more">
                <button type="button" className="text-link" disabled={exporting !== null} onClick={() => void exportRoute(candidate, 'fit')}>
                  <Icon name="download" />{exporting?.id === candidate.id && exporting.format === 'fit' ? t('route.preparing') : t('route.downloadFit')}</button>
                {candidate.searchId && connections.map(({ service, connected }) => {
                  const label = <><Icon name="send" />{sending === candidate.id ? t('route.sending') : t('route.sendTo', { service: SERVICE_NAMES[service] })}</>;
                  return connected
                    ? <button key={service} type="button" className="text-link" disabled={sending !== null} onClick={() => void sendToService(candidate, service)}>{label}</button>
                    : <a key={service} className="text-link" href={connectUrl(service, `${savedSearchPath(candidate.searchId!, candidate.id)}&send=${service}`)}>{label}</a>;
                })}
              </div>
            </li>;
          })}</ul>
          {candidates[0].searchId ? <p className="export-note"><Icon name="link" /><a href={savedSearchPath(candidates[0].searchId, selectedId ?? candidates[0].id)}>{t('results.savedLink')}</a></p>
            : candidates[0].expiresAt && <p className="export-note">{t('results.downloadsUntil', { time: new Date(candidates[0].expiresAt).toLocaleTimeString(language, { hour: '2-digit', minute: '2-digit' }) })}</p>}
          {candidates[0].searchId && <ConnectionsPanel connections={connections}
            returnUrl={savedSearchPath(candidates[0].searchId, selectedId ?? candidates[0].id)} onDisconnect={service => void disconnectService(service)} />}
        </> : phase === 'done' ? <div className="empty-state"><h2>{t('results.none')}</h2><p>{t('results.noneHint')}</p></div>
          : <p className="loading-state" role="status"><span className="spinner" />{restoring ? t('actions.opening') : t('actions.finding')}</p>}
      </section>
      {drawingSection}
      {editPhase === 'opening' && <p className="notice" role="status">{t('actions.openingEditor')}</p>}
      {alerts}
    </>;
  }

  const routeMap = <RouteMap start={start?.location ?? null} end={hasEnd ? end?.location ?? null : null}
    colorScheme={colorScheme} language={language}
    radiusKm={hasRadius && Number(radius) > 0 ? Number(radius) : null} candidates={candidates}
    drawnPoints={drawnPoints} preview={draft} previousPreview={lastGoodDraft}
    selectedId={selectedId} highlightedId={highlightedId} mode={mode}
    onPick={pickMapPoint} onSelect={selectRoute} onMoveMarker={moveMarker}
    onMarkerDragChange={dragging => { markerDragging.current = dragging; }}
    selectedPoint={selectedPoint} onSelectPoint={setSelectedPoint}
    editing={view === 'edit'} onInsertPoint={insertPoint} padding={padding} fitRequest={fitRequest} />;

  const brand = <a className="brand" href="/" aria-label={t('brand.home')}><PathIcon small /><span>meander<span className="brand-period">.</span></span></a>;
  const languagePicker = <div className="language-picker" role="group" aria-label={t('language.group')}>
    {(['nl', 'en'] as const).map(value => <button type="button" key={value} lang={value}
      title={t(`language.${value}`)} aria-pressed={language === value} onClick={() => setLanguage(value)}>
      {value.toUpperCase()}</button>)}
  </div>;
  const themePicker = <div className="theme-picker" role="group" aria-label={t('theme.group')}>
    {(['system', 'light', 'dark'] as ThemePreference[]).map(value => <button type="button" key={value}
      aria-label={t(`theme.${value}` as const)} title={t(`theme.${value}` as const)}
      aria-pressed={themePreference === value} onClick={() => changePreference(value)}>
      <Icon name={value === 'system' ? 'system' : value === 'light' ? 'sun' : 'moon'} />
    </button>)}
  </div>;
  const providerCredit = <div className="provider-credit">{t('credit.routing')} <a href="https://openrouteservice.org/" target="_blank" rel="noreferrer">openrouteservice</a> · {t('credit.data')} <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noreferrer">© OpenStreetMap</a></div>;

  if (view === 'edit') {
    const statusText = draftPhase === 'loading' ? t('edit.updating') : draft ? t('drawing.km', { km: number(draft.distanceKm, 2) }) : lastGoodDraft ? t('drawing.km', { km: number(lastGoodDraft.distanceKm, 2) }) : '—';
    return <div className="app-shell is-editing" style={{ '--inset-top': `${padding.top}px`, '--inset-bottom': `${padding.bottom}px`, '--inset-left': '0px' } as React.CSSProperties}>
      {routeMap}
      <div className="edit-top" data-map-overlay="top">
        <div className="edit-bar">
          <button type="button" className="round-button floating" aria-label={t('edit.cancel')} onClick={cancelEditing}><Icon name="close" /></button>
          <div className="edit-status" role="status" aria-live="polite"><strong>{statusText}</strong>
            <span>{t('drawing.points', { count: drawnPoints.length })}</span></div>
          <form onSubmit={event => void submit(event)} noValidate>
            <button type="submit" className="primary-pill" aria-label={t('edit.saveLabel')}
              disabled={!draft || draftPhase !== 'ready' || editPhase !== 'idle'}>{editPhase === 'saving' ? t('edit.saving') : t('edit.save')}</button>
          </form>
        </div>
        {(draftError.length > 0 || error.length > 0) ? <div className="edit-banner is-error" role="alert">
          <p>{error.length > 0 ? say(error) : <>{say(draftError)}{lastGoodDraft && ` ${t('drawing.lastValid')}`}</>}</p>
          {draftError.length > 0 && error.length === 0 && <button type="button" className="chip-button" onClick={refreshDrawing}>{t('edit.retry')}</button>}
        </div> : !hintDismissed && <div className="edit-banner">
          <Icon name="move" /><p>{t('edit.hint')}</p>
          <button type="button" className="round-button quiet" aria-label={t('edit.dismissHint')} onClick={dismissHint}><Icon name="close" /></button>
        </div>}
      </div>
      <div className="edit-bottom" data-map-overlay="bottom">
        {selectedPoint !== null && drawnPoints[selectedPoint] && <p className="edit-selection">{t('edit.selected', { n: selectedPoint + 1 })}</p>}
        <div className="edit-toolbar" role="toolbar" aria-label={t('edit.toolbar')}>
          <button type="button" disabled={!hasDrawingHistory && drawnPoints.length === 0}
            title={t('drawing.undoTitle')} aria-keyshortcuts="Control+Z Meta+Z" onClick={undoDrawnPoint}><Icon name="undo" /><span>{t('drawing.undo')}</span></button>
          <button type="button" aria-pressed={mode === 'draw'} onClick={() => setMode(current => current === 'draw' ? 'browse' : 'draw')}>
            <Icon name="plus" /><span>{mode === 'draw' ? t('edit.tapMap') : t('edit.add')}</span></button>
          <button type="button" disabled={selectedPoint === null || !drawnPoints[selectedPoint]} aria-keyshortcuts="Delete"
            aria-label={selectedPoint !== null && drawnPoints[selectedPoint] ? t('drawing.removePoint', { n: selectedPoint + 1 }) : t('edit.removePoint')}
            onClick={() => selectedPoint !== null && removePoint(selectedPoint)}><Icon name="trash" /><span>{t('edit.remove')}</span></button>
          <button type="button" onClick={() => setFitRequest(n => n + 1)}><Icon name="fit" /><span>{t('edit.fit')}</span></button>
        </div>
      </div>
    </div>;
  }

  return <div className={`app-shell step-${view} ${sheetCollapsed ? 'sheet-collapsed' : ''} ${mode === 'draw' ? 'is-drawing' : ''}`}
    style={{ '--inset-top': `${padding.top}px`, '--inset-bottom': `${padding.bottom}px`, '--inset-left': `${padding.left}px` } as React.CSSProperties}>
    {routeMap}
    <header className="top-bar" data-map-overlay="topbar">{brand}<div className="top-controls">{languagePicker}{themePicker}</div></header>
    <aside className="planner-sheet" aria-label={t('sheet.planner')} data-map-overlay="sheet" {...sheetSwipe}>
      <button type="button" className="sheet-grabber" aria-expanded={!sheetCollapsed}
        aria-label={sheetCollapsed ? t('sheet.show') : t('sheet.more')} onClick={() => setSheetCollapsed(value => !value)}><span /></button>
      <div className="sheet-body">
        {view === 'routes' ? renderRoutes() : <form className="wizard-step" onSubmit={event => void submit(event)} noValidate aria-label={t('wizard.form')}>
          {renderQuestion()}
          {alerts}
          {editPhase === 'opening' && <p className="notice" role="status">{t('actions.openingEditor')}</p>}
          <div className="step-actions">
            {view === 'preferences' ? <>
              <button type="submit" className="generate-button" disabled={phase === 'loading' || editPhase !== 'idle' || (drawnPoints.length > 0 || completeDrawing) && draftPhase !== 'ready'}>
                {phase === 'loading' ? <><span className="spinner" /> {restoring ? t('actions.opening') : t('actions.finding')}</>
                  : <>{t('actions.find')} <Icon name="next" /></>}
              </button>
              {phase === 'loading' && <button type="button" className="text-link" onClick={cancel}>{t('actions.cancel')}</button>}
            </> : <button type="submit" className="generate-button">{t(view === 'start' ? 'actions.next.distance' : 'actions.next.preferences')} <Icon name="next" /></button>}
            {candidates.length > 0 && phase === 'done' && <button type="button" className="text-link" onClick={() => goTo('routes')}>{t('actions.backToRoutes')}</button>}
          </div>
        </form>}
        {providerCredit}
      </div>
    </aside>
  </div>;
}

export default function App() {
  return <I18nProvider><Planner /></I18nProvider>;
}
