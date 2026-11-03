import '@testing-library/jest-dom/vitest';
import { cleanup } from '@testing-library/react';
afterEach(() => {
  cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState(null, '', '/');
  localStorage.removeItem('meander.theme'); document.documentElement.removeAttribute('data-theme');
  document.documentElement.style.removeProperty('color-scheme');
});
