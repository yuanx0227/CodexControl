import { useCallback, useEffect, useState } from 'react';

export type ThemePreference = 'system' | 'light' | 'dark';

const storageKey = 'codex-control-theme';
const order: ThemePreference[] = ['system', 'light', 'dark'];

export const themeLabels: Record<ThemePreference, string> = {
  system: '跟随系统',
  light: '浅色',
  dark: '深色',
};

function readPreference(): ThemePreference {
  try {
    const stored = localStorage.getItem(storageKey);
    return stored === 'light' || stored === 'dark' ? stored : 'system';
  } catch {
    return 'system';
  }
}

function applyPreference(preference: ThemePreference) {
  const root = document.documentElement;
  if (preference === 'system') delete root.dataset.theme;
  else root.dataset.theme = preference;
  const dark = preference === 'dark' ||
    (preference === 'system' && window.matchMedia?.('(prefers-color-scheme: dark)').matches);
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#0d0e12' : '#f6f6f8');
}

/** Theme preference is a per-browser convenience; the page renders correctly without storage. */
export function useThemePreference() {
  const [preference, setPreference] = useState<ThemePreference>(readPreference);

  useEffect(() => {
    applyPreference(preference);
    try {
      if (preference === 'system') localStorage.removeItem(storageKey);
      else localStorage.setItem(storageKey, preference);
    } catch {
      // Storage can be unavailable in private mode; the in-memory choice still applies.
    }
    if (preference !== 'system' || !window.matchMedia) return;
    const media = window.matchMedia('(prefers-color-scheme: dark)');
    const sync = () => applyPreference('system');
    media.addEventListener('change', sync);
    return () => media.removeEventListener('change', sync);
  }, [preference]);

  const cycle = useCallback(() => {
    setPreference((current) => order[(order.indexOf(current) + 1) % order.length]);
  }, []);

  return { preference, cycle };
}
