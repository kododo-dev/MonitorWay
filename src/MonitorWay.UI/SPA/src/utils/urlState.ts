import { useCallback, useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { LEVELS, type Level } from '../api/api.model';
import type { CategoryFilter } from './categories';
import { TIME_PRESETS, type TimePreset, type TimeRange } from './timeRange';

/** What the address remembers, so a view can be bookmarked or sent as a link. */
export interface ViewState {
  source: string | null;
  text: string;
  levels: Level[] | null;
  regex: boolean;
  caseSensitive: boolean;
  range: TimeRange;
  categories: CategoryFilter;
}

export function readViewState(params: URLSearchParams): ViewState {
  const levels = params.get('lv')?.split(',').filter((l): l is Level => (LEVELS as string[]).includes(l)) ?? null;
  const preset = params.get('t');
  const only = params.getAll('cat');
  const hidden = params.getAll('xcat');
  return {
    source: params.get('src'),
    text: params.get('q') ?? '',
    levels: levels?.length ? levels : null,
    regex: params.get('re') === '1',
    caseSensitive: params.get('cs') === '1',
    range: preset && (TIME_PRESETS as readonly string[]).includes(preset)
      ? { preset: preset as TimePreset }
      : { from: params.get('from') ?? undefined, to: params.get('to') ?? undefined },
    categories: only.length ? { mode: 'only', names: only } : hidden.length ? { mode: 'hide', names: hidden } : null,
  };
}

export function viewStateParams(state: ViewState): URLSearchParams {
  const params = new URLSearchParams();
  if (state.source) params.set('src', state.source);
  if (state.text) params.set('q', state.text);
  if (state.levels) params.set('lv', state.levels.join(','));
  if (state.regex) params.set('re', '1');
  if (state.caseSensitive) params.set('cs', '1');
  if (state.range.preset) params.set('t', state.range.preset);
  if (state.range.from) params.set('from', state.range.from);
  if (state.range.to) params.set('to', state.range.to);
  state.categories?.names.forEach(name => params.append(state.categories!.mode === 'only' ? 'cat' : 'xcat', name));
  return params;
}

/**
 * The view state of the address: read once when the page opens, then written back as it changes. The module runs under
 * the shell's router, so the parameters are those of its route (`#/logs?q=...`), not of the page's address.
 */
export function useViewState(): [initial: ViewState, write: (state: ViewState) => void] {
  const [params, setParams] = useSearchParams();
  const [initial] = useState(() => readViewState(params));
  const written = useRef(params.toString());

  useEffect(() => {
    written.current = params.toString();
  }, [params]);

  const write = useCallback((state: ViewState) => {
    const next = viewStateParams(state);
    if (next.toString() === written.current) return;
    written.current = next.toString();
    setParams(next, { replace: true });
  }, [setParams]);

  return [initial, write];
}
