import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Chip from '@mui/material/Chip';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import InputAdornment from '@mui/material/InputAdornment';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutlineOutlined';
import HelpOutlineIcon from '@mui/icons-material/HelpOutlineOutlined';
import HourglassBottomIcon from '@mui/icons-material/HourglassBottom';
import PauseIcon from '@mui/icons-material/Pause';
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import { PageHeader, useShell } from '@kododo-dev/way-shell';
import { fetchTraces, fetchTraceStats } from '../api/api';
import type { TraceQuery, TraceRow, TraceStats } from '../api/api.model';
import SearchHelp from '../components/SearchHelp';
import ServicePicker from '../components/ServicePicker';
import { useServices } from '../hooks/useServices';
import { useI18n } from '../i18n/i18n';
import { formatBytes, formatCount, formatRowTime } from '../utils/format';
import { hasToken, serviceOf, toggleToken, withService } from '../utils/search';
import { formatMs, serviceColor } from '../utils/traces';

const PAGE = 100;
const REFRESH_MS = 3_000;
const ERRORS_TOKEN = 'status:error';

/** The stored traces, newest first, following new ones live; a click opens the trace. */
const TracesPage = () => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const compact = useMediaQuery(theme.breakpoints.down('md'));

  const [params, setParams] = useSearchParams();
  const [text, setText] = useState(() => params.get('q') ?? '');
  // A time window, e.g. dragged on a chart of the overview.
  const [timeWindow, setTimeWindow] = useState(() => {
    const from = params.get('from');
    const to = params.get('to');
    return from && to ? { from, to } : null;
  });
  const query = useDebouncedQuery(text, timeWindow);
  const [live, setLive] = useState(true);
  const services = useServices();
  const [rows, setRows] = useState<TraceRow[]>([]);
  const [hasMore, setHasMore] = useState(false);
  const [loadedKey, setLoadedKey] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [stats, setStats] = useState<TraceStats | null>(null);
  // Which query the rows are for: a refresh that returns after the query changed is dropped.
  const queryKey = JSON.stringify(query);
  const current = useRef(queryKey);
  useEffect(() => {
    current.current = queryKey;
  }, [queryKey]);
  const loading = loadedKey !== queryKey;

  useEffect(() => {
    const next = new URLSearchParams();
    if (query.text) next.set('q', query.text);
    if (query.from && query.to) {
      next.set('from', query.from);
      next.set('to', query.to);
    }
    if (next.toString() !== params.toString()) setParams(next, { replace: true });
  }, [query, params, setParams]);

  useEffect(() => {
    let active = true;
    const load = () => fetchTraceStats().then(s => { if (active) setStats(s); }).catch(() => {});
    load();
    const timer = window.setInterval(load, 5_000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);

  // The newest page; the rows already loaded further back stay, without the traces stored again since.
  const refresh = useCallback((replace: boolean) => {
    const key = queryKey;
    return fetchTraces(query, undefined, PAGE)
      .then(page => {
        if (current.current !== key) return;
        setError(page.error ?? null);
        if (replace) {
          setRows(page.traces);
          setHasMore(page.hasMore);
          return;
        }
        setRows(prev => {
          const top = prev[0]?.id ?? 0;
          const added = page.traces.filter(r => r.id > top);
          if (!added.length) return prev;
          const again = new Set(added.map(r => r.traceId));
          return [...added, ...prev.filter(r => !again.has(r.traceId))];
        });
      })
      .catch(e => { if (current.current === key) setError(String(e)); })
      .finally(() => { if (current.current === key) setLoadedKey(key); });
  }, [query, queryKey]);

  useEffect(() => {
    refresh(true);
  }, [refresh]);

  useEffect(() => {
    if (!live) return;
    const timer = window.setInterval(() => refresh(false), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [live, refresh]);

  const loadOlder = () => {
    if (!rows.length) return;
    const key = queryKey;
    // Pages go by id, the order traces were completed in; the list shows them by start.
    fetchTraces(query, Math.min(...rows.map(r => r.id)), PAGE).then(page => {
      if (current.current !== key) return;
      setRows(prev => [...prev, ...page.traces]);
      setHasMore(page.hasMore);
    }).catch(e => setError(String(e)));
  };

  const slowToken = stats ? `duration:>=${Math.round(stats.slowThresholdMs)}ms` : null;
  const errorsOnly = hasToken(text, ERRORS_TOKEN);
  const slowOnly = slowToken !== null && hasToken(text, slowToken);
  const maxDuration = useMemo(() => Math.max(1, ...rows.map(r => r.durationMs)), [rows]);
  // A slow trace is completed after quicker ones that started later; newest start first reads naturally.
  const sorted = useMemo(() => [...rows].sort((a, b) => b.start.localeCompare(a.start)), [rows]);
  const border = theme.palette.divider;
  const stored = stats?.pools.reduce((sum, p) => sum + p.count, 0) ?? 0;

  const columns = compact ? '92px 1fr 72px' : '150px 1fr 160px 110px 90px';
  const emptyText = loading ? t.loading : query.text || query.from ? t.noTraceMatches : t.noTraces;

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={t.traces}>
        <TextField
          size="small"
          value={text}
          onChange={e => setText(e.target.value)}
          placeholder={t.tracesSearchPlaceholder}
          sx={{ flex: 1, minWidth: 160, mx: compact ? 0 : 1 }}
          slotProps={{
            input: {
              startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 18 }} /></InputAdornment>,
              endAdornment: (
                <InputAdornment position="end">
                  <Tooltip title={<SearchHelp rows={t.tracesSearchHelp} />} placement="bottom-end">
                    <HelpOutlineIcon sx={{ fontSize: 16, color: theme.palette.text.secondary, cursor: 'help' }} />
                  </Tooltip>
                </InputAdornment>
              ),
            },
          }}
        />
        <Tooltip title={live ? t.liveOn : t.liveOff}>
          <Button
            size="small"
            variant={live ? 'contained' : 'outlined'}
            color={live ? 'success' : 'inherit'}
            onClick={() => setLive(v => !v)}
            startIcon={live ? <PauseIcon sx={{ fontSize: 16 }} /> : <PlayArrowIcon sx={{ fontSize: 16 }} />}
            sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}
          >
            {t.live}
          </Button>
        </Tooltip>
      </PageHeader>

      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, py: 0.75, borderBottom: `1px solid ${border}`, flexWrap: 'wrap' }}>
        <ServicePicker services={services} value={serviceOf(text)} onChange={s => setText(prev => withService(prev, s))} />
        <Tooltip title={t.onlyErrors}>
          <ToggleButton value="errors" size="small" selected={errorsOnly} onChange={() => setText(prev => toggleToken(prev, ERRORS_TOKEN))} sx={{ px: 0.75, py: 0.4 }}>
            <ErrorOutlineIcon sx={{ fontSize: 18 }} />
          </ToggleButton>
        </Tooltip>
        {slowToken && stats && (
          <Tooltip title={t.onlySlow(formatMs(stats.slowThresholdMs))}>
            <ToggleButton value="slow" size="small" selected={slowOnly} onChange={() => setText(prev => toggleToken(prev, slowToken))} sx={{ px: 0.75, py: 0.4 }}>
              <HourglassBottomIcon sx={{ fontSize: 18 }} />
            </ToggleButton>
          </Tooltip>
        )}
        {timeWindow && (
          <Chip
            size="small"
            color="primary"
            variant="outlined"
            label={t.selectedRange(formatRowTime(timeWindow.from).slice(0, 14), formatRowTime(timeWindow.to).slice(0, 14))}
            onDelete={() => setTimeWindow(null)}
            sx={{ fontSize: '0.72rem' }}
          />
        )}
        <Box sx={{ flex: 1 }} />
        {stats && (
          <Tooltip title={stats.pools.map(p => `${t.tracePools[p.name] ?? p.name}: ${formatCount(p.count)}`).join(' · ')}>
            <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
              {t.tracesShown(formatCount(rows.length))} / {formatCount(stored)} · {t.memory} {formatBytes(stats.totalBytes)} / {formatBytes(stats.maxTotalBytes)}
            </Typography>
          </Tooltip>
        )}
      </Box>

      {stats && !stats.exported && <Alert severity="info" sx={{ borderRadius: 0 }}>{t.tracesNotExported}</Alert>}
      {error && <Alert severity="error" sx={{ borderRadius: 0, py: 0 }}>{error}</Alert>}

      <Box sx={{ flex: 1, overflow: 'auto' }}>
        <Box sx={{
          display: 'grid', gridTemplateColumns: columns, gap: 1.5, px: 2, py: 0.5, position: 'sticky', top: 0, zIndex: 1,
          background: theme.palette.background.paper, borderBottom: `1px solid ${border}`,
          fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.05em', color: theme.palette.text.secondary,
        }}>
          <span>{t.traceColumns.start}</span>
          <span>{t.traceColumns.name}</span>
          {!compact && <span>{t.traceColumns.service}</span>}
          <span>{t.traceColumns.duration}</span>
          {!compact && <span>{t.traceColumns.spans}</span>}
        </Box>

        {rows.length === 0 && (
          <Typography sx={{ p: 3, fontSize: '0.85rem', color: theme.palette.text.secondary }}>{emptyText}</Typography>
        )}

        {sorted.map(row => (
          <Box
            key={row.traceId}
            data-testid="trace-row"
            data-trace-id={row.traceId}
            data-errors={row.errorCount}
            role="link"
            tabIndex={0}
            onClick={() => navigate(wayId, `traces/${row.traceId}`)}
            onKeyDown={e => { if (e.key === 'Enter') navigate(wayId, `traces/${row.traceId}`); }}
            sx={{
              display: 'grid', gridTemplateColumns: columns, gap: 1.5, alignItems: 'center', px: 2, py: 0.6,
              borderBottom: `1px solid ${border}`, cursor: 'pointer', fontSize: '0.8rem',
              '&:hover': { background: theme.palette.action.hover },
            }}
          >
            <Box sx={{ color: theme.palette.text.secondary, whiteSpace: 'nowrap', fontSize: '0.75rem' }}>
              {compact ? formatRowTime(row.start).slice(6, 14) : formatRowTime(row.start)}
            </Box>
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, minWidth: 0 }}>
              {row.errorCount > 0 && (
                <Tooltip title={t.errorCount(row.errorCount)}>
                  <ErrorOutlineIcon sx={{ fontSize: 16, color: theme.palette.error.main, flexShrink: 0 }} />
                </Tooltip>
              )}
              <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600 }}>{row.name}</Box>
              {row.statusCode && (
                <Box sx={{
                  flexShrink: 0, fontSize: '0.68rem', px: 0.6, borderRadius: 0.75,
                  color: row.statusCode.startsWith('5') ? theme.palette.error.main : row.statusCode.startsWith('4') ? theme.palette.warning.main : theme.palette.text.secondary,
                  border: '1px solid currentColor',
                }}>
                  {row.statusCode}
                </Box>
              )}
            </Box>
            {!compact && (
              <Box sx={{ display: 'flex', gap: 0.75, alignItems: 'center', minWidth: 0, overflow: 'hidden' }}>
                {row.services.map(service => (
                  <Box key={service} sx={{ display: 'flex', alignItems: 'center', gap: 0.5, whiteSpace: 'nowrap', fontSize: '0.75rem' }}>
                    <Box sx={{ width: 8, height: 8, borderRadius: '50%', background: serviceColor(service) }} />
                    {service}
                  </Box>
                ))}
              </Box>
            )}
            <Box sx={{ position: 'relative', height: 18, display: 'flex', alignItems: 'center' }}>
              <Box sx={{
                position: 'absolute', left: 0, top: 3, bottom: 3, borderRadius: 0.5,
                width: `${Math.max(2, (row.durationMs / maxDuration) * 100)}%`,
                background: row.errorCount > 0 ? (isDark ? '#5c2b2b' : '#fbe0e0') : (isDark ? '#23384d' : '#dcebfa'),
              }} />
              <Box sx={{ position: 'relative', pl: 0.5, fontSize: '0.75rem', whiteSpace: 'nowrap' }}>{formatMs(row.durationMs)}</Box>
            </Box>
            {!compact && (
              <Box sx={{ fontSize: '0.75rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
                {row.spanCount}
              </Box>
            )}
          </Box>
        ))}

        {hasMore && rows.length > 0 && (
          <Box sx={{ p: 1.5, textAlign: 'center' }}>
            <Button size="small" onClick={loadOlder} sx={{ textTransform: 'none' }}>{t.loadOlder}</Button>
          </Box>
        )}
      </Box>
    </Box>
  );
};

/** The query of the search text, once it has not changed for a moment, within the time window. */
function useDebouncedQuery(text: string, range: { from: string; to: string } | null): TraceQuery {
  const [debounced, setDebounced] = useState(() => text.trim() || undefined);
  useEffect(() => {
    const timer = globalThis.setTimeout(() => setDebounced(text.trim() || undefined), 300);
    return () => globalThis.clearTimeout(timer);
  }, [text]);
  return useMemo(() => ({ text: debounced, from: range?.from, to: range?.to }), [debounced, range]);
}

export default TracesPage;
