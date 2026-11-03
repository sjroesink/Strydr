import { useEffect, useLayoutEffect, useState } from 'react';

export type ThemePreference = 'system' | 'light' | 'dark';
export type ColorScheme = 'light' | 'dark';
const key = 'meander.theme';

function savedPreference(): ThemePreference {
  try {
    const value = localStorage.getItem(key);
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch { return 'system'; }
}

export function useTheme() {
  const [preference, setPreference] = useState<ThemePreference>(savedPreference);
  const [systemDark, setSystemDark] = useState(() => window.matchMedia?.('(prefers-color-scheme: dark)').matches ?? false);
  const colorScheme: ColorScheme = preference === 'system' ? systemDark ? 'dark' : 'light' : preference;

  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    if (!media) return;
    const update = () => setSystemDark(media.matches);
    update(); media.addEventListener('change', update);
    return () => media.removeEventListener('change', update);
  }, []);

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = colorScheme;
    document.documentElement.style.colorScheme = colorScheme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', colorScheme === 'dark' ? '#181818' : '#faf9f5');
  }, [colorScheme]);

  function changePreference(value: ThemePreference) {
    setPreference(value);
    try { if (value === 'system') localStorage.removeItem(key); else localStorage.setItem(key, value); }
    catch { /* The current choice still works when browser storage is unavailable. */ }
  }
  return { preference, colorScheme, changePreference };
}
