import type { SpanInfo } from '../api/api.model';

/** A span in the waterfall: in the order of the tree (parent, then its children by start), with its depth. */
export interface WaterfallRow {
  span: SpanInfo;
  depth: number;
  /** Spans below it, at any depth. */
  descendants: number;
}

/**
 * The spans as the rows of a waterfall. A span whose parent did not reach MonitorWay (it is in another service, or was
 * dropped) starts a tree of its own, in the order of its start.
 */
export function waterfall(spans: SpanInfo[]): WaterfallRow[] {
  const ids = new Set(spans.map(s => s.spanId));
  const children = new Map<string, SpanInfo[]>();
  const roots: SpanInfo[] = [];
  for (const span of spans) {
    if (span.parentSpanId && ids.has(span.parentSpanId) && span.parentSpanId !== span.spanId) {
      const list = children.get(span.parentSpanId) ?? [];
      list.push(span);
      children.set(span.parentSpanId, list);
    } else {
      roots.push(span);
    }
  }

  const byStart = (a: SpanInfo, b: SpanInfo) => a.offsetMs - b.offsetMs;
  const rows: WaterfallRow[] = [];
  const visited = new Set<string>();
  const visit = (span: SpanInfo, depth: number): number => {
    if (visited.has(span.spanId)) return 0;
    visited.add(span.spanId);
    const row: WaterfallRow = { span, depth, descendants: 0 };
    rows.push(row);
    for (const child of (children.get(span.spanId) ?? []).sort(byStart))
      row.descendants += 1 + visit(child, depth + 1);
    return row.descendants;
  };
  roots.sort(byStart).forEach(root => visit(root, 0));
  return rows;
}

/** Rows without the descendants of the collapsed spans. */
export function visibleRows(rows: WaterfallRow[], collapsed: ReadonlySet<string>): WaterfallRow[] {
  const result: WaterfallRow[] = [];
  for (let i = 0; i < rows.length; i++) {
    result.push(rows[i]);
    if (collapsed.has(rows[i].span.spanId)) i += rows[i].descendants;
  }
  return result;
}

/** A duration for people: `850 µs`, `12.3 ms`, `1.24 s`, `2 min 5 s`. */
export function formatMs(ms: number): string {
  if (ms < 1) return `${Math.round(ms * 1000)} µs`;
  if (ms < 10) return `${ms.toFixed(2)} ms`;
  if (ms < 1000) return `${ms < 100 ? ms.toFixed(1) : Math.round(ms)} ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 2 : 1)} s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes} min ${Math.round(seconds - minutes * 60)} s`;
}

/** Evenly spaced ticks for a time axis of `totalMs`: about `count` of them at a round step. */
export function axisTicks(totalMs: number, count = 5): number[] {
  if (totalMs <= 0) return [0];
  const raw = totalMs / count;
  const magnitude = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map(m => m * magnitude).find(s => s >= raw) ?? raw;
  const ticks: number[] = [];
  for (let t = 0; t <= totalMs + 1e-9; t += step) ticks.push(t);
  return ticks;
}

const SERVICE_COLORS = ['#1e88e5', '#8e24aa', '#00897b', '#f4511e', '#6d4c41', '#3949ab', '#c0ca33', '#d81b60'];

/** A steady color per service, so the spans of one service look alike across traces. */
export function serviceColor(service: string | null): string {
  if (!service) return '#757575';
  let hash = 0;
  for (let i = 0; i < service.length; i++) hash = (hash * 31 + service.charCodeAt(i)) | 0;
  return SERVICE_COLORS[Math.abs(hash) % SERVICE_COLORS.length];
}
