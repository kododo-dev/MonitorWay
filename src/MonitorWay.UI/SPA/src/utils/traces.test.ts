import { describe, expect, it } from 'vitest';
import type { SpanInfo } from '../api/api.model';
import { axisTicks, formatMs, visibleRows, waterfall } from './traces';

const span = (spanId: string, parentSpanId: string | null, offsetMs: number): SpanInfo => ({
  spanId, parentSpanId, offsetMs, name: spanId, kind: 'Internal', start: '', durationMs: 1, status: 'Unset',
  statusDescription: null, service: null, scope: null, attributes: [], events: [], links: [],
});

describe('waterfall', () => {
  it('orders spans as a tree, children by start', () => {
    const rows = waterfall([span('b', 'root', 5), span('root', null, 0), span('a', 'root', 1), span('a1', 'a', 2)]);
    expect(rows.map(r => `${r.span.spanId}:${r.depth}`)).toEqual(['root:0', 'a:1', 'a1:2', 'b:1']);
    expect(rows[0].descendants).toBe(3);
    expect(rows[1].descendants).toBe(1);
  });

  it('starts a tree for a span whose parent is missing', () => {
    const rows = waterfall([span('root', null, 0), span('orphan', 'elsewhere', 3)]);
    expect(rows.map(r => `${r.span.spanId}:${r.depth}`)).toEqual(['root:0', 'orphan:0']);
  });

  it('hides the descendants of collapsed spans', () => {
    const rows = waterfall([span('root', null, 0), span('a', 'root', 1), span('a1', 'a', 2), span('b', 'root', 3)]);
    expect(visibleRows(rows, new Set(['a'])).map(r => r.span.spanId)).toEqual(['root', 'a', 'b']);
    expect(visibleRows(rows, new Set(['root'])).map(r => r.span.spanId)).toEqual(['root']);
  });
});

describe('formatMs', () => {
  it('picks a readable unit', () => {
    expect(formatMs(0.25)).toBe('250 µs');
    expect(formatMs(4.5)).toBe('4.50 ms');
    expect(formatMs(42.25)).toBe('42.3 ms');
    expect(formatMs(420)).toBe('420 ms');
    expect(formatMs(1240)).toBe('1.24 s');
    expect(formatMs(125_000)).toBe('2 min 5 s');
  });
});

describe('axisTicks', () => {
  it('uses a round step', () => {
    expect(axisTicks(1000)).toEqual([0, 200, 400, 600, 800, 1000]);
    expect(axisTicks(37)).toEqual([0, 10, 20, 30]);
  });
});
