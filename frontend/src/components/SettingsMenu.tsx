import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useI18n } from '../i18n';
import { PwaInstall } from '../PwaInstall';

export function SettingsMenu({ languagePicker, themePicker }: { languagePicker: ReactNode; themePicker: ReactNode }) {
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const toggle = useRef<HTMLButtonElement>(null);
  const id = useId();
  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener('pointerdown', outside);
    return () => document.removeEventListener('pointerdown', outside);
  }, [open]);
  return <div className="settings-menu" ref={root} onKeyDown={event => {
    if (event.key === 'Escape' && open) {
      event.preventDefault(); event.stopPropagation(); setOpen(false); toggle.current?.focus();
    }
  }} onBlur={event => {
    if (event.relatedTarget && !event.currentTarget.contains(event.relatedTarget)) setOpen(false);
  }}>
    <button ref={toggle} type="button" className="settings-toggle round-button floating"
      aria-label={t('settings.title')} title={t('settings.title')} aria-expanded={open} aria-controls={id}
      onClick={() => setOpen(value => !value)}><span aria-hidden="true">⋯</span></button>
    <section id={id} hidden={!open} className="settings-panel" aria-label={t('settings.title')}>
      <div className="settings-section"><h2>{t('language.group')}</h2>{languagePicker}</div>
      <div className="settings-section"><h2>{t('theme.group')}</h2>{themePicker}</div>
      <PwaInstall />
    </section>
  </div>;
}
