import { describe, expect, it } from 'vitest';
import { firstLine, formatBytes, formatDuration, formatFullTime, formatRowTime } from './format';
import { resolveRange } from './timeRange';

describe('format', () => {
  it('shows times in local time with a fixed width', () => {
    const iso = new Date(2026, 9, 3, 9, 5, 7, 42).toISOString();

    expect(formatRowTime(iso)).toBe('10-03 09:05:07.042');
    expect(formatFullTime(iso)).toBe('2026-10-03 09:05:07.042');
    expect(formatRowTime(null)).toBe('');
  });

  it('rounds durations to a readable unit', () => {
    expect(formatDuration(42)).toBe('42 s');
    expect(formatDuration(90)).toBe('2 min');
    expect(formatDuration(3 * 3600)).toBe('3 h');
    expect(formatDuration(3 * 86400)).toBe('3 d');
  });

  it('formats sizes in binary units', () => {
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(64 * 1024 * 1024)).toBe('64 MB');
  });

  it('keeps the first line of a message', () => {
    expect(firstLine('one line')).toBe('one line');
    expect(firstLine('first\nsecond')).toBe('first …');
  });
});

describe('resolveRange', () => {
  const now = Date.UTC(2026, 9, 13, 12, 0, 0);

  it('turns a preset into a start without an end', () => {
    expect(resolveRange({ preset: '1h' }, now)).toEqual({ from: '2026-10-13T11:00:00.000Z' });
    expect(resolveRange({ preset: '7d' }, now)).toEqual({ from: '2026-10-06T12:00:00.000Z' });
  });

  it('keeps explicit bounds and all time', () => {
    expect(resolveRange({ from: 'a', to: 'b' }, now)).toEqual({ from: 'a', to: 'b' });
    expect(resolveRange({}, now)).toEqual({ from: undefined, to: undefined });
  });
});
