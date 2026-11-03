import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useTheme } from './useTheme';

function Harness() {
  const theme = useTheme();
  return <><output>{theme.colorScheme}</output>
    {(['system', 'light', 'dark'] as const).map(value => <button key={value} onClick={() => theme.changePreference(value)}>{value}</button>)}
  </>;
}
function systemTheme(initial: boolean) {
  let dark = initial;
  const listeners = new Set<() => void>();
  vi.stubGlobal('matchMedia', vi.fn(() => ({ get matches() { return dark; },
    addEventListener: (_event: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_event: string, listener: () => void) => listeners.delete(listener),
  })));
  return (value: boolean) => act(() => { dark = value; listeners.forEach(listener => listener()); });
}

it('uses and follows the system theme by default', () => {
  const changeSystem = systemTheme(true);
  render(<Harness />);
  expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  expect(localStorage.getItem('meander.theme')).toBeNull();
  changeSystem(false);
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
});

it('persists an explicit choice and can return to the system setting', async () => {
  const changeSystem = systemTheme(true);
  const view = render(<Harness />);
  await userEvent.click(screen.getByRole('button', { name: 'light' }));
  expect(localStorage.getItem('meander.theme')).toBe('light');
  changeSystem(false); changeSystem(true);
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  view.unmount(); render(<Harness />);
  expect(document.documentElement).toHaveAttribute('data-theme', 'light');
  await userEvent.click(screen.getByRole('button', { name: 'system' }));
  expect(document.documentElement).toHaveAttribute('data-theme', 'dark');
  expect(localStorage.getItem('meander.theme')).toBeNull();
});
