import { describe, expect, it } from 'vitest';
import { formatNumber, formatValue, unitOf } from './units';

describe('formatNumber', () => {
  it('stays short', () => {
    expect(formatNumber(1234)).toBe('1,234');
    expect(formatNumber(12.5)).toBe('12.5');
    expect(formatNumber(0.042)).toBe('0.042');
    expect(formatNumber(1_200_000)).toBe('1.2M');
    expect(formatNumber(0)).toBe('0');
  });
});

describe('formatValue', () => {
  it('reads the unit', () => {
    expect(formatValue(0.25, 's')).toBe('250 ms');
    expect(formatValue(2048, 'By')).toBe('2.0 KB');
    expect(formatValue(3, '{request}')).toBe('3');
    expect(formatValue(1.5, '{request}', true)).toBe('1.5/s');
    expect(formatValue(120, 'PLN')).toBe('120 PLN');
    expect(formatValue(null, 's')).toBe('-');
  });

  it('counts of a duration histogram have no unit', () => {
    expect(unitOf('count', 's')).toBeNull();
    expect(unitOf('p95', 's')).toBe('s');
  });
});
