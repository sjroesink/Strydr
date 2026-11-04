import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import App from './App';
import { ApiError, connectUrl, disconnect, downloadFit, downloadGpx, editRoute, generate, listConnections, loadSearch, previewRoute, saveRoute, sendRoute } from './api';
import { candidate } from './test/fixtures';
import type { Location, SavedRouteSearch } from './types';

vi.mock('./api', async importOriginal => {
  const actual = await importOriginal<typeof import('./api')>();
  return { ...actual, generate: vi.fn(), editRoute: vi.fn(), saveRoute: vi.fn(), loadSearch: vi.fn(), previewRoute: vi.fn(), downloadGpx: vi.fn(), downloadFit: vi.fn(), sendRoute: vi.fn(), disconnect: vi.fn(), listConnections: vi.fn(), geocode: vi.fn().mockResolvedValue([]) };
});
vi.mock('./components/RouteMap', () => ({
  ROUTE_COLORS: ['#d3733c', '#466bc3', '#9363a9'],
  default: ({ onPick, onMoveMarker, onInsertPoint, onSelectPoint, mode, highlightedId, drawnPoints, preview, previousPreview, editing }: {
    onPick: (p: Location) => void; onMoveMarker: (target: 'start' | 'end' | number, p: Location) => void;
    onInsertPoint: (index: number, p: Location) => void;
    onSelectPoint: (index: number | null) => void;
    mode: string; highlightedId: string; drawnPoints: Location[]; preview: { id: string } | null;
    previousPreview: { id: string } | null; editing: boolean;
  }) => <div aria-label="Route map">
    <button onClick={() => onPick({ latitude: 52.09 + (mode === 'draw' ? (drawnPoints.length + 1) * .001 : 0), longitude: 5.12 })}>Set {mode} fixture</button>
    <button onClick={() => onMoveMarker(0, { latitude: 52.095, longitude: 5.13 })}>Move first fixture</button>
    <button onClick={() => onMoveMarker('start', { latitude: 52.085, longitude: 5.11 })}>Move start fixture</button>
    <button onClick={() => onSelectPoint(0)}>Select first fixture</button>
    {editing && <button onClick={() => onInsertPoint(1, { latitude: 52.2, longitude: 5.2 })}>Insert from line fixture</button>}
    <span data-testid="highlighted">{highlightedId}</span><span data-testid="draft-map">{preview?.id}</span>
    <span data-testid="last-valid-map">{previousPreview?.id}</span>
  </div>,
}));

afterEach(() => { window.localStorage.removeItem('meander.language'); });

beforeEach(() => {
  window.localStorage.removeItem('meander.language');
  vi.mocked(generate).mockReset(); vi.mocked(downloadGpx).mockReset();
  vi.mocked(downloadFit).mockReset(); vi.mocked(sendRoute).mockReset(); vi.mocked(disconnect).mockReset();
  vi.mocked(listConnections).mockReset().mockResolvedValue([]);
  vi.mocked(previewRoute).mockReset();
  vi.mocked(loadSearch).mockReset();
  vi.mocked(editRoute).mockReset(); vi.mocked(saveRoute).mockReset();
  vi.mocked(previewRoute).mockResolvedValue({ ...candidate(), id: 'drawn-1' });
});

async function chooseStart() { await userEvent.click(screen.getByRole('button', { name: 'Set start fixture' })); }
async function toDistance() { await userEvent.click(screen.getByRole('button', { name: /Next: distance/ })); }
async function toPreferences() {
  if (screen.queryByRole('button', { name: /Next: distance/ })) await toDistance();
  await userEvent.click(screen.getByRole('button', { name: /Next: preferences/ }));
}
async function search() { await userEvent.click(screen.getByRole('button', { name: /Find my routes/ })); }
async function startDrawing() { await userEvent.click(screen.getByRole('button', { name: /Pass through a spot/ })); }
const greenCard = () => screen.getByRole('checkbox', { name: /Green surroundings/ });

it('walks through the questions one step at a time and back again', async () => {
  render(<App />);
  expect(screen.getByRole('heading', { name: 'Where do you start?' })).toBeInTheDocument();
  expect(screen.queryByLabelText('How far?')).not.toBeInTheDocument();
  await chooseStart(); await toDistance();
  expect(screen.getByText('Step 2 of 3')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: '8 km' }));
  expect(screen.getByLabelText('How far?')).toHaveValue(8);
  await userEvent.click(screen.getByRole('button', { name: 'Half a kilometre longer' }));
  expect(screen.getByLabelText('How far?')).toHaveValue(8.5);
  await toPreferences();
  expect(screen.getByRole('heading', { name: 'What do you like?' })).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Back to distance' }));
  expect(screen.getByLabelText('How far?')).toHaveValue(8.5);
  await userEvent.click(screen.getByRole('button', { name: 'Back to start' }));
  expect(screen.getByLabelText('Start location')).toHaveValue('52.09000, 5.12000');
});

it('always enables backtracking avoidance when regenerating an older saved search', async () => {
  window.history.replaceState(null, '', '/routes/older');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('older'));
  vi.mocked(generate).mockResolvedValue([candidate()]);
  render(<App />);
  await screen.findByRole('button', { name: /Route 1/ });
  await userEvent.click(screen.getByRole('button', { name: 'Change preferences' }));
  await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ avoidBacktracking: true }), expect.any(AbortSignal));
});

it('connects clicked points, exports a drawn section, and includes it when completing the route', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  expect(screen.getByRole('button', { name: /Find my routes/ })).toBeDisabled();
  await screen.findByText('5.01 km drawn', {}, { timeout: 2500 });
  expect(previewRoute).toHaveBeenCalledWith(expect.objectContaining({ points: [{ latitude: 52.091, longitude: 5.12 }] }), expect.any(AbortSignal));
  expect(screen.getByTestId('draft-map')).toHaveTextContent('drawn-1');
  await userEvent.click(screen.getByRole('button', { name: /Download drawn GPX/ }));
  expect(downloadGpx).toHaveBeenCalledWith(expect.objectContaining({ id: 'drawn-1' }), expect.any(AbortSignal));
  await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ drawnRouteId: 'drawn-1' }), expect.any(AbortSignal));
  expect(screen.getByRole('button', { name: 'Set browse fixture' })).toBeInTheDocument();
});

it('undoing a point aborts the old preview and ignores its late response', async () => {
  let resolve!: (value: ReturnType<typeof candidate>) => void;
  vi.mocked(previewRoute).mockReturnValue(new Promise(done => { resolve = done; }));
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  const signal = vi.mocked(previewRoute).mock.calls[0][1];
  await userEvent.click(screen.getByRole('button', { name: 'Undo' }));
  expect(signal.aborted).toBe(true);
  await act(async () => { resolve(candidate()); });
  expect(screen.queryByText('5.01 km drawn')).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: /Find my routes/ })).toBeEnabled();
});

it('retries a failed preview and clears the drawing', async () => {
  vi.mocked(previewRoute).mockRejectedValueOnce(new ApiError({ title: 'No path', detail: 'No path near this point.', status: 422 }))
    .mockResolvedValueOnce({ ...candidate(), id: 'drawn-1' });
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  expect(await screen.findByRole('alert', {}, { timeout: 2500 })).toHaveTextContent('No path near this point.');
  await userEvent.click(screen.getByRole('button', { name: 'Refresh drawing' }));
  await screen.findByText('5.01 km drawn', {}, { timeout: 2500 });
  await userEvent.click(screen.getByRole('button', { name: 'Clear drawing' }));
  expect(screen.queryByRole('region', { name: 'Drawn route section' })).not.toBeInTheDocument();
  expect(screen.getByTestId('draft-map')).toBeEmptyDOMElement();
});

it('undoes added points with Ctrl+Z or Cmd+Z while preserving native undo in form fields', async () => {
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  expect(screen.getByText('2 points')).toBeInTheDocument();
  fireEvent.keyDown(greenCard(), { key: 'z', ctrlKey: true });
  fireEvent.keyDown(greenCard(), { key: 'z', metaKey: true });
  fireEvent.keyDown(window, { key: 'Z', ctrlKey: true, shiftKey: true });
  expect(screen.getByText('2 points')).toBeInTheDocument();
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  expect(screen.getByText('1 point')).toBeInTheDocument();
  fireEvent.keyDown(window, { key: 'z', metaKey: true });
  expect(screen.getByText('0 points')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Undo' })).toBeDisabled();
});

it('deletes only the selected point with Delete or its button, preserves text editing, and supports undo', async () => {
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Select first fixture' }));
  expect(screen.getByRole('button', { name: /Remove point 1/ })).toBeInTheDocument();
  fireEvent.keyDown(greenCard(), { key: 'Delete' });
  expect(screen.getByText('2 points')).toBeInTheDocument();
  fireEvent.keyDown(window, { key: 'Delete' });
  expect(screen.getByText('1 point')).toBeInTheDocument();
  // Selection is cleared, so another Delete cannot remove a different point.
  fireEvent.keyDown(window, { key: 'Delete' });
  expect(screen.getByText('1 point')).toBeInTheDocument();
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  const remaining = vi.mocked(previewRoute).mock.calls.at(-1)![0].points;
  expect(remaining).toHaveLength(1);
  expect(remaining[0].latitude).toBeCloseTo(52.092);
  expect(remaining[0].longitude).toBeCloseTo(5.12);
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  expect(screen.getByText('2 points')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Select first fixture' }));
  await userEvent.click(screen.getByRole('button', { name: /Remove point 1/ }));
  expect(screen.getByText('1 point')).toBeInTheDocument();
});

it('moves a drawn point, aborts an obsolete preview, and can undo just the movement', async () => {
  let resolve!: (value: ReturnType<typeof candidate>) => void;
  vi.mocked(previewRoute).mockReturnValueOnce(new Promise(done => { resolve = done; }))
    .mockResolvedValue({ ...candidate(), id: 'moved-preview' });
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  const originalSignal = vi.mocked(previewRoute).mock.calls[0][1];
  await userEvent.click(screen.getByRole('button', { name: 'Move first fixture' }));
  expect(originalSignal.aborted).toBe(true);
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(2), { timeout: 2000 });
  expect(previewRoute).toHaveBeenLastCalledWith(expect.objectContaining({ points: [{ latitude: 52.095, longitude: 5.13 }] }), expect.any(AbortSignal));
  await act(async () => { resolve({ ...candidate(), id: 'obsolete-preview' }); });
  expect(screen.getByTestId('draft-map')).toHaveTextContent('moved-preview');
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  expect(screen.getByText('1 point')).toBeInTheDocument();
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(3), { timeout: 2000 });
  expect(previewRoute).toHaveBeenLastCalledWith(expect.objectContaining({ points: [{ latitude: 52.091, longitude: 5.12 }] }), expect.any(AbortSignal));
});

it('undoes moving the start marker and clearing a drawing', async () => {
  render(<App />); await chooseStart(); await toPreferences(); await startDrawing();
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Move start fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Back to distance' }));
  await userEvent.click(screen.getByRole('button', { name: 'Back to start' }));
  expect(screen.getByLabelText('Start location')).toHaveValue('52.08500, 5.11000');
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  expect(screen.getByLabelText('Start location')).toHaveValue('52.09000, 5.12000');
  await toPreferences();
  expect(screen.getByText('1 point')).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Clear drawing' }));
  expect(screen.queryByRole('region', { name: 'Drawn route section' })).not.toBeInTheDocument();
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  expect(screen.getByText('1 point')).toBeInTheDocument();
});

it('allows a shorter or longer distance range and sends the selected scenery margin', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  render(<App />); await chooseStart(); await toDistance();
  fireEvent.change(screen.getByLabelText('How far?'), { target: { value: '10' } });
  expect(screen.queryByLabelText('Distance margin')).not.toBeInTheDocument();
  await userEvent.click(screen.getByRole('checkbox', { name: /Flexible distance/ }));
  expect(screen.getByText('8.50 and 11.50 km')).toBeInTheDocument();
  await userEvent.selectOptions(screen.getByLabelText('Distance margin'), '20');
  expect(screen.getByText('8.00 and 12.00 km')).toBeInTheDocument();
  await toPreferences();
  await userEvent.click(screen.getByRole('checkbox', { name: /Unpaved paths/ }));
  await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ targetDistanceKm: 10, preferUnpaved: true, distanceFlexibilityPercent: 20 }), expect.any(AbortSignal));
  await userEvent.click(await screen.findByRole('button', { name: 'Change distance' }));
  await userEvent.click(screen.getByRole('checkbox', { name: /Flexible distance/ }));
  expect(screen.queryByRole('button', { name: /Route 1/ })).not.toBeInTheDocument();
  await toPreferences(); await search();
  expect(generate).toHaveBeenLastCalledWith(expect.objectContaining({ distanceFlexibilityPercent: 0 }), expect.any(AbortSignal));
});

it('sends scenery preferences and default backtracking avoidance and shows route quality', async () => {
  vi.mocked(generate).mockResolvedValue([{ ...candidate(), greenScore: .8, woodlandPercent: 61, unpavedPercent: 42, surfaceKnownPercent: 90 }]);
  render(<App />); await chooseStart(); await toPreferences();
  expect(screen.queryByRole('checkbox', { name: /Avoid out-and-back/ })).not.toBeInTheDocument();
  await userEvent.click(greenCard());
  await userEvent.click(screen.getByRole('checkbox', { name: /Unpaved paths/ }));
  await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ preferGreen: true, preferUnpaved: true, avoidBacktracking: true }), expect.any(AbortSignal));
  expect(await screen.findByText('61% nature')).toBeInTheDocument();
  expect(screen.queryByText(/Green index/)).not.toBeInTheDocument();
  expect(screen.getByText('42% confirmed unpaved')).toBeInTheDocument();
});

it('requires a selected start and synchronizes numeric and slider distance', async () => {
  render(<App />);
  await toDistance();
  expect(screen.getByRole('alert')).toHaveTextContent('Choose a start location');
  expect(screen.queryByLabelText('How far?')).not.toBeInTheDocument();
  await chooseStart(); await toDistance();
  fireEvent.change(screen.getByLabelText('How far?'), { target: { value: '8.5' } });
  expect(screen.getByRole('slider', { name: 'Target distance' })).toHaveValue('8.5');
  fireEvent.change(screen.getByRole('slider'), { target: { value: '12.2' } });
  expect(screen.getByLabelText('How far?')).toHaveValue(12.2);
  fireEvent.change(screen.getByLabelText('How far?'), { target: { value: '0.1' } });
  await toPreferences();
  expect(screen.getByRole('alert')).toHaveTextContent('Choose a distance between 0.5 and');
});

it('creates a loop, selects and highlights candidates, and downloads GPX', async () => {
  vi.mocked(generate).mockResolvedValue([candidate(1), candidate(2), candidate(3)]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ end: null, targetDistanceKm: 5, activity: 'running', candidateCount: 3 }), expect.any(AbortSignal));
  expect(screen.getByRole('heading', { name: '3 routes for you' })).toBeInTheDocument();
  const second = await screen.findByRole('button', { name: /Route 2/ });
  await userEvent.click(second);
  expect(second).toHaveAttribute('aria-pressed', 'true');
  fireEvent.focus(second);
  expect(screen.getByTestId('highlighted')).toHaveTextContent('route-2');
  await userEvent.click(screen.getAllByRole('button', { name: /Download GPX/ })[1]);
  expect(downloadGpx).toHaveBeenCalledWith(expect.objectContaining({ id: 'route-2' }), expect.any(AbortSignal));
});

it('downloads a FIT course for the chosen route', async () => {
  vi.mocked(generate).mockResolvedValue([candidate(1), candidate(2)]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: /Route 2/ }));
  await userEvent.click(screen.getAllByRole('button', { name: /Download FIT/ })[1]);
  expect(downloadFit).toHaveBeenCalledWith(expect.objectContaining({ id: 'route-2' }), expect.any(AbortSignal));
});

it('supports a different end, walking, and a radius', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  render(<App />); await chooseStart();
  await userEvent.click(screen.getByRole('radio', { name: 'Walk' }));
  await toDistance();
  await userEvent.click(screen.getByRole('checkbox', { name: /Stay close to home/ }));
  fireEvent.change(screen.getByLabelText('Maximum radius'), { target: { value: '3' } });
  await toPreferences();
  await userEvent.click(screen.getByRole('button', { name: /Finish somewhere else/ }));
  await userEvent.click(screen.getByRole('button', { name: 'Set end fixture' }));
  await search();
  expect(generate).toHaveBeenCalledWith(expect.objectContaining({ end: { latitude: 52.09, longitude: 5.12 }, activity: 'walking', radiusKm: 3 }), expect.any(AbortSignal));
});

it('clears stale results and ignores an obsolete response after inputs change', async () => {
  let resolve!: (value: ReturnType<typeof candidate>[]) => void;
  vi.mocked(generate).mockReturnValue(new Promise(done => { resolve = done; }));
  render(<App />); await chooseStart(); await toPreferences(); await search();
  expect(screen.getByRole('button', { name: /Finding your way/ })).toBeDisabled();
  const signal = vi.mocked(generate).mock.calls[0][1];
  await userEvent.click(greenCard());
  expect(signal.aborted).toBe(true);
  await act(async () => { resolve([candidate()]); });
  expect(screen.queryByRole('button', { name: /Route 1/ })).not.toBeInTheDocument();
  expect(screen.getByRole('button', { name: /Find my routes/ })).toBeEnabled();
});

it('cancels generation without displaying a failure', async () => {
  vi.mocked(generate).mockReturnValue(new Promise(() => {}));
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(vi.mocked(generate).mock.calls[0][1].aborted).toBe(true);
  expect(screen.getByRole('status')).toHaveTextContent('Search cancelled');
  expect(screen.queryByRole('alert')).not.toBeInTheDocument();
});

it('labels diagnostic routes and renders warnings and missing elevation', async () => {
  vi.mocked(generate).mockResolvedValue([{ ...candidate(), status: 'diagnostic', elevationGainM: null,
    warnings: [{ code: 'radiusExceeded', message: 'This route exceeds the radius.' },
      { code: 'woodlandUnavailable', message: 'Could not look up nearby nature. These routes may not follow your green preference. Try again later.' }] }]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  expect(await screen.findByText('Exceeds radius · diagnostic')).toBeInTheDocument();
  expect(screen.getByText('This route exceeds the radius.')).toBeInTheDocument();
  expect(screen.getByText(/Elevation unavailable/)).toBeInTheDocument();
  expect(screen.queryByText(/Could not look up nearby nature/)).not.toBeInTheDocument();
  expect(screen.queryByText(/Furthest point/)).not.toBeInTheDocument();
});

it('shows measured minimum distance and supports retrying', async () => {
  vi.mocked(generate).mockRejectedValueOnce(new ApiError({ detail: 'Target is too short.', minimumDistanceKm: 7.2 }))
    .mockResolvedValueOnce([candidate()]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  expect(await screen.findByRole('alert')).toHaveTextContent('Minimum distance: 7.20 km');
  await search();
  expect(await screen.findByRole('button', { name: /Route 1/ })).toBeInTheDocument();
});

it('explains expired GPX exports', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  vi.mocked(downloadGpx).mockRejectedValue(new ApiError({ status: 404 }));
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: /Download GPX/ }));
  expect(await screen.findByRole('alert')).toHaveTextContent('export has expired');
});

it('handles permission denied and retains manual location selection', async () => {
  Object.defineProperty(navigator, 'geolocation', { configurable: true, value: {
    getCurrentPosition: (_success: unknown, failure: (e: { code: number }) => void) => failure({ code: 1 }),
  } });
  render(<App />);
  await userEvent.click(screen.getByRole('button', { name: /Use my location/ }));
  expect(screen.getByRole('alert')).toHaveTextContent('Location permission was denied');
  await chooseStart();
  expect(screen.getByLabelText('Start location')).toHaveValue('52.09000, 5.12000');
});

it('shows an empty state defensively if a server returns no suggestions', async () => {
  vi.mocked(generate).mockResolvedValue([]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await waitFor(() => expect(screen.getByText('No routes found')).toBeInTheDocument());
});

function savedSearch(id = 'search-1', withDrawing = false): SavedRouteSearch {
  return {
    id, createdAt: new Date().toISOString(),
    request: { start: { latitude: 52.09, longitude: 5.12 }, end: null, radiusKm: 3,
      targetDistanceKm: 12, activity: 'walking', candidateCount: 3, preferGreen: true,
      preferUnpaved: true, avoidBacktracking: false, distanceFlexibilityPercent: 20,
      drawnRouteId: withDrawing ? 'drawn-saved' : null },
    candidates: [1, 2].map(n => ({ ...candidate(n), searchId: id, expiresAt: null })),
    drawnPoints: withDrawing ? [{ latitude: 52.091, longitude: 5.12 }] : [],
    drawnPreview: withDrawing ? { ...candidate(), id: 'drawn-saved', expiresAt: null } : null,
  };
}

it('keeps results on an empty map click and only changes the start when explicitly requested', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  const first = await screen.findByRole('button', { name: /Route 1/ });
  await userEvent.click(screen.getByRole('button', { name: 'Set browse fixture' }));
  expect(first).toBeInTheDocument();
  expect(generate).toHaveBeenCalledTimes(1);
  await userEvent.click(screen.getByRole('button', { name: 'Change start' }));
  await userEvent.click(screen.getByRole('button', { name: /Pick on map/ }));
  await chooseStart();
  expect(screen.queryByRole('button', { name: /Route 1/ })).not.toBeInTheDocument();
});

it('puts the saved search and selected route in the URL', async () => {
  vi.mocked(generate).mockResolvedValue(savedSearch().candidates);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await screen.findByRole('button', { name: /Route 1/ });
  expect(window.location.pathname).toBe('/routes/search-1');
  expect(window.location.search).toBe('?route=route-1');
  await userEvent.click(screen.getByRole('button', { name: /Route 2/ }));
  expect(window.location.search).toBe('?route=route-2');
  expect(screen.getByRole('link', { name: 'Saved route link' })).toHaveAttribute('href', '/routes/search-1?route=route-2');
});

it('opens a saved URL with its settings, chosen candidate and fixed section without routing calls', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-2');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1', true));
  render(<App />);
  expect(await screen.findByRole('button', { name: /Route 2/ })).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByRole('button', { name: 'Change distance' })).toHaveTextContent('12 km, ±20%, within 3 km');
  expect(screen.getByRole('button', { name: 'Change preferences' })).toHaveTextContent('green, unpaved, drawn section');
  await userEvent.click(screen.getByRole('button', { name: /Download drawn GPX/ }));
  expect(downloadGpx).toHaveBeenCalledWith(expect.objectContaining({ id: 'drawn-saved' }), expect.any(AbortSignal));
  await userEvent.click(screen.getByRole('button', { name: 'Change start' }));
  expect(screen.getByRole('radio', { name: 'Walk' })).toBeChecked();
  await toDistance();
  expect(screen.getByLabelText('How far?')).toHaveValue(12);
  expect(screen.getByLabelText('Maximum radius')).toHaveValue(3);
  expect(screen.getByLabelText('Distance margin')).toHaveValue('20');
  await toPreferences();
  expect(greenCard()).toBeChecked();
  expect(screen.getByRole('checkbox', { name: /Unpaved paths/ })).toBeChecked();
  expect(screen.queryByRole('checkbox', { name: /Avoid out-and-back/ })).not.toBeInTheDocument();
  expect(screen.getByTestId('draft-map')).toHaveTextContent('drawn-saved');
  expect(screen.getByRole('button', { name: /Find my routes/ })).toBeEnabled();
  await new Promise(resolve => setTimeout(resolve, 650));
  expect(previewRoute).not.toHaveBeenCalled(); expect(generate).not.toHaveBeenCalled();
});

it('restores an earlier search on browser navigation and ignores a late obsolete response', async () => {
  let resolve!: (value: SavedRouteSearch) => void;
  vi.mocked(loadSearch).mockReturnValueOnce(new Promise(done => { resolve = done; }))
    .mockResolvedValueOnce(savedSearch('search-2'));
  window.history.replaceState(null, '', '/routes/search-1');
  render(<App />);
  const signal = vi.mocked(loadSearch).mock.calls[0][1];
  await act(async () => {
    window.history.replaceState(null, '', '/routes/search-2?route=route-2');
    window.dispatchEvent(new PopStateEvent('popstate'));
  });
  expect(signal.aborted).toBe(true);
  expect(await screen.findByRole('button', { name: /Route 2/ })).toHaveAttribute('aria-pressed', 'true');
  await act(async () => { resolve(savedSearch('search-1')); });
  expect(window.location.pathname).toBe('/routes/search-2');
  expect(generate).not.toHaveBeenCalled();
});

it('shows a clear error for a saved link that no longer exists', async () => {
  window.history.replaceState(null, '', '/routes/missing');
  vi.mocked(loadSearch).mockRejectedValue(new ApiError({ status: 404, detail: 'This saved search is unavailable.' }));
  render(<App />);
  expect(await screen.findByRole('alert')).toHaveTextContent('This saved search is unavailable.');
  expect(screen.getByRole('heading', { name: 'Where do you start?' })).toBeInTheDocument();
  expect(generate).not.toHaveBeenCalled();
});

it('opens a found route without recalculation, edits its markers and saves the exact preview as a new search', async () => {
  const source = savedSearch();
  const seed = { ...candidate(), id: 'exact-edit-preview' };
  vi.mocked(generate).mockResolvedValue(source.candidates);
  vi.mocked(editRoute).mockResolvedValue({ preview: seed, request: source.request,
    points: [{ latitude: 52.1, longitude: 5.13 }], isLoop: true });
  vi.mocked(saveRoute).mockResolvedValue([{ ...candidate(), id: 'edited-route', searchId: 'edited-search', expiresAt: null }]);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: 'Edit route 1' }));
  expect(await screen.findByRole('button', { name: 'Save changes' })).toBeEnabled();
  expect(screen.queryByRole('complementary', { name: 'Route planner' })).not.toBeInTheDocument();
  expect(screen.getByTestId('draft-map')).toHaveTextContent('exact-edit-preview');
  await new Promise(resolve => setTimeout(resolve, 600));
  expect(previewRoute).not.toHaveBeenCalled();
  await userEvent.click(screen.getByRole('button', { name: 'Move first fixture' }));
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  expect(previewRoute).toHaveBeenCalledWith(expect.objectContaining({
    points: [{ latitude: 52.095, longitude: 5.13 }, source.request.start],
  }), expect.any(AbortSignal));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled());
  await userEvent.click(screen.getByRole('button', { name: 'Save changes' }));
  expect(saveRoute).toHaveBeenCalledWith(expect.objectContaining({ drawnRouteId: 'drawn-1', isEditedRoute: true, candidateCount: 1, avoidBacktracking: true }), expect.any(AbortSignal));
  expect(window.location.pathname).toBe('/routes/edited-search');
  expect(await screen.findByRole('heading', { name: 'Your route' })).toBeInTheDocument();
  expect(generate).toHaveBeenCalledTimes(1);
});

it('adds points while editing from a line handle or one deliberate map tap', async () => {
  const source = savedSearch();
  vi.mocked(generate).mockResolvedValue(source.candidates);
  vi.mocked(editRoute).mockResolvedValue({ preview: { ...candidate(), id: 'seed' }, request: source.request,
    points: [{ latitude: 52.1, longitude: 5.13 }], isLoop: true });
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: 'Edit route 1' }));
  await screen.findByRole('button', { name: 'Save changes' });
  // Without the Add tool, a map tap only pans or deselects.
  expect(screen.getByRole('button', { name: 'Set browse fixture' })).toBeInTheDocument();
  await userEvent.click(screen.getByRole('button', { name: 'Insert from line fixture' }));
  expect(screen.getByText('2 points')).toBeInTheDocument();
  expect(screen.getByText('Point 2 selected')).toBeInTheDocument();
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  expect(vi.mocked(previewRoute).mock.calls[0][0].points).toEqual([{ latitude: 52.1, longitude: 5.13 }, { latitude: 52.2, longitude: 5.2 }, source.request.start]);
  await userEvent.click(screen.getByRole('button', { name: 'Add' }));
  expect(screen.getByRole('button', { name: 'Tap map' })).toHaveAttribute('aria-pressed', 'true');
  await userEvent.click(screen.getByRole('button', { name: 'Set draw fixture' }));
  expect(screen.getByText('3 points')).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Set browse fixture' })).toBeInTheDocument();
  expect(screen.getByRole('button', { name: /^Remove point \d$/ })).toBeEnabled();
});

it('restores a saved complete route with a closed loop and no duplicate finish marker or new routing calls', async () => {
  const saved = savedSearch('manual', true);
  saved.request.isEditedRoute = true;
  saved.drawnPoints.push(saved.request.start);
  window.history.replaceState(null, '', '/routes/manual');
  vi.mocked(loadSearch).mockResolvedValue(saved);
  render(<App />);
  await screen.findByRole('button', { name: /Route 1/ });
  expect(screen.queryByRole('region', { name: 'Drawn route section' })).not.toBeInTheDocument();
  await new Promise(resolve => setTimeout(resolve, 600));
  expect(previewRoute).not.toHaveBeenCalled();
  await userEvent.click(screen.getByRole('button', { name: 'Move first fixture' }));
  await screen.findByRole('button', { name: 'Save changes' });
  expect(screen.getByText('1 point')).toBeInTheDocument();
  await waitFor(() => expect(previewRoute).toHaveBeenCalledTimes(1), { timeout: 2000 });
  expect(vi.mocked(previewRoute).mock.calls[0][0].points.at(-1)).toEqual(saved.request.start);
});

it('can undo deleting the last control point and restores the exact original preview', async () => {
  const source = savedSearch();
  vi.mocked(generate).mockResolvedValue(source.candidates);
  vi.mocked(editRoute).mockResolvedValue({ preview: { ...candidate(), id: 'original-exact' }, request: source.request,
    points: [{ latitude: 52.1, longitude: 5.13 }], isLoop: true });
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: 'Edit route 1' }));
  await screen.findByRole('button', { name: 'Save changes' });
  expect(screen.getByRole('button', { name: 'Remove point' })).toBeDisabled();
  await userEvent.click(screen.getByRole('button', { name: 'Select first fixture' }));
  await userEvent.click(screen.getByRole('button', { name: 'Remove point 1' }));
  expect(await screen.findByRole('alert')).toHaveTextContent('Add a point on the map to shape this loop.');
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled());
  expect(screen.getByTestId('draft-map')).toHaveTextContent('original-exact');
  expect(previewRoute).not.toHaveBeenCalled();
});

it('keeps the last valid line after a failed edit, blocks saving and recovers with undo', async () => {
  const source = savedSearch();
  vi.mocked(generate).mockResolvedValue(source.candidates);
  vi.mocked(editRoute).mockResolvedValue({ preview: { ...candidate(), id: 'last-valid-route' }, request: source.request,
    points: [{ latitude: 52.1, longitude: 5.13 }], isLoop: true });
  vi.mocked(previewRoute).mockRejectedValue(new ApiError({ status: 502, detail: 'The routing service returned an invalid response.' }));
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: 'Edit route 1' }));
  await screen.findByRole('button', { name: 'Save changes' });
  await userEvent.click(screen.getByRole('button', { name: 'Move first fixture' }));
  expect(await screen.findByRole('alert', {}, { timeout: 2500 })).toHaveTextContent('The last valid route is still shown.');
  expect(screen.getByTestId('last-valid-map')).toHaveTextContent('last-valid-route');
  expect(screen.getByTestId('draft-map')).toBeEmptyDOMElement();
  expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled();
  expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument();
  fireEvent.keyDown(window, { key: 'z', ctrlKey: true });
  await waitFor(() => expect(screen.getByRole('button', { name: 'Save changes' })).toBeEnabled());
  expect(screen.getByTestId('draft-map')).toHaveTextContent('last-valid-route');
  expect(previewRoute).toHaveBeenCalledTimes(1);
});

it('cancels editing back to the saved routes and remembers a dismissed tip', async () => {
  window.localStorage.removeItem('meander.editHintDismissed');
  const source = savedSearch();
  window.history.replaceState(null, '', '/routes/search-1');
  vi.mocked(loadSearch).mockResolvedValue(source);
  vi.mocked(editRoute).mockResolvedValue({ preview: { ...candidate(), id: 'seed' }, request: source.request,
    points: [{ latitude: 52.1, longitude: 5.13 }], isLoop: true });
  render(<App />);
  await userEvent.click(await screen.findByRole('button', { name: 'Edit route 1' }));
  await userEvent.click(await screen.findByRole('button', { name: 'Dismiss tip' }));
  expect(screen.queryByText(/Drag a point to reshape/)).not.toBeInTheDocument();
  expect(window.localStorage.getItem('meander.editHintDismissed')).toBe('1');
  await userEvent.click(screen.getByRole('button', { name: 'Cancel editing' }));
  expect(await screen.findByRole('button', { name: /Route 2/ })).toBeInTheDocument();
  expect(screen.queryByRole('button', { name: 'Save changes' })).not.toBeInTheDocument();
});

it('follows a Dutch browser and switches to English with the toggle', async () => {
  const languages = vi.spyOn(navigator, 'languages', 'get').mockReturnValue(['nl-NL']);
  try {
    render(<App />);
    expect(await screen.findByRole('heading', { name: 'Waar begin je?' })).toBeInTheDocument();
    expect(document.documentElement.lang).toBe('nl');
    expect(screen.getByRole('button', { name: 'NL' })).toHaveAttribute('aria-pressed', 'true');
    fireEvent.click(screen.getByRole('button', { name: 'EN' }));
    expect(screen.getByRole('button', { name: 'EN' })).toHaveAttribute('aria-pressed', 'true');
    expect(await screen.findByRole('heading', { name: 'Where do you start?' })).toBeInTheDocument();
    expect(localStorage.getItem('meander.language')).toBe('en');
  } finally { languages.mockRestore(); }
});

it('shows translated route warnings in Dutch', async () => {
  localStorage.setItem('meander.language', 'nl');
  vi.mocked(generate).mockResolvedValue([{ ...candidate(), warnings: [
    { code: 'startSnapped', message: 'Start moved 40 m to the nearest routable path.', params: { meters: 40 } }] }]);
  render(<App />);
  await userEvent.click(screen.getByRole('button', { name: 'Set start fixture' }));
  await userEvent.click(screen.getByRole('button', { name: /Volgende: afstand/ }));
  await userEvent.click(screen.getByRole('button', { name: /Volgende: voorkeuren/ }));
  await userEvent.click(screen.getByRole('button', { name: /Vind mijn routes/ }));
  expect(await screen.findByText('Start 40 m verplaatst naar het dichtstbijzijnde pad.')).toBeInTheDocument();
});

it('sends a route to a connected Garmin account', async () => {
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockResolvedValue({ externalId: '1', viewUrl: null });
  vi.mocked(generate).mockResolvedValue(savedSearch().candidates);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click((await screen.findAllByRole('button', { name: /Send to Garmin/ }))[0]);
  expect(sendRoute).toHaveBeenCalledWith('route-1', 'garmin', expect.any(AbortSignal));
  expect(await screen.findByText(/Route sent to Garmin/)).toBeInTheDocument();
  expect(screen.getByText('Garmin connected')).toBeInTheDocument();
});

it('links to Garmin when it is not connected yet', async () => {
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: false }]);
  vi.mocked(generate).mockResolvedValue(savedSearch().candidates);
  render(<App />); await chooseStart(); await toPreferences(); await search();
  const link = (await screen.findAllByRole('link', { name: /Send to Garmin/ }))[0];
  expect(link).toHaveAttribute('href', connectUrl('garmin', '/routes/search-1?route=route-1&send=garmin'));
  expect(sendRoute).not.toHaveBeenCalled();
});

it('sends the route automatically after returning from Garmin', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-2&send=garmin&connected=garmin');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1'));
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockResolvedValue({ externalId: '1', viewUrl: null });
  render(<App />);
  await waitFor(() => expect(sendRoute).toHaveBeenCalledWith('route-2', 'garmin', expect.any(AbortSignal)));
  expect(sendRoute).toHaveBeenCalledTimes(1);
  expect(window.location.search).toBe('?route=route-2');
});

it('only connects, without sending, when returning from the connections panel', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-2&connected=garmin');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1'));
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  render(<App />);
  expect(await screen.findByText('Garmin connected')).toBeInTheDocument();
  expect(sendRoute).not.toHaveBeenCalled();
  expect(window.location.search).toBe('?route=route-2');
});

it('shows the Preparing label on the export button that was clicked', async () => {
  vi.mocked(generate).mockResolvedValue([candidate()]);
  let finish: () => void = () => {};
  vi.mocked(downloadFit).mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
  render(<App />); await chooseStart(); await toPreferences(); await search();
  await userEvent.click(await screen.findByRole('button', { name: /Download FIT/ }));
  expect(await screen.findByRole('button', { name: /Preparing/ })).toBeDisabled();
  expect(screen.getByRole('button', { name: /Download GPX/ })).toBeDisabled();
  finish();
  expect(await screen.findByRole('button', { name: /Download FIT/ })).toBeEnabled();
});

it('reports a cancelled connection and an expired one', async () => {
  window.history.replaceState(null, '', '/routes/search-1?route=route-1&connectError=garmin');
  vi.mocked(loadSearch).mockResolvedValue(savedSearch('search-1'));
  vi.mocked(listConnections).mockResolvedValue([{ service: 'garmin', connected: true }]);
  vi.mocked(sendRoute).mockRejectedValue(new ApiError({ status: 401, code: 'connectionExpired' }));
  render(<App />);
  expect(await screen.findByRole('alert')).toHaveTextContent('The connection was cancelled');
  await userEvent.click((await screen.findAllByRole('button', { name: /Send to Garmin/ }))[0]);
  expect(await screen.findByRole('alert')).toHaveTextContent('The connection has expired');
  expect((await screen.findAllByRole('link', { name: /Send to Garmin/ }))[0]).toBeInTheDocument();
});
