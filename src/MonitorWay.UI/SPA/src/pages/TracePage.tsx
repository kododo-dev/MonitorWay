import { useEffect, useMemo, useState } from 'react';
import { useParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import IconButton from '@mui/material/IconButton';
import ToggleButton from '@mui/material/ToggleButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import CloseIcon from '@mui/icons-material/Close';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import NotesIcon from '@mui/icons-material/Notes';
import { useTheme } from '@mui/material/styles';
import { PageHeader, useShell } from '@kododo-dev/way-shell';
import { fetchEntries, fetchTrace } from '../api/api';
import type { EntryRow, TraceDetail } from '../api/api.model';
import SpanDetails from '../components/SpanDetails';
import Waterfall from '../components/Waterfall';
import { useI18n } from '../i18n/i18n';
import { firstLine, formatFullTime, formatRowTime } from '../utils/format';
import { LEVEL_SHORT, levelColor } from '../utils/levels';
import { fieldToken } from '../utils/search';
import { formatMs, serviceColor } from '../utils/traces';

const REFRESH_MS = 2_000;

/** One trace: its spans as a waterfall, the selected span in detail, and the logs written in the trace. */
const TracePage = () => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const compact = useMediaQuery(theme.breakpoints.down('md'));
  const traceId = (useParams().traceId ?? '').toLowerCase();

  // Kept with the trace it was loaded for; while another trace loads, `trace` is undefined.
  const [loaded, setLoaded] = useState<{ id: string; trace: TraceDetail | null; logs: EntryRow[] } | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [spanLogsOnly, setSpanLogsOnly] = useState(false);
  const trace = loaded?.id === traceId ? loaded.trace : undefined;
  const logs = useMemo(() => (loaded?.id === traceId ? loaded.logs : []), [loaded, traceId]);
  const complete = trace?.isComplete ?? false;

  useEffect(() => {
    let active = true;
    let timer: number | undefined;
    const load = () => Promise.all([
      fetchTrace(traceId).catch(() => null),
      fetchEntries('memory', { text: fieldToken('trace', traceId) }, {}, 1000).then(p => p.entries, () => []),
    ]).then(([t, l]) => {
      if (!active) return;
      setLoaded({ id: traceId, trace: t, logs: l });
      // A trace still being received is read again until it is complete.
      if (t && !t.isComplete) timer = window.setTimeout(load, REFRESH_MS);
    });
    load();
    return () => {
      active = false;
      window.clearTimeout(timer);
    };
  }, [traceId]);

  const root = useMemo(() => trace?.spans.find(s => !s.parentSpanId || !trace.spans.some(p => p.spanId === s.parentSpanId)) ?? trace?.spans[0], [trace]);
  const selectedSpan = trace?.spans.find(s => s.spanId === selected) ?? null;
  const logCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const entry of logs) {
      if (entry.spanId) counts.set(entry.spanId, (counts.get(entry.spanId) ?? 0) + 1);
    }
    return counts;
  }, [logs]);
  const shownLogs = spanLogsOnly && selected ? logs.filter(l => l.spanId === selected) : logs;
  const services = useMemo(() => [...new Set(trace?.spans.map(s => s.service).filter((s): s is string => !!s))], [trace]);
  const errors = trace?.spans.filter(s => s.status === 'Error').length ?? 0;
  const border = theme.palette.divider;
  const showLogs = () => navigate(wayId, `logs?q=${encodeURIComponent(fieldToken('trace', traceId))}`);

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={root?.name ?? t.traces} subtitle={compact ? undefined : traceId}>
        <Box sx={{ flex: 1 }} />
        <Tooltip title={t.copyTraceId}>
          <IconButton size="small" onClick={() => navigator.clipboard?.writeText(traceId).catch(() => {})}>
            <ContentCopyIcon sx={{ fontSize: 16 }} />
          </IconButton>
        </Tooltip>
        {compact ? (
          <>
            <Tooltip title={t.showInLogs}>
              <IconButton size="small" aria-label={t.showInLogs} onClick={showLogs}><NotesIcon sx={{ fontSize: 18 }} /></IconButton>
            </Tooltip>
            <Tooltip title={t.backToTraces}>
              <IconButton size="small" aria-label={t.backToTraces} onClick={() => navigate(wayId, 'traces')}><ArrowBackIcon sx={{ fontSize: 18 }} /></IconButton>
            </Tooltip>
          </>
        ) : (
          <>
            <Button size="small" variant="outlined" startIcon={<NotesIcon sx={{ fontSize: 16 }} />} onClick={showLogs} sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}>
              {t.showInLogs}
            </Button>
            <Button size="small" startIcon={<ArrowBackIcon sx={{ fontSize: 16 }} />} onClick={() => navigate(wayId, 'traces')} sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}>
              {t.backToTraces}
            </Button>
          </>
        )}
      </PageHeader>

      {trace === null && <Alert severity="warning" sx={{ borderRadius: 0 }}>{t.traceGone}</Alert>}

      {trace && (
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, px: 2, py: 0.75, borderBottom: `1px solid ${border}`, flexWrap: 'wrap', fontSize: '0.78rem' }}>
          <span>{formatFullTime(trace.start)}</span>
          <Box component="span" sx={{ fontWeight: 700 }} data-testid="trace-duration">{formatMs(trace.durationMs)}</Box>
          <span>{t.spanCount(trace.spans.length)}</span>
          {errors > 0 && <Box component="span" sx={{ color: theme.palette.error.main }}>{t.errorCount(errors)}</Box>}
          {services.map(service => (
            <Box key={service} component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
              <Box component="span" sx={{ width: 8, height: 8, borderRadius: '50%', background: serviceColor(service) }} />
              {service}
            </Box>
          ))}
          {!complete && <Chip size="small" color="info" variant="outlined" label={t.traceInProgress} />}
          {trace.droppedSpans > 0 && <Chip size="small" color="warning" variant="outlined" label={t.droppedSpans(trace.droppedSpans)} />}
        </Box>
      )}

      <Box sx={{ flex: 1, display: 'flex', overflow: 'hidden', position: 'relative' }}>
        <Box sx={{ flex: 1, overflow: 'auto', minWidth: 0 }}>
          {trace && (
            <Waterfall
              trace={trace}
              selectedSpanId={selected}
              onSelect={id => setSelected(prev => (prev === id ? null : id))}
              logCounts={logCounts}
              compact={compact}
            />
          )}

          {trace && (
            <Box sx={{ mt: 2, borderTop: `1px solid ${border}` }}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, py: 0.75 }}>
                <Typography sx={{ fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary }}>
                  {spanLogsOnly && selected ? t.spanLogs : t.traceLogs} ({shownLogs.length})
                </Typography>
                {selected && (
                  <ToggleButton value="span" size="small" selected={spanLogsOnly} onChange={() => setSpanLogsOnly(v => !v)} sx={{ py: 0, px: 1, fontSize: '0.7rem', textTransform: 'none' }}>
                    {selectedSpan?.name ?? selected}
                  </ToggleButton>
                )}
              </Box>
              {shownLogs.length === 0 && (
                <Typography sx={{ px: 2, pb: 2, fontSize: '0.8rem', color: theme.palette.text.secondary }}>
                  {spanLogsOnly && selected ? t.noSpanLogs : t.noTraceLogs}
                </Typography>
              )}
              {shownLogs.map(entry => (
                <Box
                  key={entry.id}
                  data-testid="trace-log-row"
                  onClick={() => entry.spanId && setSelected(entry.spanId)}
                  sx={{
                    display: 'flex', gap: 1.5, px: 2, py: 0.3, fontSize: '0.75rem', cursor: entry.spanId ? 'pointer' : 'default',
                    background: entry.spanId && entry.spanId === selected ? theme.palette.action.selected : undefined,
                    '&:hover': { background: theme.palette.action.hover },
                  }}
                >
                  <Box sx={{ color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>{formatRowTime(entry.timestamp).slice(6)}</Box>
                  <Box sx={{ color: levelColor(entry.level, isDark), fontWeight: 700, width: 28, flexShrink: 0 }}>{LEVEL_SHORT[entry.level]}</Box>
                  <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{firstLine(entry.message)}</Box>
                </Box>
              ))}
            </Box>
          )}
        </Box>

        {selectedSpan && (
          <Box sx={{
            width: compact ? '100%' : '38%', minWidth: compact ? undefined : 320, maxWidth: compact ? undefined : 640,
            position: compact ? 'absolute' : 'relative', inset: compact ? 0 : undefined, zIndex: compact ? 2 : undefined,
            display: 'flex', flexDirection: 'column', borderLeft: `1px solid ${border}`, background: theme.palette.background.paper,
          }}>
            <Box sx={{ display: 'flex', alignItems: 'center', px: 2, height: 40, borderBottom: `1px solid ${border}`, flexShrink: 0 }}>
              <Typography sx={{ flex: 1, fontSize: '0.8rem', fontWeight: 700 }}>{t.details}</Typography>
              <Tooltip title={t.close}>
                <IconButton size="small" onClick={() => setSelected(null)}><CloseIcon sx={{ fontSize: 18 }} /></IconButton>
              </Tooltip>
            </Box>
            <Box sx={{ flex: 1, overflow: 'auto', p: 2 }}>
              <SpanDetails span={selectedSpan} />
            </Box>
          </Box>
        )}
      </Box>
    </Box>
  );
};

export default TracePage;
