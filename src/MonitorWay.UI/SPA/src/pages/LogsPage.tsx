import { useCallback, useEffect, useMemo, useState } from 'react';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import InputAdornment from '@mui/material/InputAdornment';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import AccountTreeOutlinedIcon from '@mui/icons-material/AccountTreeOutlined';
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined';
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrong';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import FilterAltOutlinedIcon from '@mui/icons-material/FilterAltOutlined';
import FilterAltOffOutlinedIcon from '@mui/icons-material/FilterAltOffOutlined';
import HelpOutlineIcon from '@mui/icons-material/HelpOutlineOutlined';
import PauseIcon from '@mui/icons-material/Pause';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import { PageHeader } from '@kododo-dev/way-shell';
import { useI18n } from '../i18n/i18n';
import { fetchSources } from '../api/api';
import { LEVELS, type EntryRow, type Level, type Query, type Source } from '../api/api.model';
import { useLogs, type Anchor } from '../hooks/useLogs';
import { useStats } from '../hooks/useStats';
import { useOverview } from '../hooks/useOverview';
import LogList, { type RowAction, type VisibleRange } from '../components/LogList';
import EntryDetails from '../components/EntryDetails';
import LevelChips from '../components/LevelChips';
import CategoryPicker from '../components/CategoryPicker';
import OverviewBar from '../components/OverviewBar';
import SourcePicker from '../components/SourcePicker';
import TimeRangePicker from '../components/TimeRangePicker';
import SearchHelp from '../components/SearchHelp';
import ServicePicker from '../components/ServicePicker';
import { useServices } from '../hooks/useServices';
import { resolveRange, type TimeRange } from '../utils/timeRange';
import { formatBytes, formatCount, formatFullTime } from '../utils/format';
import { addToken, fieldToken, hasToken, highlightPattern, serviceOf, toggleToken, withService } from '../utils/search';
import { useViewState } from '../utils/urlState';
import { parseSelection, SOURCE_SEPARATOR } from '../utils/sources';
import { categoryQuery, hideCategory, onlyCategory, type CategoryFilter } from '../utils/categories';

const EXCEPTIONS_TOKEN = 'has:exception';

/** What the list shows: the newest entries (following live), an entry in its context, or entries around a moment. */
const LATEST: View = { kind: 'latest' };

type View =
  | { kind: 'latest' }
  | { kind: 'context'; id: number; queryKey: string }
  | { kind: 'around'; time: string }
  | { kind: 'position'; position: number; percent: number };

function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(timer);
  }, [value, delayMs]);
  return debounced;
}

/** The logs: the newest entries following live, search, filters, an entry with its context and details. */
const LogsPage = () => {
  const theme = useTheme();
  const { t } = useI18n();
  const compact = useMediaQuery(theme.breakpoints.down('md'));

  const [initial, writeViewState] = useViewState();
  const [sources, setSources] = useState<Source[] | null>(null);
  const [sourceError, setSourceError] = useState<string | null>(null);
  const [chosenSource, setChosenSource] = useState<string | null>(initial.source);
  const [levels, setLevels] = useState<ReadonlySet<Level>>(() => new Set(initial.levels ?? LEVELS));
  const [text, setText] = useState(initial.text);
  const [regex, setRegex] = useState(initial.regex);
  const [caseSensitive, setCaseSensitive] = useState(initial.caseSensitive);
  const [range, setRange] = useState<TimeRange>(initial.range);
  const [categories, setCategories] = useState<CategoryFilter>(initial.categories);
  const [live, setLive] = useState(true);
  const services = useServices();
  const [requestedView, setView] = useState<View>(LATEST);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  useEffect(() => {
    const load = () => fetchSources().then(setSources).catch(e => setSourceError(String(e)));
    load();
    // Files appear and disappear as logs rotate.
    const timer = window.setInterval(load, 15_000);
    return () => window.clearInterval(timer);
  }, []);

  const selection = useMemo(() => (sources ? parseSelection(chosenSource, sources) : []), [sources, chosenSource]);
  const sourceId = selection.length ? selection.join(SOURCE_SEPARATOR) : null;
  const debouncedText = useDebounced(text, 300);

  const query = useMemo<Query>(() => ({
    levels: levels.size === LEVELS.length ? undefined : [...levels],
    text: debouncedText.trim() || undefined,
    regex: regex || undefined,
    caseSensitive: caseSensitive || undefined,
    ...resolveRange(range),
    ...categoryQuery(categories),
  }), [levels, debouncedText, regex, caseSensitive, range, categories]);
  const queryKey = JSON.stringify(query);

  useEffect(() => {
    writeViewState({
      source: chosenSource,
      text: debouncedText.trim(),
      levels: levels.size === LEVELS.length ? null : LEVELS.filter(l => levels.has(l)),
      regex,
      caseSensitive,
      range,
      categories,
    });
  }, [writeViewState, chosenSource, debouncedText, levels, regex, caseSensitive, range, categories]);

  // Context shows the neighbours of an entry with filters off; changing a filter means going back to results.
  const view = requestedView.kind === 'context' && requestedView.queryKey !== queryKey ? LATEST : requestedView;

  const anchor = useMemo<Anchor | null>(
    () => view.kind === 'context' ? { id: view.id }
      : view.kind === 'around' ? { time: view.time }
      : view.kind === 'position' ? { position: view.position }
      : null,
    [view],
  );
  const viewQuery = useMemo<Query>(() => (view.kind === 'context' ? {} : query), [view.kind, query]);

  const logs = useLogs(sourceId, viewQuery, live, anchor);
  const stats = useStats(sourceId);
  const overview = useOverview(sourceId);
  const [visible, setVisible] = useState<VisibleRange | null>(null);
  const filtered = query.levels !== undefined || !!query.text || !!query.from || !!query.to || categories !== null;
  const highlight = useMemo(
    () => (view.kind === 'context' ? null : highlightPattern(debouncedText, regex, caseSensitive)),
    [view.kind, debouncedText, regex, caseSensitive],
  );

  const chooseSource = useCallback((ids: string[]) => {
    setChosenSource(ids.join(SOURCE_SEPARATOR));
    // Another source has other categories.
    setCategories(null);
    setSelectedId(null);
    setView(LATEST);
  }, []);

  // With every level shown, a click narrows the list to that level; after that clicks add and remove levels.
  const toggleLevel = useCallback((level: Level) => {
    setLevels(prev => {
      if (prev.size === LEVELS.length) return new Set([level]);
      const next = new Set(prev);
      if (next.has(level)) next.delete(level);
      else next.add(level);
      return next.size === 0 ? new Set(LEVELS) : next;
    });
  }, []);

  const applyToken = useCallback((token: string) => {
    setRegex(false);
    setText(prev => addToken(regex ? '' : prev, token));
  }, [regex]);

  // A request is shown whole: every level, nothing but the trace filter.
  const showRequest = useCallback((traceId: string) => {
    setRegex(false);
    setLevels(new Set(LEVELS));
    setCategories(null);
    setText(fieldToken('trace', traceId));
    setView(LATEST);
  }, []);

  const showContext = useCallback((id: number) => {
    setSelectedId(id);
    setView({ kind: 'context', id, queryKey });
  }, [queryKey]);

  const rowActions = useCallback((row: EntryRow): RowAction[] => {
    const actions: RowAction[] = [
      { label: t.showContext, icon: <CenterFocusStrongIcon />, onClick: () => showContext(row.id) },
    ];
    if (row.traceId)
      actions.push({ label: t.sameRequest, icon: <AccountTreeOutlinedIcon />, onClick: () => showRequest(row.traceId!) });
    if (row.category) {
      actions.push(
        { label: t.onlyCategory, icon: <FilterAltOutlinedIcon />, onClick: () => setCategories(onlyCategory(row.category!)) },
        { label: t.hideCategory, icon: <FilterAltOffOutlinedIcon />, onClick: () => setCategories(prev => hideCategory(prev, row.category!)) },
      );
    }
    actions.push({
      label: t.copyMessage,
      icon: <ContentCopyIcon />,
      onClick: () => navigator.clipboard?.writeText(row.message).catch(() => {}),
    });
    return actions;
  }, [t, showContext, showRequest]);

  const seek = useCallback((position: number) => {
    if (!overview) return;
    const percent = Math.round(((position - overview.start) / Math.max(1, overview.end - overview.start)) * 100);
    setView({ kind: 'position', position, percent });
  }, [overview]);

  const closeDetails = useCallback(() => setSelectedId(null), []);
  const backToLatest = useCallback(() => setView(LATEST), []);
  const border = theme.palette.divider;
  const error = sourceError ?? logs.error;
  const exceptionsOnly = !regex && hasToken(text, EXCEPTIONS_TOKEN);

  const emptyText = logs.loading
    ? t.loading
    : logs.hasOlder || logs.hasNewer
      ? t.searching
      : filtered && view.kind !== 'context' ? t.noMatches : t.noEntries;

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default, fontFamily: theme.typography.fontFamily }}>
      {/* The shell's header bar: the menu button on narrow screens and the title, with the search and live in it. */}
      <PageHeader title={t.logs}>
        <TextField
          size="small"
          value={text}
          onChange={e => setText(e.target.value)}
          placeholder={t.searchPlaceholder}
          sx={{ flex: 1, minWidth: 160, mx: compact ? 0 : 1 }}
          slotProps={{
            input: {
              startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 18 }} /></InputAdornment>,
              endAdornment: (
                <InputAdornment position="end" sx={{ gap: 0.5 }}>
                  <Tooltip title={<SearchHelp rows={t.searchHelp} />} placement="bottom-end">
                    <HelpOutlineIcon sx={{ fontSize: 16, color: theme.palette.text.secondary, cursor: 'help' }} />
                  </Tooltip>
                  <Tooltip title={t.caseSensitive}>
                    <ToggleButton value="case" size="small" selected={caseSensitive} onChange={() => setCaseSensitive(v => !v)} sx={{ px: 0.75, py: 0.1, fontSize: '0.72rem', textTransform: 'none' }}>
                      Aa
                    </ToggleButton>
                  </Tooltip>
                  <Tooltip title={t.regex}>
                    <ToggleButton value="regex" size="small" selected={regex} onChange={() => setRegex(v => !v)} sx={{ px: 0.75, py: 0.1, fontSize: '0.72rem' }}>
                      .*
                    </ToggleButton>
                  </Tooltip>
                </InputAdornment>
              ),
            },
          }}
        />

        <Tooltip title={live ? t.liveOn : t.liveOff}>
          <span>
            <Button
              size="small"
              variant={live && view.kind === 'latest' ? 'contained' : 'outlined'}
              color={live && view.kind === 'latest' ? 'success' : 'inherit'}
              disabled={view.kind !== 'latest'}
              onClick={() => setLive(v => !v)}
              startIcon={live ? <PauseIcon sx={{ fontSize: 16 }} /> : <PlayArrowIcon sx={{ fontSize: 16 }} />}
              sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}
            >
              {t.live}
            </Button>
          </span>
        </Tooltip>
      </PageHeader>

      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, px: 2, py: 0.75, borderBottom: `1px solid ${border}`, flexWrap: 'wrap' }}>
        {sources && sources.length > 1 && sourceId && (
          <SourcePicker sources={sources} value={selection} onChange={chooseSource} />
        )}

        <TimeRangePicker value={range} onChange={setRange} onJump={time => setView({ kind: 'around', time })} />

        {!regex && <ServicePicker services={services} value={serviceOf(text)} onChange={s => setText(prev => withService(prev, s))} />}

        {sourceId && <CategoryPicker sourceId={sourceId} filter={categories} onChange={setCategories} />}

        <Tooltip title={t.onlyExceptions}>
          <ToggleButton
            value="exceptions"
            size="small"
            selected={exceptionsOnly}
            onChange={() => {
              setRegex(false);
              setText(prev => toggleToken(regex ? '' : prev, EXCEPTIONS_TOKEN));
            }}
            sx={{ px: 0.75, py: 0.4 }}
          >
            <BugReportOutlinedIcon sx={{ fontSize: 18 }} />
          </ToggleButton>
        </Tooltip>
      </Box>

      <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, px: 2, py: 0.75, borderBottom: `1px solid ${border}`, flexWrap: 'wrap' }}>
        <LevelChips selected={levels} onToggle={toggleLevel} stats={stats} />
        {categories && (
          <Tooltip title={categories.names.join(', ')}>
            <Chip
              size="small"
              color="primary"
              variant="outlined"
              label={categories.names.length === 1
                ? (categories.mode === 'only' ? t.categoriesOnly : t.categoriesHidden)(categories.names[0])
                : (categories.mode === 'only' ? t.categoriesOnlyMany : t.categoriesHiddenMany)(categories.names.length)}
              onDelete={() => setCategories(null)}
              sx={{ maxWidth: 360, fontSize: '0.72rem' }}
            />
          </Tooltip>
        )}
        <Box sx={{ flex: 1 }} />
        {logs.skipped > 0 && (
          <Typography sx={{ fontSize: '0.72rem', color: theme.palette.warning.main }}>{t.skipped(logs.skipped)}</Typography>
        )}
        <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
          {formatCount(logs.rows.length)} {t.shown}
          {stats?.file
            ? ` · ${stats.file.format ?? '…'} · ${formatBytes(stats.file.length)}${stats.file.indexedBytes < stats.file.length
                ? ` · ${t.indexing} ${Math.floor((stats.file.indexedBytes / Math.max(stats.file.length, 1)) * 100)}%`
                : ''}`
            : stats && ` · ${t.memory} ${formatBytes(stats.totalBytes)} / ${formatBytes(stats.maxTotalBytes)}`}
        </Typography>
      </Box>

      {view.kind !== 'latest' && (
        <Alert
          severity="info"
          icon={<CenterFocusStrongIcon fontSize="small" />}
          sx={{ borderRadius: 0, py: 0, alignItems: 'center' }}
          action={
            <Button size="small" color="inherit" onClick={backToLatest} sx={{ textTransform: 'none' }}>
              {view.kind === 'context' ? t.backToResults : t.backToLatest}
            </Button>
          }
        >
          {view.kind === 'context' ? t.contextBanner
            : view.kind === 'around' ? t.aroundBanner(formatFullTime(view.time))
            : t.positionBanner(view.percent)}
        </Alert>
      )}

      {error && <Alert severity="error" sx={{ borderRadius: 0 }}>{error}</Alert>}

      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', position: 'relative' }}>
        {sources !== null && sources.length === 0 ? (
          <Typography sx={{ p: 3, color: theme.palette.text.secondary, fontSize: '0.85rem' }}>{t.noSources}</Typography>
        ) : (
          <LogList
            rows={logs.rows}
            version={logs.version}
            hasOlder={logs.hasOlder}
            loadingOlder={logs.loadingOlder}
            onLoadOlder={logs.loadOlder}
            hasNewer={logs.hasNewer}
            loadingNewer={logs.loadingNewer}
            onLoadNewer={logs.loadNewer}
            anchorId={logs.anchorId}
            selectedId={selectedId}
            onSelect={setSelectedId}
            highlight={highlight}
            rowActions={rowActions}
            onVisibleRange={setVisible}
            emptyText={emptyText}
            compact={compact}
          />
        )}
        <OverviewBar
          overview={overview}
          visible={overview?.time
            ? visible?.firstTime != null ? { first: visible.firstTime, last: visible.lastTime ?? visible.firstTime } : null
            : visible}
          onSeek={seek}
        />
        {sourceId && selectedId !== null && (
          <EntryDetails
            sourceId={sourceId}
            id={selectedId}
            onClose={closeDetails}
            onFilter={applyToken}
            onOnlyCategory={name => setCategories(onlyCategory(name))}
            onContext={showContext}
            onRequest={showRequest}
            highlight={highlight}
            overlay={compact}
          />
        )}
      </Box>
    </Box>
  );
};

export default LogsPage;
