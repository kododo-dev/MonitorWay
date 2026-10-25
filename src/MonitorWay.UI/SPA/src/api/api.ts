import type { WayApi } from '@kododo-dev/way-shell';
import type {
  AroundPage, EntriesPage, EntryDetail, EntryRow, ErrorDetail, ErrorQuery, ErrorsPage, ErrorStats, Instrument, MetricSeriesPage,
  MetricStats, Overview, Query, ServiceOverview, ServiceRow, Source, Stats, TraceDetail, TraceQuery, TracesPage, TraceStats,
} from './api.model';

// Errors come from the shell's API client: ApiError (problem details), ForbiddenError, SessionExpiredError (the shell
// shows its sign-in dialog).
export { ApiError } from '@kododo-dev/way-shell';

let current: WayApi | null = null;

/** Sets the shell's API client of this Way; the App does it before anything is fetched. */
export function setApi(api: WayApi) {
  current = api;
}

function client(): WayApi {
  if (!current) throw new Error('The API client is not set.');
  return current;
}

const post = <T,>(request: string, body?: unknown): Promise<T> => client().post<T>(request, body ?? {});

export const fetchSources = (): Promise<Source[]> =>
  post('GetSources');

export const fetchEntries = (
  sourceId: string,
  query: Query,
  cursor: { before?: number; after?: number } = {},
  count = 300,
): Promise<EntriesPage> =>
  post('GetEntries', { sourceId, query, count, ...cursor });

/** Categories of a source with entry counts, most frequent first. */
export const fetchCategories = (sourceId: string): Promise<{ name: string; count: number }[]> =>
  post('GetCategories', { sourceId });

/** Where entries, warnings and errors are along the whole source, for the overview bar. */
export const fetchOverview = (sourceId: string, buckets: number): Promise<Overview | null> =>
  post('GetOverview', { sourceId, buckets });

/**
 * Entries around one entry (`id`), around the first entry at or after a moment (`time`), or at or after a
 * position in id space (`position`, from the overview bar).
 */
export const fetchAround = (
  sourceId: string,
  anchor: { id?: number; time?: string; position?: number },
  query: Query,
  count = 200,
): Promise<AroundPage> =>
  post('GetAround', { sourceId, ...anchor, query, count });

export const fetchEntry = (sourceId: string, id: number): Promise<EntryDetail | null> =>
  post('GetEntry', { sourceId, id });

export const fetchStats = (sourceId: string): Promise<Stats | null> =>
  post('GetStats', { sourceId });

/** Stored traces, newest first; `before` is the id of the last trace of the previous page. */
export const fetchTraces = (query: TraceQuery, before?: number, count = 100): Promise<TracesPage> =>
  post('GetTraces', { query, before, count });

/** A trace with all its spans, including those still coming; `null` when it is not in memory. */
export const fetchTrace = (traceId: string): Promise<TraceDetail | null> =>
  post('GetTrace', { traceId });

export const fetchTraceStats = (): Promise<TraceStats> =>
  post('GetTraceStats');

let tracesExported: Promise<boolean> | null = null;

/** Whether the application exports traces to MonitorWay, asked once: it is set at startup. */
export function areTracesExported(): Promise<boolean> {
  tracesExported ??= fetchTraceStats().then(s => s.exported, () => {
    tracesExported = null;
    return false;
  });
  return tracesExported;
}

/** Error groups, the latest seen first or, by `count`, the most frequent first. */
export const fetchErrors = (query: ErrorQuery, sort: 'recent' | 'count', count = 200): Promise<ErrorsPage> =>
  post('GetErrors', { query, sort, count });

/** An error group with its stack trace and latest occurrences; `null` when it is not in memory. */
export const fetchError = (fingerprint: string): Promise<ErrorDetail | null> =>
  post('GetError', { fingerprint });

export const fetchErrorStats = (): Promise<ErrorStats> =>
  post('GetErrorStats');

export const fetchMetrics = (): Promise<Instrument[]> =>
  post('GetMetrics');

export interface SeriesRequest {
  key: string;
  from?: string;
  to?: string;
  stat?: string;
  groupBy?: string[];
  filter?: { key: string; value: string | null }[];
  maxGroups?: number;
}

/** The series of an instrument; `null` when it is not in memory. */
export const fetchMetricSeries = (request: SeriesRequest): Promise<MetricSeriesPage | null> =>
  post('GetMetricSeries', request);

export const fetchServiceOverview = (from?: string, to?: string, service?: string | null): Promise<ServiceOverview> =>
  post('GetServiceOverview', { from, to, service: service ?? undefined });

/** The services telemetry came from: the application and those sending over OTLP. */
export const fetchServices = (): Promise<ServiceRow[]> =>
  post('GetServices');

export const fetchMetricStats = (): Promise<MetricStats> =>
  post('GetMetricStats');

export interface LiveHandlers {
  onEntries: (entries: EntryRow[]) => void;
  onGap: (count: number) => void;
  /** The source's ids are no longer valid (a file was truncated or replaced); load it again. */
  onReset: () => void;
  onError: () => void;
}

/**
 * Opens the live stream of entries newer than `after`, or appended from now on when it is omitted.
 * Entries are delivered in batches (at most every 200 ms) so a burst of logs does not re-render the
 * list per entry.
 */
export function openLiveStream(sourceId: string, query: Query, after: number | undefined, handlers: LiveHandlers): () => void {
  const params = new URLSearchParams({ source: sourceId });
  if (after !== undefined) params.set('after', String(after));
  if (query.levels?.length) params.set('levels', query.levels.join(','));
  if (query.text) params.set('text', query.text);
  if (query.regex) params.set('regex', 'true');
  if (query.caseSensitive) params.set('caseSensitive', 'true');
  if (query.from) params.set('from', query.from);
  if (query.to) params.set('to', query.to);
  query.categories?.forEach(c => params.append('category', c));
  query.excludedCategories?.forEach(c => params.append('excludedCategory', c));

  const source = new EventSource(`${client().url('stream')}?${params}`);
  let pending: EntryRow[] = [];
  const timer = window.setInterval(() => {
    if (pending.length) {
      handlers.onEntries(pending);
      pending = [];
    }
  }, 200);

  source.addEventListener('entry', e => pending.push(JSON.parse((e as MessageEvent).data)));
  source.addEventListener('gap', e => handlers.onGap(Number((e as MessageEvent).data)));
  source.addEventListener('reset', () => {
    source.close();
    handlers.onReset();
  });
  source.onerror = () => handlers.onError();

  return () => {
    window.clearInterval(timer);
    source.close();
  };
}
