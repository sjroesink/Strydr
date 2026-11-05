import type { ReactNode } from 'react';

export type IconName = 'run' | 'walk' | 'bike' | 'green' | 'unpaved' | 'system' | 'sun' | 'moon' | 'download' | 'edit'
  | 'back' | 'next' | 'close' | 'plus' | 'minus' | 'undo' | 'trash' | 'fit' | 'pin' | 'locate' | 'chevron' | 'check' | 'move' | 'link' | 'send' | 'external' | 'reverse';
const shapes: Record<IconName, ReactNode> = {
  run: <><circle cx="16" cy="4" r="2" /><path d="m12 8 4 3 4-1M12 8l-3 5 5 3-1 5M9 13l-4 1-2-3M12 8 8 7 5 10" /></>,
  walk: <><circle cx="14" cy="4" r="2" /><path d="m13 8-2 6 5 3 1 5M11 14l-3 8M13 8l4 4 3 1M13 8l-4 3-2 3" /></>,
  bike: <><circle cx="5.5" cy="17" r="3.5" /><circle cx="18.5" cy="17" r="3.5" /><path d="M5.5 17 9 9h7l2.5 8M9 9l3.5 8h-7M16 9l-1-3h-2.5M7.5 6H10" /></>,
  green: <><path d="M12 3 6 10h3l-5 6h6v5h4v-5h6l-5-6h3L12 3Z" /><path d="M7 21h10" /></>,
  unpaved: <><path d="M10 3c-4 5 5 7 3 11-1 3-5 4-4 7M16 3c-4 5 5 7 3 11-1 3-5 4-4 7" /><path d="M5 5h.01M7 10h.01M4 16h.01M20 5h.01M22 17h.01M5 21h.01" strokeWidth="3" /></>,
  system: <><rect x="3" y="4" width="18" height="13" rx="2" /><path d="M8 21h8M12 17v4" /></>,
  sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.4 1.4M17.6 17.6 19 19M5 19l1.4-1.4M17.6 6.4 19 5" /></>,
  moon: <path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z" />,
  download: <><path d="M12 3v12m-4-4 4 4 4-4M5 15v5h14v-5" /></>,
  edit: <><path d="m15 5 4 4M4 20l4-1L20 7a2.8 2.8 0 0 0-4-4L4 15v5Z" /></>,
  back: <path d="M19 12H5m7 7-7-7 7-7" />,
  next: <path d="M5 12h14m-7-7 7 7-7 7" />,
  close: <path d="M18 6 6 18M6 6l12 12" />,
  plus: <path d="M12 5v14M5 12h14" />,
  minus: <path d="M5 12h14" />,
  undo: <><path d="M9 14 4 9l5-5" /><path d="M4 9h10a6 6 0 0 1 0 12h-3" /></>,
  trash: <path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" />,
  fit: <path d="M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5" />,
  pin: <><path d="M12 21s-7-6.2-7-11.5a7 7 0 0 1 14 0C19 14.8 12 21 12 21Z" /><circle cx="12" cy="9.5" r="2.5" /></>,
  locate: <><circle cx="12" cy="12" r="3" /><circle cx="12" cy="12" r="7" /><path d="M12 2v3M12 19v3M2 12h3M19 12h3" /></>,
  chevron: <path d="m9 6 6 6-6 6" />,
  check: <path d="m5 12 5 5 9-10" />,
  move: <path d="M12 3v18M3 12h18M9 6l3-3 3 3M9 18l3 3 3-3M6 9l-3 3 3 3M18 9l3 3-3 3" />,
  send: <><path d="M21 3 10 14" /><path d="m21 3-7 18-4-7-7-4 18-7Z" /></>,
  link: <><path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1" /><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" /></>,
  reverse: <path d="M7 4 3 8l4 4M3 8h14M17 12l4 4-4 4M21 16H7" />,
  external: <><path d="M14 4h6v6M20 4l-9 9" /><path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" /></>,
};

export function Icon({ name, className = '' }: { name: IconName; className?: string }) {
  return <svg className={`icon ${className}`} width="24" height="24" viewBox="0 0 24 24" fill="none"
    stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
    {shapes[name]}
  </svg>;
}
