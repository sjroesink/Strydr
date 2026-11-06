import { expect, it } from 'vitest';
import { buildWorker } from '../pwa/build';
it('pins a content-addressed shell including every emitted hashed asset, never runtime data', () => {
  const files = { 'index.html': '<html>build 1</html>', 'assets/app-123.js': 'js', 'assets/map-worker-abc.js': 'worker', 'assets/app-123.css': 'css', 'api/private.json': 'secret', 'map/tiles.pmtiles': 'private', 'assets/app.js.map': 'source' };
  const worker = buildWorker(files, '__VERSION__ __PRECACHE__');
  expect(worker).toContain('/assets/map-worker-abc.js');
  expect(worker).toContain('/index.html');
  expect(worker).not.toContain('private');
  expect(worker).not.toContain('source');
  expect(worker).not.toContain('__VERSION__');
  expect(buildWorker(files, '__VERSION__a').slice(0, 20)).not.toBe(buildWorker(files, '__VERSION__b').slice(0, 20));
  expect(buildWorker({ ...files, 'index.html': 'new build' }, '__VERSION__')).not.toBe(buildWorker(files, '__VERSION__'));
});
