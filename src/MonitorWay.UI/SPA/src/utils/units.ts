import { formatBytes } from './format';
import { formatMs } from './traces';

/** A number for people: `1,234`, `12.5`, `0.042`, `1.2M`. */
export function formatNumber(value: number): string {
  const abs = Math.abs(value);
  if (abs >= 1e9) return `${(value / 1e9).toFixed(1)}G`;
  if (abs >= 1e6) return `${(value / 1e6).toFixed(1)}M`;
  if (abs >= 1e4) return `${(value / 1e3).toFixed(1)}k`;
  if (abs >= 100 || Number.isInteger(value)) return Math.round(value).toLocaleString('en-US');
  if (abs >= 1) return value.toFixed(2).replace(/\.?0+$/, '');
  if (abs === 0) return '0';
  return value.toPrecision(2);
}

/**
 * A value of an instrument in its unit (UCUM, as OpenTelemetry names them): `s` and `ms` as durations, `By` as bytes,
 * `{request}` as a plain count, anything else after the number.
 */
export function formatValue(value: number | null | undefined, unit: string | null, perSecond = false): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '-';
  const rate = perSecond ? '/s' : '';
  switch (unit) {
    case 's': return perSecond ? `${formatNumber(value)}${rate}` : formatMs(value * 1000);
    case 'ms': return perSecond ? `${formatNumber(value)}${rate}` : formatMs(value);
    case 'us': return perSecond ? `${formatNumber(value)}${rate}` : formatMs(value / 1000);
    case 'By': return `${formatBytes(Math.round(value))}${rate}`;
    case '%': return `${formatNumber(value)}%${rate}`;
    case '1': case null: case '': return `${formatNumber(value)}${rate}`;
    default:
      return unit.startsWith('{') ? `${formatNumber(value)}${rate}` : `${formatNumber(value)} ${unit}${rate}`;
  }
}

/** What a statistic is for counting units: a count per period stays a count, a rate is per second. */
export const isRate = (stat: string) => stat === 'rate';

/** Statistics that are counts whatever the instrument's unit (a histogram's count, not its seconds). */
export const unitOf = (stat: string, unit: string | null) => (stat === 'count' || stat === 'rate') && unit === 's' ? null : unit;
