export type IconName =
  | 'menu' | 'x' | 'plus' | 'refresh' | 'chevron-left' | 'chevron-right' | 'chevron-down'
  | 'folder' | 'arrow-up' | 'arrow-down' | 'stop' | 'check' | 'alert' | 'shield' | 'terminal'
  | 'monitor' | 'unlink' | 'sparkles' | 'clock' | 'search' | 'sun' | 'moon' | 'contrast'
  | 'link' | 'file' | 'message' | 'cpu' | 'qr';

// Stroke icons on a 24px grid; every path inherits currentColor so CSS controls tone.
const paths: Record<IconName, string[]> = {
  menu: ['M4 7h16', 'M4 12h16', 'M4 17h10'],
  x: ['M6 6l12 12', 'M18 6L6 18'],
  plus: ['M12 5v14', 'M5 12h14'],
  refresh: ['M20 11a8 8 0 0 0-14.7-4.3L4 8', 'M4 4v4h4', 'M4 13a8 8 0 0 0 14.7 4.3L20 16', 'M20 20v-4h-4'],
  'chevron-left': ['M15 6l-6 6 6 6'],
  'chevron-right': ['M9 6l6 6-6 6'],
  'chevron-down': ['M6 9l6 6 6-6'],
  folder: ['M3.5 7.5A2 2 0 0 1 5.5 5.5h3.6l2 2.2h7.4a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z'],
  'arrow-up': ['M12 19V5', 'M6 11l6-6 6 6'],
  'arrow-down': ['M12 5v14', 'M6 13l6 6 6-6'],
  stop: ['M7.5 6.5h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1h-9a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1z'],
  check: ['M5 12.5l4.5 4.5L19 7.5'],
  alert: ['M12 4l9 16H3z', 'M12 10v4', 'M12 17.2v.1'],
  shield: ['M12 3l7 3v5.5c0 4.4-3 8.1-7 9.5-4-1.4-7-5.1-7-9.5V6z', 'M9 12l2 2 4-4'],
  terminal: ['M4.5 5.5h15a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1h-15a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1z', 'M7.5 10l2.5 2-2.5 2', 'M12.5 14.5h4'],
  monitor: ['M4 5h16a1 1 0 0 1 1 1v9.5a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z', 'M9 20h6', 'M12 16.5V20'],
  unlink: ['M9.5 14.5l-1.8 1.8a3 3 0 0 1-4.2-4.2l2.6-2.6', 'M14.5 9.5l1.8-1.8a3 3 0 0 1 4.2 4.2l-2.6 2.6', 'M4 4l16 16'],
  sparkles: ['M12 4l1.6 4.4L18 10l-4.4 1.6L12 16l-1.6-4.4L6 10l4.4-1.6z', 'M18.5 15.5l.7 1.8 1.8.7-1.8.7-.7 1.8-.7-1.8-1.8-.7 1.8-.7z'],
  clock: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M12 7.5V12l3 2'],
  search: ['M11 18a7 7 0 1 0 0-14 7 7 0 0 0 0 14z', 'M20 20l-4-4'],
  sun: ['M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z', 'M12 2.5v2', 'M12 19.5v2', 'M4.6 4.6l1.4 1.4', 'M18 18l1.4 1.4', 'M2.5 12h2', 'M19.5 12h2', 'M4.6 19.4L6 18', 'M18 6l1.4-1.4'],
  moon: ['M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z'],
  contrast: ['M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z', 'M12 3v18', 'M12 7.5h5.5', 'M12 12h8.5', 'M12 16.5h5.5'],
  link: ['M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1', 'M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1'],
  file: ['M7 3.5h6.5L18 8v11.5a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1v-15a1 1 0 0 1 1-1z', 'M13 3.5V8h5'],
  message: ['M5 5h14a1 1 0 0 1 1 1v9.5a1 1 0 0 1-1 1h-8l-4.5 3.5v-3.5H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z'],
  cpu: ['M7.5 7.5h9v9h-9z', 'M10 3.5v4', 'M14 3.5v4', 'M10 16.5v4', 'M14 16.5v4', 'M3.5 10h4', 'M3.5 14h4', 'M16.5 10h4', 'M16.5 14h4'],
  qr: ['M4 4h6v6H4z', 'M14 4h6v6h-6z', 'M4 14h6v6H4z', 'M14 14h2.5', 'M19 14h1v2.5', 'M14 17.5v2.5h2.5', 'M19.5 19.5h.5'],
};

export function Icon({ name, size = 18, className }: { name: IconName; size?: number; className?: string }) {
  return (
    <svg
      className={`icon ${className ?? ''}`}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.8}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {paths[name].map((d) => <path key={d} d={d} />)}
    </svg>
  );
}

/** Product mark: a prompt chevron and cursor with a live signal dot. */
export function BrandMark({ size = 32, className }: { size?: number; className?: string }) {
  return (
    <svg className={`brand-mark ${className ?? ''}`} width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" focusable="false">
      <rect width="64" height="64" rx="18" className="brand-mark-tile" />
      <path d="M19 22l10 10-10 10" className="brand-mark-chevron" />
      <path d="M34 42h12" className="brand-mark-cursor" />
      <circle cx="47" cy="19" r="4.5" className="brand-mark-signal" />
    </svg>
  );
}
