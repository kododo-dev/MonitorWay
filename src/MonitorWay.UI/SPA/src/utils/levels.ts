import type { Level } from '../api/api.model';

export const LEVEL_SHORT: Record<Level | 'None', string> = {
  Trace: 'TRC',
  Debug: 'DBG',
  Information: 'INF',
  Warning: 'WRN',
  Error: 'ERR',
  Critical: 'CRT',
  None: '???',
};

const LEVEL_COLOR: Record<Level | 'None', { dark: string; light: string }> = {
  Trace: { dark: '#757575', light: '#9e9e9e' },
  Debug: { dark: '#9e9e9e', light: '#757575' },
  Information: { dark: '#64b5f6', light: '#1976d2' },
  Warning: { dark: '#ffb74d', light: '#ef6c00' },
  Error: { dark: '#ef5350', light: '#d32f2f' },
  Critical: { dark: '#ff1744', light: '#b71c1c' },
  None: { dark: '#9e9e9e', light: '#757575' },
};

export const levelColor = (level: Level | 'None', isDark: boolean) =>
  isDark ? LEVEL_COLOR[level].dark : LEVEL_COLOR[level].light;

/** Rows of errors and critical entries get a tinted background, so they stand out while scrolling. */
export const levelRowTint = (level: Level | 'None', isDark: boolean): string | undefined => {
  if (level === 'Critical') return isDark ? 'rgba(255, 23, 68, 0.16)' : 'rgba(183, 28, 28, 0.10)';
  if (level === 'Error') return isDark ? 'rgba(239, 83, 80, 0.10)' : 'rgba(211, 47, 47, 0.06)';
  if (level === 'Warning') return isDark ? 'rgba(255, 183, 77, 0.06)' : 'rgba(239, 108, 0, 0.05)';
  return undefined;
};
