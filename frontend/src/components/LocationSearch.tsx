import { useEffect, useId, useState } from 'react';
import { ApiError, geocode } from '../api';
import { problemMessages } from '../apiText';
import { useI18n, type Message } from '../i18n';
import type { Place, Suggestion } from '../types';
import { useOnline } from '../pwa';

type Props = {
  label: string; value: string; place: Place | null; placeholder: string;
  onTextChange: (value: string) => void; onPick: (place: Place) => void;
};

export function LocationSearch({ label, value, place, placeholder, onTextChange, onPick }: Props) {
  const online = useOnline();
  const { t, say, number, language } = useI18n();
  const id = useId();
  const [suggestions, setSuggestions] = useState<Suggestion[]>([]);
  const [focused, setFocused] = useState(false);
  const [active, setActive] = useState(-1);
  const [searching, setSearching] = useState(false);
  const [error, setError] = useState<Message | null>(null);

  useEffect(() => {
    setSuggestions([]); setActive(-1); setError(null); setSearching(false);
    if (!online || value.trim().length < 3 || place || !focused) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      setSearching(true);
      void geocode(value.trim(), language, controller.signal).then(results => {
        if (!controller.signal.aborted) { setSuggestions(results); if (!results.length) setError({ key: 'search.noResults' }); }
      }).catch((ex: unknown) => {
        if (!controller.signal.aborted) setError(ex instanceof ApiError ? problemMessages(ex, number)[0] : { key: 'search.unavailable' });
      }).finally(() => { if (!controller.signal.aborted) setSearching(false); });
    }, 350);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [value, place, focused, language, online]);

  function select(suggestion: Suggestion) {
    onPick({ label: suggestion.label, location: suggestion.location });
    setSuggestions([]); setActive(-1);
  }

  return <div className="location-search">
    <label htmlFor={id}>{label}</label>
    <div className={`search-input ${place ? 'is-set' : ''}`}>
      <span className="location-dot" aria-hidden="true" />
      <input id={id} role="combobox" disabled={!online} autoComplete="off" placeholder={placeholder} value={value}
        aria-autocomplete="list" aria-expanded={focused && suggestions.length > 0}
        aria-controls={`${id}-results`} aria-activedescendant={active >= 0 ? `${id}-${active}` : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
        onChange={e => onTextChange(e.target.value)} onFocus={() => setFocused(true)} onBlur={() => setFocused(false)}
        onKeyDown={e => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setActive(i => Math.min(suggestions.length - 1, i + 1)); }
          if (e.key === 'ArrowUp') { e.preventDefault(); setActive(i => Math.max(0, i - 1)); }
          if (e.key === 'Enter' && suggestions[active]) { e.preventDefault(); select(suggestions[active]); }
          if (e.key === 'Escape') { setSuggestions([]); setActive(-1); }
        }} />
      {searching && <span className="search-spinner" aria-label={t('search.searching')} />}
      {place && <span className="set-check" aria-label={t('search.selected')}>✓</span>}
    </div>
    {focused && suggestions.length > 0 && <ul className="suggestions" id={`${id}-results`} role="listbox" aria-label={t('search.suggestions', { label })}>
      {suggestions.map((suggestion, index) => <li key={suggestion.id} role="option" id={`${id}-${index}`}
        aria-selected={active === index} onMouseDown={e => e.preventDefault()}
        onMouseEnter={() => setActive(index)} onClick={() => select(suggestion)}>{suggestion.label}</li>)}
    </ul>}
    {error && <p id={`${id}-error`} className="field-help field-error" role="alert">{say(error)}</p>}
  </div>;
}
