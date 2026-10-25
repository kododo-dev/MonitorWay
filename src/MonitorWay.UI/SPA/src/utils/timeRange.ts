export const TIME_PRESETS = ['15m', '1h', '6h', '24h', '7d'] as const;
export type TimePreset = typeof TIME_PRESETS[number];

/** A time filter: a preset relative to when it was chosen, or explicit bounds (ISO strings). Empty = all time. */
export interface TimeRange {
  preset?: TimePreset;
  from?: string;
  to?: string;
}

const PRESET_MS: Record<TimePreset, number> = {
  '15m': 15 * 60_000,
  '1h': 60 * 60_000,
  '6h': 6 * 60 * 60_000,
  '24h': 24 * 60 * 60_000,
  '7d': 7 * 24 * 60 * 60_000,
};

/** Bounds of a range at `now`. A preset becomes "from now minus the duration", without an end, so live entries keep coming. */
export function resolveRange(range: TimeRange, now = Date.now()): { from?: string; to?: string } {
  if (range.preset) return { from: new Date(now - PRESET_MS[range.preset]).toISOString() };
  return { from: range.from, to: range.to };
}
