import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  worker: { format: 'es' },
  server: {
    port: 5173, strictPort: true,
    // /map serves local basemap files (PMTiles, glyphs, sprites) when the API has MapData:Path set.
    proxy: Object.fromEntries(['/api', '/map'].map(path => [path, { target: process.env.API_PROXY_TARGET ?? 'http://127.0.0.1:5080', changeOrigin: true }])),
  },
  test: { environment: 'jsdom', globals: true, setupFiles: ['./src/test/setup.ts'], include: ['src/**/*.test.{ts,tsx}'] },
});
