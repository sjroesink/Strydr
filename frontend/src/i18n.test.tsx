import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, expect, test, vi } from 'vitest';
import { detectLanguage, I18nProvider, LANGUAGE_KEY, useI18n } from './i18n';

function languages(list: string[]) {
  vi.spyOn(navigator, 'languages', 'get').mockReturnValue(list);
  vi.spyOn(navigator, 'language', 'get').mockReturnValue(list[0] ?? '');
}
afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

test('detects Dutch, falls back to English, and prefers a saved choice', () => {
  languages(['nl-NL', 'en']); expect(detectLanguage()).toBe('nl');
  languages(['de-DE', 'en-GB']); expect(detectLanguage()).toBe('en');
  languages(['fr']); expect(detectLanguage()).toBe('en');
  localStorage.setItem(LANGUAGE_KEY, 'nl'); languages(['en-US']); expect(detectLanguage()).toBe('nl');
});

function Probe() {
  const { t, say, number, setLanguage, language } = useI18n();
  return <div>
    <p>{t('drawing.points', { count: 1 })}|{t('drawing.points', { count: 3 })}</p>
    <p>{say([{ key: 'error.minimumDistance', params: { km: number(2.5, 2) } }, { text: 'raw' }])}</p>
    <button onClick={() => setLanguage(language === 'nl' ? 'en' : 'nl')}>toggle</button>
  </div>;
}

test('translates with plurals, params and locale numbers, and persists a switch', async () => {
  languages(['en-US']);
  render(<I18nProvider><Probe /></I18nProvider>);
  expect(screen.getByText('1 point|3 points')).toBeInTheDocument();
  expect(screen.getByText('Minimum distance: 2.50 km. raw')).toBeInTheDocument();
  await act(() => userEvent.click(screen.getByRole('button', { name: 'toggle' })));
  expect(screen.getByText('1 punt|3 punten')).toBeInTheDocument();
  expect(screen.getByText('Minimale afstand: 2,50 km. raw')).toBeInTheDocument();
  expect(document.documentElement.lang).toBe('nl');
  expect(localStorage.getItem(LANGUAGE_KEY)).toBe('nl');
});
