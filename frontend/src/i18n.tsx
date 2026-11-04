import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { en } from './locales/en';
import { nl } from './locales/nl';

export type Language = 'nl' | 'en';
export type MessageKey = keyof typeof en;
export type PluralBase = 'drawing.points';
export type Params = Record<string, string | number>;
// UI state keeps descriptors rather than finished text, so a language switch re-renders it.
export type Message = { key: MessageKey; params?: Params } | { text: string };
export const LANGUAGE_KEY = 'meander.language';
const dictionaries: Record<Language, Record<MessageKey, string>> = { en, nl };

function savedLanguage(): Language | null {
  try {
    const value = window.localStorage.getItem(LANGUAGE_KEY);
    return value === 'nl' || value === 'en' ? value : null;
  } catch { return null; }
}

export function detectLanguage(): Language {
  const saved = savedLanguage();
  if (saved) return saved;
  const candidates = navigator.languages?.length ? navigator.languages : [navigator.language];
  for (const tag of candidates) {
    const primary = tag?.toLowerCase().split('-')[0];
    if (primary === 'nl' || primary === 'en') return primary;
  }
  return 'en';
}

type I18n = {
  language: Language;
  setLanguage(language: Language): void;
  t(key: MessageKey | PluralBase, params?: Params): string;
  say(message: Message | Message[] | null | undefined): string;
  number(value: number, digits?: number): string;
};
const I18nContext = createContext<I18n | null>(null);

export function I18nProvider({ children }: { children: ReactNode }) {
  const [language, setLanguageState] = useState<Language>(detectLanguage);
  useEffect(() => { document.documentElement.lang = language; }, [language]);
  const setLanguage = useCallback((next: Language) => {
    setLanguageState(next);
    try { window.localStorage.setItem(LANGUAGE_KEY, next); } catch { /* the choice lasts for this visit */ }
  }, []);
  const value = useMemo<I18n>(() => {
    const dictionary = dictionaries[language];
    const number = (value: number, digits?: number) => new Intl.NumberFormat(language,
      digits === undefined ? { maximumFractionDigits: 1 } : { minimumFractionDigits: digits, maximumFractionDigits: digits }).format(value);
    const t = (key: MessageKey | PluralBase, params?: Params) => {
      const resolved = (`${key}.one` in dictionary
        ? `${key}.${params?.count === 1 ? 'one' : 'other'}` : key) as MessageKey;
      const template = dictionary[resolved] ?? en[resolved] ?? key;
      return template.replace(/\{(\w+)\}/g, (match, name: string) => {
        const param = params?.[name];
        return param === undefined ? match : typeof param === 'number' ? number(param) : param;
      });
    };
    const say = (message: Message | Message[] | null | undefined): string =>
      !message ? '' : Array.isArray(message) ? message.map(say).filter(Boolean).join(' ')
        : 'text' in message ? message.text : t(message.key, message.params);
    return { language, setLanguage, t, say, number };
  }, [language, setLanguage]);
  return <I18nContext.Provider value={value}>{children}</I18nContext.Provider>;
}

export function useI18n(): I18n {
  const context = useContext(I18nContext);
  if (!context) throw new Error('useI18n must be used inside I18nProvider.');
  return context;
}
