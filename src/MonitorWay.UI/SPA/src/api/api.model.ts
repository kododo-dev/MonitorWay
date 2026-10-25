export type Level = 'Trace' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Critical';

export const LEVELS: Level[] = ['Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical'];

export interface Source {
  id: string;
  name: string;
  kind: string;
  /** Heading the source is listed under, e.g. a directory or a service. */
  group: string | null;
}

export interface EntryRow {
  id: number;
  timestamp: string | null;
  level: Level | 'None';
  category: string | null;
  message: string;
  hasException: boolean;
  traceId: string | null;
  /** In a view of several sources, the one the entry comes from. */
  member?: string | null;
  /** The span the entry was logged in. */
  spanId?: string | null;
}

export interface OverviewBucket {
  total: number;
  warnings: number;
  /** Error and Critical entries. */
  errors: number;
}

/** Entry counts along a source, in id space from `start` (inclusive) to `end` (exclusive). */
export interface Overview {
  start: number;
  end: number;
  buckets: OverviewBucket[];
  /** Positions are Unix milliseconds instead of ids (a merged source, whose ids are not contiguous). */
  time?: boolean;
}

export interface AroundPage {
  entries: EntryRow[];
  anchorId: number | null;
  hasOlder: boolean;
  hasNewer: boolean;
  olderCursor: number | null;
  newerCursor: number | null;
  error?: string | null;
  version: number;
}

export interface Query {
  levels?: Level[];
  text?: string;
  regex?: boolean;
  caseSensitive?: boolean;
  from?: string;
  to?: string;
  /** Categories to show, whole names. */
  categories?: string[];
  /** Categories to leave out, whole names. */
  excludedCategories?: string[];
}

export interface EntriesPage {
  entries: EntryRow[];
  hasMore: boolean;
  error?: string | null;
  /** Where to continue when the source stopped scanning before it found enough entries. */
  cursor?: number | null;
  version: number;
}

export interface Property {
  key: string;
  value: string | null;
}

export interface EntryDetail {
  id: number;
  timestamp: string | null;
  level: Level | 'None';
  category: string | null;
  message: string;
  messageTemplate: string | null;
  exception: string | null;
  properties: Property[];
  traceId: string | null;
  spanId: string | null;
  /** The error group of the entry's exception. */
  errorFingerprint?: string | null;
}

export interface LevelStats {
  level: Level;
  count: number;
  /** Retention pool of the level; -1 for sources without pools. */
  pool: number;
}

export interface FileDetails {
  path: string;
  length: number;
  indexedBytes: number;
  indexing: boolean;
  format: string | null;
  error: string | null;
}

export interface Pool {
  levels: Level[];
  count: number;
  bytes: number;
  oldest: string | null;
  evicted: number;
  maxEntries: number | null;
  maxBytes: number | null;
  maxAgeSeconds: number | null;
}

export interface Stats {
  levels: LevelStats[];
  pools: Pool[];
  totalBytes: number;
  maxTotalBytes: number;
  lastId: number;
  now: string;
  version: number;
  file: FileDetails | null;
}

export type SpanKind = 'Internal' | 'Server' | 'Client' | 'Producer' | 'Consumer';
export type SpanStatus = 'Unset' | 'Ok' | 'Error';

export interface TraceQuery {
  text?: string;
  from?: string;
  to?: string;
}

export interface TraceRow {
  /** Position in the store, the cursor for paging. */
  id: number;
  traceId: string;
  start: string;
  durationMs: number;
  /** Name of the root span. */
  name: string;
  kind: SpanKind;
  service: string | null;
  services: string[];
  spanCount: number;
  errorCount: number;
  /** HTTP status code of the root span. */
  statusCode: string | null;
}

export interface TracesPage {
  traces: TraceRow[];
  hasMore: boolean;
  error?: string | null;
  lastId: number;
}

export interface SpanEvent {
  name: string;
  offsetMs: number;
  timestamp: string;
  attributes: Property[];
  /** For an exception, its error group. */
  errorFingerprint?: string | null;
}

export interface SpanInfo {
  spanId: string;
  parentSpanId: string | null;
  name: string;
  kind: SpanKind;
  start: string;
  /** From the start of the trace. */
  offsetMs: number;
  durationMs: number;
  status: SpanStatus;
  statusDescription: string | null;
  service: string | null;
  scope: string | null;
  attributes: Property[];
  events: SpanEvent[];
  links: { traceId: string; spanId: string }[];
}

export interface TraceDetail {
  traceId: string;
  /** False while spans of the trace are still coming. */
  isComplete: boolean;
  droppedSpans: number;
  start: string;
  durationMs: number;
  spans: SpanInfo[];
}

export interface TracePool {
  name: 'errors' | 'slow' | 'other';
  count: number;
  bytes: number;
  oldest: string | null;
  evicted: number;
  maxTraces: number | null;
  maxBytes: number | null;
  maxAgeSeconds: number | null;
}

export interface TraceStats {
  /** Whether OpenTelemetry exports traces to MonitorWay. */
  exported: boolean;
  pools: TracePool[];
  pending: number;
  totalBytes: number;
  maxTotalBytes: number;
  slowThresholdMs: number;
  lastId: number;
  now: string;
}

export interface ErrorQuery {
  text?: string;
  from?: string;
  to?: string;
}

export interface ErrorRow {
  fingerprint: string;
  type: string;
  /** Of the latest occurrence. */
  message: string;
  count: number;
  firstSeen: string;
  lastSeen: string;
  services: string[];
  /** Operations it happened in, most frequent first. */
  endpoints: string[];
  /** Occurrences per hour for the last 24 hours, the current hour last. */
  trend: number[];
}

export interface ErrorsPage {
  errors: ErrorRow[];
  error?: string | null;
}

export interface Occurrence {
  timestamp: string;
  message: string;
  service: string | null;
  endpoint: string | null;
  traceId: string | null;
  spanId: string | null;
  logEntryId: number | null;
  category: string | null;
  fromSpan: boolean;
  fromLog: boolean;
}

export interface ErrorDetail {
  fingerprint: string;
  type: string;
  message: string;
  stackTrace: string | null;
  /** The frames the group is recognized by. */
  frames: string[];
  count: number;
  firstSeen: string;
  lastSeen: string;
  services: string[];
  endpoints: string[];
  /** Occurrences per hour for the last 7 days, the current hour last. */
  trend: number[];
  occurrences: Occurrence[];
}

export interface ErrorStats {
  exported: boolean;
  groups: number;
  occurrences: number;
  maxGroups: number;
  maxAgeSeconds: number | null;
}

export type MetricKind = 'Counter' | 'UpDownCounter' | 'Gauge' | 'Histogram';

export interface Instrument {
  /** meter/name */
  key: string;
  meter: string;
  name: string;
  unit: string | null;
  description: string | null;
  kind: MetricKind;
  series: number;
  droppedSeries: number;
  lastUpdate: string;
  /** Attribute keys with how many values each has. */
  attributes: { key: string; values: number }[];
  /** Statistics the series offer, the default first. */
  stats: string[];
}

export interface MetricSeries {
  label: string;
  attributes: Property[];
  values: (number | null)[];
  /** The statistic over the whole range. */
  total: number | null;
  series: number;
}

export interface MetricSeriesPage {
  key: string;
  name: string;
  unit: string | null;
  kind: MetricKind;
  stat: string;
  resolutionSeconds: number;
  /** Unix seconds. */
  times: number[];
  series: MetricSeries[];
  hiddenGroups: number;
  error?: string | null;
}

export interface ServiceRow {
  name: string;
  /** What it sent: logs, traces, metrics. */
  signals: string[];
  lastSeen: string;
}

export interface RouteStats {
  service: string | null;
  method: string | null;
  route: string;
  requests: number;
  errors: number;
  /** Seconds. */
  p50: number | null;
  p95: number | null;
  p99: number | null;
}

export interface ServiceOverview {
  exported: boolean;
  /** Whether the application reports http.server.request.duration. */
  available: boolean;
  resolutionSeconds: number;
  times: number[];
  requests: (number | null)[];
  errors: (number | null)[];
  p50: (number | null)[];
  p95: (number | null)[];
  p99: (number | null)[];
  totalRequests: number;
  totalErrors: number;
  totalP50: number | null;
  totalP95: number | null;
  totalP99: number | null;
  routes: RouteStats[];
}

export interface MetricStats {
  exported: boolean;
  instruments: number;
  series: number;
  droppedSeries: number;
  bytes: number;
  maxTotalBytes: number;
  exportIntervalSeconds: number;
}
