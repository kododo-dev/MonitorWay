import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import { TIME_PRESETS, type TimePreset, type TimeRange } from '../utils/timeRange';

const PRESET_SECONDS: Record<TimePreset, number> = { '15m': 900, '1h': 3600, '6h': 21600, '24h': 86400, '7d': 604800 };

/**
 * The time range of a page of charts, in the address: a preset (`t=1h`, the default) or the range dragged on a chart
 * (`from` and `to`). `bounds()` reads it at the moment it is called, so a preset follows the clock.
 */
export function useChartRange(): [TimeRange, (range: TimeRange) => void, () => { from: string; to?: string }] {
  const [params, setParams] = useSearchParams();
  const range = useMemo<TimeRange>(() => {
    const from = params.get('from');
    const to = params.get('to');
    if (from && to) return { from, to };
    const preset = params.get('t');
    return { preset: (TIME_PRESETS as readonly string[]).includes(preset ?? '') ? preset as TimePreset : '1h' };
  }, [params]);

  const setRange = useCallback((next: TimeRange) => {
    setParams(prev => {
      const updated = new URLSearchParams(prev);
      updated.delete('t');
      updated.delete('from');
      updated.delete('to');
      if (next.preset && next.preset !== '1h') updated.set('t', next.preset);
      if (next.from && next.to) {
        updated.set('from', next.from);
        updated.set('to', next.to);
      }
      return updated;
    }, { replace: true });
  }, [setParams]);

  const bounds = useCallback(() => range.preset
    ? { from: new Date(Date.now() - PRESET_SECONDS[range.preset] * 1000).toISOString() }
    : { from: range.from!, to: range.to }, [range]);

  return [range, setRange, bounds];
}
