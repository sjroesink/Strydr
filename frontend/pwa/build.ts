import { createHash } from 'node:crypto';
import { readFile, readdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import type { Plugin } from 'vite';

// Closed allowlist: no runtime URLs, user data, source maps or third-party map assets.
const shellFile = (path: string) => path === 'index.html' || path === 'manifest.webmanifest'
  || path === 'favicon.svg' || path === 'apple-touch-icon.png'
  || /^icons\/(?:icon|maskable)-(?:192|512)\.png$/.test(path)
  || /^assets\/[^/]+-[\w-]+\.(?:js|css|woff2?)$/.test(path);

export function buildWorker(files: Record<string, string | Buffer>, template: string) {
  const paths = Object.keys(files).filter(shellFile).sort();
  const hash = createHash('sha256').update(template);
  for (const path of paths) hash.update(path).update(files[path]);
  return template.replace('__VERSION__', hash.digest('hex').slice(0, 20))
    .replace('__PRECACHE__', JSON.stringify(paths.map(path => `/${path}`)));
}

export function appShell(): Plugin {
  let outDir: string;
  let root: string;
  return {
    name: 'meander-app-shell', apply: 'build',
    configResolved(config) { root = config.root; outDir = resolve(root, config.build.outDir); },
    async closeBundle() {
      const files: Record<string, Buffer> = {};
      async function collect(dir: string, prefix = '') {
        for (const entry of await readdir(dir, { withFileTypes: true })) {
          const name = prefix + entry.name;
          if (entry.isDirectory()) await collect(resolve(dir, entry.name), `${name}/`);
          else if (shellFile(name)) files[name] = await readFile(resolve(dir, entry.name));
        }
      }
      await collect(outDir);
      const template = await readFile(resolve(root, 'pwa/sw.js'), 'utf8');
      await writeFile(resolve(outDir, 'sw.js'), buildWorker(files, template));
    },
  };
}
