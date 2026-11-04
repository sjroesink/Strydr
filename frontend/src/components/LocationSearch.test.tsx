import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useState } from 'react';
import userEvent from '@testing-library/user-event';
import { geocode } from '../api';
import { LocationSearch } from './LocationSearch';
import type { Place } from '../types';

vi.mock('../api', () => ({ geocode: vi.fn() }));
const suggestion = { id: 'utrecht', label: 'Utrecht, Netherlands', location: { latitude: 52.09, longitude: 5.12 } };
function Search() {
  const [text, setText] = useState(''); const [place, setPlace] = useState<Place | null>(null);
  return <LocationSearch label="Start location" placeholder="Search" value={text} place={place}
    onTextChange={value => { setText(value); setPlace(null); }} onPick={value => { setPlace(value); setText(value.label); }} />;
}

beforeEach(() => { vi.mocked(geocode).mockReset(); vi.mocked(geocode).mockResolvedValue([suggestion]); });

it('debounces and allows keyboard selection without requerying the selected place', async () => {
  render(<Search />);
  await userEvent.type(screen.getByRole('combobox'), 'Utrecht');
  expect(geocode).not.toHaveBeenCalled();
  expect(await screen.findByRole('option')).toHaveTextContent('Utrecht, Netherlands');
  expect(geocode).toHaveBeenCalledTimes(1);
  await userEvent.keyboard('{ArrowDown}{Enter}');
  expect(screen.getByRole('combobox')).toHaveValue('Utrecht, Netherlands');
  expect(screen.queryByRole('option')).not.toBeInTheDocument();
  expect(geocode).toHaveBeenCalledTimes(1);
});

it('does not search fewer than three characters', async () => {
  vi.useFakeTimers();
  try {
    render(<Search />); fireEvent.focus(screen.getByRole('combobox'));
    fireEvent.change(screen.getByRole('combobox'), { target: { value: 'Ut' } });
    await act(async () => { vi.advanceTimersByTime(500); });
    expect(geocode).not.toHaveBeenCalled();
  } finally { vi.useRealTimers(); }
});

it('aborts obsolete searches and ignores their late responses', async () => {
  let resolve!: (value: typeof suggestion[]) => void;
  vi.mocked(geocode).mockReturnValueOnce(new Promise(done => { resolve = done; })).mockResolvedValueOnce([]);
  render(<Search />); await userEvent.type(screen.getByRole('combobox'), 'Utrecht');
  await waitFor(() => expect(geocode).toHaveBeenCalledTimes(1));
  const signal = vi.mocked(geocode).mock.calls[0][2];
  await userEvent.type(screen.getByRole('combobox'), ' station');
  expect(signal.aborted).toBe(true);
  await act(async () => { resolve([suggestion]); });
  expect(screen.queryByRole('option')).not.toBeInTheDocument();
  expect(await screen.findByRole('alert')).toHaveTextContent('No places found');
});
