import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import NotesIcon from '@mui/icons-material/Notes';
import TimelineIcon from '@mui/icons-material/Timeline';
import { useTheme } from '@mui/material/styles';
import { PageHeader, useShell } from '@kododo-dev/way-shell';
import { areTracesExported, fetchError } from '../api/api';
import type { ErrorDetail } from '../api/api.model';
import Sparkline from '../components/Sparkline';
import { useI18n } from '../i18n/i18n';
import { formatFullTime, formatRowTime } from '../utils/format';
import { isApplicationFrame, shortType } from '../utils/errors';
import { fieldToken } from '../utils/search';

const REFRESH_MS = 5_000;

/** One error group: how often and where it happens, its stack trace and its latest occurrences with their traces. */
const ErrorPage = () => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const compact = useMediaQuery(theme.breakpoints.down('md'));
  const fingerprint = (useParams().fingerprint ?? '').toLowerCase();
  // With the moment it was read: the weekly trend ends with the hour of that moment.
  const [loaded, setLoaded] = useState<{ id: string; error: ErrorDetail | null; at: number } | null>(null);
  const [traces, setTraces] = useState(false);
  const error = loaded?.id === fingerprint ? loaded.error : undefined;

  useEffect(() => {
    let active = true;
    areTracesExported().then(exported => { if (active) setTraces(exported); });
    return () => { active = false; };
  }, []);

  useEffect(() => {
    let active = true;
    const load = () => fetchError(fingerprint)
      .then(e => { if (active) setLoaded({ id: fingerprint, error: e, at: Date.now() }); })
      .catch(() => { if (active) setLoaded({ id: fingerprint, error: null, at: Date.now() }); });
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [fingerprint]);

  const border = theme.palette.divider;
  const label = { fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary, mt: 2.5, mb: 0.75 } as const;
  const showLogs = (traceId: string) => navigate(wayId, `logs?q=${encodeURIComponent(fieldToken('trace', traceId))}`);
  const back = () => navigate(wayId, 'errors');

  // The hour a bar of the weekly trend stands for, e.g. `10-25 14:00`.
  const hourOf = (index: number, length: number) => {
    const hour = Math.floor((loaded?.at ?? 0) / 3_600_000) - (length - 1 - index);
    return `${formatRowTime(new Date(hour * 3_600_000).toISOString()).slice(0, 9)}00`;
  };

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={error ? shortType(error.type) : t.errors} subtitle={compact ? undefined : error?.type}>
        <Box sx={{ flex: 1 }} />
        {compact ? (
          <Tooltip title={t.backToErrors}>
            <IconButton size="small" aria-label={t.backToErrors} onClick={back}><ArrowBackIcon sx={{ fontSize: 18 }} /></IconButton>
          </Tooltip>
        ) : (
          <Button size="small" startIcon={<ArrowBackIcon sx={{ fontSize: 16 }} />} onClick={back} sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}>
            {t.backToErrors}
          </Button>
        )}
      </PageHeader>

      {error === null && <Alert severity="warning" sx={{ borderRadius: 0 }}>{t.errorGone}</Alert>}

      {error && (
        <Box sx={{ flex: 1, overflow: 'auto', px: 2, pb: 3 }} data-testid="error-detail">
          <Typography sx={{ mt: 2, fontSize: '0.9rem', fontWeight: 600, wordBreak: 'break-word', whiteSpace: 'pre-wrap' }}>{error.message}</Typography>

          <Box sx={{ display: 'flex', gap: 3, mt: 1.5, flexWrap: 'wrap', fontSize: '0.78rem' }}>
            <Box component="span" sx={{ fontWeight: 700, color: theme.palette.error.main }}>{t.occurrenceCount(error.count)}</Box>
            <Box><Box component="span" sx={{ color: theme.palette.text.secondary }}>{t.firstSeen}:</Box> {formatFullTime(error.firstSeen)}</Box>
            <Box><Box component="span" sx={{ color: theme.palette.text.secondary }}>{t.lastSeen}:</Box> {formatFullTime(error.lastSeen)}</Box>
          </Box>
          <Box sx={{ display: 'flex', gap: 3, mt: 0.75, flexWrap: 'wrap', fontSize: '0.78rem' }}>
            {error.services.length > 0 && <Box><Box component="span" sx={{ color: theme.palette.text.secondary }}>{t.services}:</Box> {error.services.join(', ')}</Box>}
            {error.endpoints.length > 0 && <Box><Box component="span" sx={{ color: theme.palette.text.secondary }}>{t.endpoints}:</Box> {error.endpoints.join(', ')}</Box>}
          </Box>

          <Typography sx={label}>{t.trendWeek}</Typography>
          <Box sx={{ overflowX: 'auto' }}>
            <Sparkline values={error.trend} width={compact ? 336 : 672} height={48} label={(i, v) => `${hourOf(i, error.trend.length)}: ${v}`} />
          </Box>

          {error.frames.length > 0 && (
            <>
              <Typography sx={label}>{t.recognizedBy}</Typography>
              <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap' }}>
                {error.frames.map(frame => <Chip key={frame} size="small" variant="outlined" label={frame} sx={{ maxWidth: '100%' }} />)}
              </Box>
            </>
          )}

          {error.stackTrace && (
            <>
              <Typography sx={label}>{t.stackTrace}</Typography>
              <Box component="pre" sx={{
                m: 0, p: 1.25, borderRadius: 1, fontSize: '0.75rem', overflowX: 'auto', fontFamily: "'IBM Plex Mono', monospace",
                background: isDark ? '#1b1b1b' : '#f7f7f7', border: `1px solid ${border}`,
              }}>
                {error.stackTrace.split('\n').map((line, i) => (
                  <Box key={i} component="span" sx={{
                    display: 'block',
                    color: i === 0 || isApplicationFrame(line, error.frames) ? theme.palette.error.main : theme.palette.text.secondary,
                    fontWeight: isApplicationFrame(line, error.frames) ? 700 : 400,
                  }}>
                    {line || ' '}
                  </Box>
                ))}
              </Box>
            </>
          )}

          <Typography sx={label}>{t.latestOccurrences}</Typography>
          {error.occurrences.map((o, i) => (
            <Box key={i} data-testid="occurrence-row" sx={{
              display: 'flex', alignItems: 'center', gap: 1.5, py: 0.6, borderBottom: `1px solid ${border}`, fontSize: '0.75rem', flexWrap: compact ? 'wrap' : 'nowrap',
            }}>
              <Box sx={{ color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>{formatRowTime(o.timestamp)}</Box>
              {!compact && <Box sx={{ whiteSpace: 'nowrap', minWidth: 120 }}>{o.endpoint ?? ''}</Box>}
              <Box sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{o.message}</Box>
              <Box sx={{ display: 'flex', gap: 0.5 }}>
                {o.fromLog && <Chip size="small" label="log" sx={{ height: 18 }} />}
                {o.fromSpan && <Chip size="small" label="span" sx={{ height: 18 }} />}
              </Box>
              {o.traceId && traces && (
                <Tooltip title={t.openTrace}>
                  <IconButton size="small" aria-label={t.openTrace} onClick={() => navigate(wayId, `traces/${o.traceId}`)}>
                    <TimelineIcon sx={{ fontSize: 16 }} />
                  </IconButton>
                </Tooltip>
              )}
              {o.traceId && (
                <Tooltip title={t.showInLogs}>
                  <IconButton size="small" aria-label={t.showInLogs} onClick={() => showLogs(o.traceId!)}>
                    <NotesIcon sx={{ fontSize: 16 }} />
                  </IconButton>
                </Tooltip>
              )}
            </Box>
          ))}
        </Box>
      )}
    </Box>
  );
};

export default ErrorPage;
