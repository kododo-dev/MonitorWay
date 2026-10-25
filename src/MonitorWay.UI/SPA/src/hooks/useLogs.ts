import { useCallback, useEffect, useRef, useState } from 'react';
import { fetchAround, fetchEntries, openLiveStream } from '../api/api';
import type { EntryRow, Query } from '../api/api.model';

const PAGE_SIZE = 300;
/**
 * Rows kept in the browser. Loading past it drops rows from the other end, which can be loaded again by
 * scrolling back, so the list stays light however far one scrolls through a large file.
 */
const MAX_ROWS = 20_000;

/** Centre of the view instead of the newest entries: an entry (context), a moment (jump to time) or a position (overview bar). */
export interface Anchor {
  id?: number;
  time?: string;
  position?: number;
}

export interface LogsState {
  rows: EntryRow[];
  hasOlder: boolean;
  hasNewer: boolean;
  loading: boolean;
  loadingOlder: boolean;
  loadingNewer: boolean;
  error: string | null;
  skipped: number;
  /** Changes whenever the rows are replaced by a fresh load (new source, filter or anchor). */
  version: number;
  anchorId: number | null;
  loadOlder: () => void;
  loadNewer: () => void;
}

/**
 * Entries of a source matching `query`. Without an anchor: the newest page first, older pages on demand
 * and, while `live` is on, entries appended as they are logged. With an anchor: a window around it that
 * grows in both directions on demand.
 */
export function useLogs(sourceId: string | null, query: Query, live: boolean, anchor: Anchor | null): LogsState {
  const [rows, setRows] = useState<EntryRow[]>([]);
  const [hasOlder, setHasOlder] = useState(false);
  const [hasNewer, setHasNewer] = useState(false);
  const [loadingOlder, setLoadingOlder] = useState(false);
  const [loadingNewer, setLoadingNewer] = useState(false);
  const [version, setVersion] = useState(0);
  const [anchorId, setAnchorId] = useState<number | null>(null);
  const [reloads, setReloads] = useState(0);

  const generation = useRef(0);
  const rowsRef = useRef(rows);
  const loadingOlderRef = useRef(false);
  const loadingNewerRef = useRef(false);
  // Where the next page starts. Usually the first or last row's id, but a file source that stopped scanning
  // before it found a full page tells where it stopped, so a sparse filter keeps making progress.
  const olderCursor = useRef<number | null>(null);
  const newerCursor = useRef<number | null>(null);

  // What is being shown. Loading state, errors and skipped counts belong to one request; when the source, query or
  // anchor changes they simply stop matching, so nothing has to be reset when a new load starts.
  const requestKey = JSON.stringify([sourceId, query, anchor, reloads]);
  const [loadedKey, setLoadedKey] = useState<string | null>(null);
  const [failure, setFailure] = useState<{ key: string; message: string } | null>(null);
  const [gaps, setGaps] = useState<{ key: string; count: number } | null>(null);
  const loading = loadedKey !== requestKey && failure?.key !== requestKey;
  const error = failure?.key === requestKey ? failure.message : null;
  const skipped = gaps?.key === requestKey ? gaps.count : 0;

  useEffect(() => {
    rowsRef.current = rows;
  }, [rows]);

  useEffect(() => {
    if (!sourceId) return;
    const current = ++generation.current;

    const load = anchor
      ? fetchAround(sourceId, anchor, query, PAGE_SIZE).then(page => ({
          entries: page.entries,
          hasOlder: page.hasOlder,
          hasNewer: page.hasNewer,
          olderCursor: page.olderCursor,
          newerCursor: page.newerCursor,
          anchorId: page.anchorId,
          error: page.error,
        }))
      : fetchEntries(sourceId, query, {}, PAGE_SIZE).then(page => ({
          entries: page.entries,
          hasOlder: page.hasMore,
          hasNewer: false,
          olderCursor: page.cursor ?? page.entries[0]?.id ?? null,
          newerCursor: null,
          anchorId: null,
          error: page.error,
        }));

    load
      .then(page => {
        if (current !== generation.current) return;
        rowsRef.current = page.entries;
        olderCursor.current = page.olderCursor;
        newerCursor.current = page.newerCursor;
        setRows(page.entries);
        setHasOlder(page.hasOlder);
        setHasNewer(page.hasNewer);
        setAnchorId(page.anchorId);
        setFailure(page.error ? { key: requestKey, message: page.error } : null);
        setLoadedKey(requestKey);
        setVersion(current);
      })
      .catch(e => {
        if (current === generation.current) setFailure({ key: requestKey, message: String(e) });
      });
  }, [sourceId, query, anchor, requestKey]);

  useEffect(() => {
    // Live entries follow the newest row; while newer rows were dropped, there is nothing to attach them to.
    if (!sourceId || !live || anchor || hasNewer || error || version === 0 || version !== generation.current) return;

    // Without rows there is no position to continue from; the stream then starts at the current end.
    const after = rowsRef.current.at(-1)?.id;
    return openLiveStream(sourceId, query, after, {
      onEntries: entries =>
        setRows(prev => {
          // The browser reconnects the stream from the original position, so drop what is already shown.
          const last = prev.at(-1)?.id ?? 0;
          const fresh = entries.filter(e => e.id > last);
          if (!fresh.length) return prev;
          const next = prev.concat(fresh);
          if (next.length <= MAX_ROWS) return next;
          setHasOlder(true);
          const kept = next.slice(next.length - MAX_ROWS);
          olderCursor.current = kept[0].id;
          return kept;
        }),
      onGap: count => setGaps(g => ({ key: requestKey, count: (g?.key === requestKey ? g.count : 0) + count })),
      onReset: () => setReloads(r => r + 1),
      onError: () => {},
    });
  }, [sourceId, query, anchor, requestKey, live, version, error, hasNewer]);

  const loadOlder = useCallback(() => {
    const before = olderCursor.current ?? rowsRef.current[0]?.id;
    if (!sourceId || before == null || !hasOlder || loadingOlderRef.current) return;

    const current = generation.current;
    loadingOlderRef.current = true;
    setLoadingOlder(true);

    fetchEntries(sourceId, query, { before }, PAGE_SIZE)
      .then(page => {
        if (current !== generation.current) return;
        olderCursor.current = page.cursor ?? page.entries[0]?.id ?? before;
        setRows(prev => {
          const firstId = prev[0]?.id ?? Number.MAX_SAFE_INTEGER;
          const next = page.entries.filter(e => e.id < firstId).concat(prev);
          if (next.length <= MAX_ROWS) return next;
          const kept = next.slice(0, MAX_ROWS);
          newerCursor.current = kept[kept.length - 1].id;
          setHasNewer(true);
          return kept;
        });
        setHasOlder(page.hasMore);
      })
      .catch(e => setFailure({ key: requestKey, message: String(e) }))
      .finally(() => {
        loadingOlderRef.current = false;
        setLoadingOlder(false);
      });
  }, [sourceId, query, requestKey, hasOlder]);

  const loadNewer = useCallback(() => {
    const after = newerCursor.current ?? rowsRef.current.at(-1)?.id;
    if (!sourceId || after == null || !hasNewer || loadingNewerRef.current) return;

    const current = generation.current;
    loadingNewerRef.current = true;
    setLoadingNewer(true);

    fetchEntries(sourceId, query, { after }, PAGE_SIZE)
      .then(page => {
        if (current !== generation.current) return;
        newerCursor.current = page.cursor ?? page.entries.at(-1)?.id ?? after;
        setRows(prev => {
          const lastId = prev.at(-1)?.id ?? 0;
          const next = prev.concat(page.entries.filter(e => e.id > lastId));
          if (next.length <= MAX_ROWS) return next;
          const kept = next.slice(next.length - MAX_ROWS);
          olderCursor.current = kept[0].id;
          setHasOlder(true);
          return kept;
        });
        setHasNewer(page.hasMore);
      })
      .catch(e => setFailure({ key: requestKey, message: String(e) }))
      .finally(() => {
        loadingNewerRef.current = false;
        setLoadingNewer(false);
      });
  }, [sourceId, query, requestKey, hasNewer]);

  return { rows, hasOlder, hasNewer, loading, loadingOlder, loadingNewer, error, skipped, version, anchorId, loadOlder, loadNewer };
}
