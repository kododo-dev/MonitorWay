import { useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import { useTheme } from '@mui/material/styles';
import { PageHeader, useShell } from '@kododo-dev/way-shell';
import { fetchErrors, fetchServiceOverview } from '../api/api';
import type { ErrorRow, RouteStats, ServiceOverview } from '../api/api.model';
import RangeButtons from '../components/RangeButtons';
import ServicePicker from '../components/ServicePicker';
import TimeChart from '../components/TimeChart';
import { useChartRange } from '../hooks/useChartRange';
import { useServices } from '../hooks/useServices';
import { useI18n } from '../i18n/i18n';
import { shortType } from '../utils/errors';
import { firstLine, formatCount, formatDuration } from '../utils/format';
import { fieldToken } from '../utils/search';
import { formatMs } from '../utils/traces';
import { formatNumber } from '../utils/units';

const REFRESH_MS = 10_000;

/** The health of the application at a glance: requests, server errors and durations, per route, and the latest errors. */
const OverviewPage = () => {
  const theme = useTheme();
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const compact = useMediaQuery(theme.breakpoints.down('md'));
  const [range, setRange, bounds] = useChartRange();
  const [params, setParams] = useSearchParams();
  const service = params.get('svc');
  const services = useServices();
  const setService = (svc: string | null) => setParams(prev => {
    const next = new URLSearchParams(prev);
    if (svc) next.set('svc', svc);
    else next.delete('svc');
    return next;
  }, { replace: true });
  const [loaded, setLoaded] = useState<{ overview: ServiceOverview; errors: ErrorRow[]; at: number } | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const load = () => {
      const { from, to } = bounds();
      Promise.all([
        fetchServiceOverview(from, to, service),
        fetchErrors({ text: service ? fieldToken('service', service) : undefined }, 'recent', 5).catch(() => ({ errors: [] })),
      ])
        .then(([overview, errors]) => {
          if (!active) return;
          setLoaded({ overview, errors: errors.errors, at: Date.now() });
          setFailure(null);
        })
        .catch(e => { if (active) setFailure(String(e)); });
    };
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [bounds, service]);

  const overview = loaded?.overview;
  const border = theme.palette.divider;
  const secondsMs = (v: number | null) => (v === null ? '-' : formatMs(v * 1000));
  const tracesIn = (from: number, to: number) => {
    const query = new URLSearchParams({ from: new Date(from * 1000).toISOString(), to: new Date(to * 1000).toISOString() });
    if (service) query.set('q', fieldToken('service', service));
    navigate(wayId, `traces?${query}`);
  };
  const routeTraces = (route: RouteStats) => {
    const { from, to } = bounds();
    const text = [route.service ? fieldToken('service', route.service) : null, fieldToken('http.route', route.route)].filter(Boolean).join(' ');
    const query = new URLSearchParams({ q: text, from, to: to ?? new Date().toISOString() });
    navigate(wayId, `traces?${query}`);
  };

  const requestSeries = useMemo(() => overview ? [
    { label: t.requests, values: overview.requests, color: theme.palette.primary.main },
    { label: t.errorsSeries, values: overview.errors, color: theme.palette.error.main },
  ] : [], [overview, t, theme]);
  const latencySeries = useMemo(() => overview ? [
    { label: 'p50', values: overview.p50, color: '#43a047' },
    { label: 'p95', values: overview.p95, color: '#fb8c00' },
    { label: 'p99', values: overview.p99, color: '#e53935' },
  ] : [], [overview]);

  const label = { fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary, mb: 0.75 } as const;
  const panel = { background: theme.palette.background.paper, border: `1px solid ${border}`, borderRadius: 1, p: 1.5, minWidth: 0 } as const;
  const errorRate = overview && overview.totalRequests > 0 ? (overview.totalErrors / overview.totalRequests) * 100 : 0;
  // Per second of the part of the range with requests: an application started a minute ago has not been idle for an hour.
  const first = overview ? overview.requests.findIndex(r => (r ?? 0) > 0) : -1;
  const activeSeconds = overview && first >= 0 ? (overview.times.length - first) * overview.resolutionSeconds : 1;

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={t.overview}>
        <Box sx={{ flex: 1 }} />
        <ServicePicker services={services} value={service} onChange={setService} />
        <RangeButtons value={range} onChange={setRange} />
      </PageHeader>

      {failure && <Alert severity="error" sx={{ borderRadius: 0, py: 0 }}>{failure}</Alert>}
      {overview && !overview.exported && <Alert severity="info" sx={{ borderRadius: 0 }}>{t.metricsNotExported}</Alert>}
      {overview && overview.exported && !overview.available && <Alert severity="info" sx={{ borderRadius: 0 }}>{t.overviewUnavailable}</Alert>}

      <Box sx={{ flex: 1, overflow: 'auto', p: 2, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {overview?.available && (
          <>
            <Box sx={{ display: 'grid', gridTemplateColumns: compact ? 'repeat(2, 1fr)' : 'repeat(5, 1fr)', gap: 1.5 }}>
              <Tile title={t.requests} value={formatCount(overview.totalRequests)}
                note={t.perSecond(formatNumber(overview.totalRequests / activeSeconds))} />
              <Tile title={t.serverErrors} value={`${formatNumber(Math.round(errorRate * 100) / 100)}%`}
                note={formatCount(overview.totalErrors)} danger={overview.totalErrors > 0} />
              <Tile title={`${t.duration} p50`} value={secondsMs(overview.totalP50)} />
              <Tile title={`${t.duration} p95`} value={secondsMs(overview.totalP95)} />
              <Tile title={`${t.duration} p99`} value={secondsMs(overview.totalP99)} />
            </Box>

            <Box sx={{ display: 'grid', gridTemplateColumns: compact ? '1fr' : '1fr 1fr', gap: 1.5 }}>
              <Box sx={panel} data-testid="requests-chart">
                <Typography sx={label}>{t.requestsChart}</Typography>
                <TimeChart times={overview.times} series={requestSeries} format={v => formatNumber(v)} bars height={180} onSelect={tracesIn} />
              </Box>
              <Box sx={panel} data-testid="latency-chart">
                <Typography sx={label}>{t.latencyChart}</Typography>
                <TimeChart times={overview.times} series={latencySeries} format={v => formatMs(v * 1000)} height={180} onSelect={tracesIn} />
              </Box>
            </Box>
            <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, mt: -1 }}>{t.dragHint}</Typography>

            <Box sx={panel}>
              <Typography sx={label}>{t.routes}</Typography>
              {overview.routes.length === 0 && <Typography sx={{ fontSize: '0.8rem', color: theme.palette.text.secondary }}>{t.noRequests}</Typography>}
              <Box component="table" sx={{ width: '100%', borderCollapse: 'collapse', fontSize: '0.78rem' }}>
                {overview.routes.length > 0 && (
                  <thead>
                    <Box component="tr" sx={{ color: theme.palette.text.secondary, fontSize: '0.7rem', textTransform: 'uppercase' }}>
                      <Box component="th" sx={{ textAlign: 'left', fontWeight: 400, pb: 0.5 }}>{t.routeColumns.route}</Box>
                      <Box component="th" sx={{ textAlign: 'right', fontWeight: 400 }}>{t.routeColumns.requests}</Box>
                      <Box component="th" sx={{ textAlign: 'right', fontWeight: 400 }}>{t.routeColumns.errors}</Box>
                      {!compact && <Box component="th" sx={{ textAlign: 'right', fontWeight: 400 }}>p50</Box>}
                      <Box component="th" sx={{ textAlign: 'right', fontWeight: 400 }}>p95</Box>
                      {!compact && <Box component="th" sx={{ textAlign: 'right', fontWeight: 400 }}>p99</Box>}
                    </Box>
                  </thead>
                )}
                <tbody>
                  {overview.routes.map(route => (
                    <Box component="tr" key={`${route.service} ${route.method} ${route.route}`} data-testid="route-row" title={t.routeTraces}
                      onClick={() => routeTraces(route)}
                      sx={{ cursor: 'pointer', borderTop: `1px solid ${border}`, '&:hover': { background: theme.palette.action.hover } }}>
                      <Box component="td" sx={{ py: 0.6, wordBreak: 'break-all' }}>
                        {services.length > 1 && !service && route.service && (
                          <Box component="span" sx={{ color: theme.palette.text.secondary, mr: 1 }}>{route.service}</Box>
                        )}
                        <Box component="span" sx={{ color: theme.palette.text.secondary, mr: 1 }}>{route.method}</Box>{route.route}
                      </Box>
                      <Box component="td" sx={{ textAlign: 'right' }}>{formatCount(route.requests)}</Box>
                      <Box component="td" sx={{ textAlign: 'right', color: route.errors > 0 ? theme.palette.error.main : undefined }}>
                        {route.errors > 0 ? `${formatCount(route.errors)} (${formatNumber(Math.round((route.errors / Math.max(1, route.requests)) * 1000) / 10)}%)` : '0'}
                      </Box>
                      {!compact && <Box component="td" sx={{ textAlign: 'right' }}>{secondsMs(route.p50)}</Box>}
                      <Box component="td" sx={{ textAlign: 'right' }}>{secondsMs(route.p95)}</Box>
                      {!compact && <Box component="td" sx={{ textAlign: 'right' }}>{secondsMs(route.p99)}</Box>}
                    </Box>
                  ))}
                </tbody>
              </Box>
            </Box>
          </>
        )}

        {loaded && loaded.errors.length > 0 && (
          <Box sx={panel}>
            <Typography sx={label}>{t.recentErrors}</Typography>
            {loaded.errors.map(error => (
              <Box key={error.fingerprint} data-testid="overview-error" onClick={() => navigate(wayId, `errors/${error.fingerprint}`)}
                sx={{ display: 'flex', gap: 1.5, alignItems: 'baseline', py: 0.5, cursor: 'pointer', borderTop: `1px solid ${border}`,
                  fontSize: '0.78rem', '&:hover': { background: theme.palette.action.hover } }}>
                <Box sx={{ fontWeight: 700, color: theme.palette.error.main, whiteSpace: 'nowrap' }}>{shortType(error.type)}</Box>
                <Box sx={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: theme.palette.text.secondary }}>
                  {firstLine(error.message)}
                </Box>
                <Box sx={{ whiteSpace: 'nowrap' }}>{formatCount(error.count)}</Box>
                <Box sx={{ whiteSpace: 'nowrap', color: theme.palette.text.secondary, fontSize: '0.72rem' }}>
                  {t.ago(formatDuration(Math.max(0, (loaded.at - Date.parse(error.lastSeen)) / 1000)))}
                </Box>
              </Box>
            ))}
          </Box>
        )}
      </Box>
    </Box>
  );
};

const Tile = ({ title, value, note, danger }: { title: string; value: string; note?: string; danger?: boolean }) => {
  const theme = useTheme();
  return (
    <Box data-testid="tile" sx={{ background: theme.palette.background.paper, border: `1px solid ${theme.palette.divider}`, borderRadius: 1, p: 1.5 }}>
      <Typography sx={{ fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary }}>{title}</Typography>
      <Typography sx={{ fontSize: '1.35rem', fontWeight: 700, color: danger ? theme.palette.error.main : undefined }}>{value}</Typography>
      {note && <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary }}>{note}</Typography>}
    </Box>
  );
};

export default OverviewPage;
