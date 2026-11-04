// Local HTTP fixtures exercise the real API/ORS adapter without paid services.
// This server is test tooling only; it is never included in the app image.
import { createServer } from 'node:http';

const center = [5.12, 52.09];
const destination = (point, bearing, km) => {
  const r = Math.PI / 180, a = km / 6371.0088, lat = point[1] * r, b = bearing * r;
  const nextLat = Math.asin(Math.sin(lat) * Math.cos(a) + Math.cos(lat) * Math.sin(a) * Math.cos(b));
  const lng = point[0] * r + Math.atan2(Math.sin(b) * Math.sin(a) * Math.cos(lat), Math.cos(a) - Math.sin(lat) * Math.sin(nextLat));
  return [lng / r, nextLat / r];
};
const roads = [];
for (let i = -15; i <= 15; i++) {
  roads.push({ type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates: [[4.9 + i * .015, 51.9], [5.3 + i * .015, 52.3]] } });
  roads.push({ type: 'Feature', properties: {}, geometry: { type: 'LineString', coordinates: [[4.9, 51.9 + i * .015], [5.5, 52.1 + i * .015]] } });
}
const style = {
  version: 8, sources: { fixtures: { type: 'geojson', data: { type: 'FeatureCollection', features: roads }, attribution: 'Local test map fixture' } },
  layers: [
    { id: 'land', type: 'background', paint: { 'background-color': '#e5eadb' } },
    { id: 'roads-halo', type: 'line', source: 'fixtures', paint: { 'line-color': '#d3d8c8', 'line-width': 5 } },
    { id: 'roads', type: 'line', source: 'fixtures', paint: { 'line-color': '#faf9f3', 'line-width': 3 } },
  ],
};
const darkStyle = { ...style, layers: style.layers.map(layer => ({ ...layer, paint: { ...layer.paint,
  ...(layer.type === 'background' ? { 'background-color': '#1b1b1b' }
    : { 'line-color': layer.id === 'roads' ? '#514e46' : '#30302d' }),
} })) };
let calls = 0;
let garminCourses = 0;
const server = createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Content-Type', 'application/json');
  const path = new URL(req.url, 'http://127.0.0.1:5091').pathname;
  if (path === '/health') { res.end('{"ok":true}'); return; }
  if (path === '/style.json') { res.end(JSON.stringify(style)); return; }
  if (path === '/style-dark.json') { res.end(JSON.stringify(darkStyle)); return; }
  if (path === '/woodland') {
    const geometry = [[5.07, 52.06], [5.18, 52.06], [5.18, 52.13], [5.07, 52.13], [5.07, 52.06]]
      .map(([lon, lat]) => ({ lon, lat }));
    // No path anchors here: the API still measures woodland on the normal fixture routes.
    res.end(JSON.stringify({ elements: [{ type: 'way', id: 1, tags: { natural: 'wood' }, geometry }] })); return;
  }
  if (path === '/geocode/autocomplete') {
    res.end(JSON.stringify({ features: [{ properties: { gid: 'fixture:utrecht', label: 'Utrecht, Netherlands' }, geometry: { coordinates: center } }] })); return;
  }
  if (path === '/garmin/authorize') {
    const params = new URL(req.url, 'http://127.0.0.1:5091').searchParams;
    const back = new URL(params.get('redirect_uri'));
    back.searchParams.set('code', 'e2e-code');
    back.searchParams.set('state', params.get('state'));
    res.statusCode = 302; res.setHeader('Location', back.toString()); res.end(); return;
  }
  if (path === '/garmin/token') { res.end(JSON.stringify({ access_token: 'e2e-access', refresh_token: 'e2e-refresh', expires_in: 86400 })); return; }
  if (path === '/garmin/user/id') { res.end(JSON.stringify({ userId: 'e2e-user' })); return; }
  if (path === '/garmin/user/registration') { res.statusCode = 204; res.end(); return; }
  if (path === '/garmin/course') { garminCourses++; res.end(JSON.stringify({ courseId: 4242 })); return; }
  if (path === '/garmin/courses') { res.end(JSON.stringify({ count: garminCourses })); return; }
  try {
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    const body = JSON.parse(Buffer.concat(chunks).toString());
    // Only the profiles a self-hosted Meander ORS builds; anything else is a wiring mistake.
    const profile = path.split('/')[3];
    if ((path.startsWith('/v2/snap/') || path.startsWith('/v2/directions/')) && !['foot-walking', 'cycling-regular'].includes(profile)) {
      res.statusCode = 404; res.end('{"error":"unknown profile"}'); return;
    }
    if (path.startsWith('/v2/snap/')) {
      res.end(JSON.stringify({ locations: body.locations.map(location => ({ location, snapped_distance: 0 })) })); return;
    }
    if (path.startsWith('/v2/directions/')) {
      const start = body.coordinates[0], bearing = (calls++ % 3) * 120;
      const points = body.coordinates.length === 1 ? [start, destination(start, bearing, 1.7), destination(start, bearing + 60, 1.7), start] : body.coordinates;
      const extras = {};
      if (body.extra_info?.includes('green')) extras.green = { values: [[0, points.length - 1, 7]] };
      if (body.extra_info?.includes('surface')) extras.surface = { values: [[0, 1, 3], [1, points.length - 1, 10]] };
      res.end(JSON.stringify({ type: 'FeatureCollection', features: [{ type: 'Feature',
        geometry: { type: 'LineString', coordinates: points.map(p => [...p, 12]) },
        properties: { summary: { distance: body.coordinates.length === 2 ? 2000 : 5000 }, ascent: 24, extras },
      }] })); return;
    }
    res.statusCode = 404; res.end('{"error":"not found"}');
  } catch { res.statusCode = 400; res.end('{"error":"bad fixture request"}'); }
});
server.listen(5091, '127.0.0.1', () => console.log('ORS fixture listening on 5091'));
for (const signal of ['SIGTERM', 'SIGINT']) process.on(signal, () => server.close(() => process.exit(0)));
