import { useCallback, useEffect, useMemo, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import InputAdornment from '@mui/material/InputAdornment';
import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import ArrowBackIcon from '@mui/icons-material/ArrowBack';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import { PageHeader } from '@kododo-dev/way-shell';
import { fetchMetrics, fetchMetricSeries, fetchMetricStats } from '../api/api';
import type { Instrument, MetricSeriesPage, MetricStats } from '../api/api.model';
import RangeButtons from '../components/RangeButtons';
import ServicePicker from '../components/ServicePicker';
import TimeChart from '../components/TimeChart';
import { useChartRange } from '../hooks/useChartRange';
import { useServices } from '../hooks/useServices';
import { useI18n } from '../i18n/i18n';
import { formatBytes, formatDuration } from '../utils/format';
import { formatValue, isRate, unitOf } from '../utils/units';

const REFRESH_MS = 10_000;
const COLORS = ['#1e88e5', '#e53935', '#43a047', '#fb8c00', '#8e24aa', '#00897b', '#6d4c41', '#3949ab', '#c0ca33', '#d81b60'];
const PERCENTILE_COLORS: Record<string, string> = { p50: '#43a047', p95: '#fb8c00', p99: '#e53935' };

/** The instruments the application reports, and a chart of the chosen one: a statistic, grouped by an attribute. */
const MetricsPage = () => {
  const theme = useTheme();
  const { t } = useI18n();
  const compact = useMediaQuery(theme.breakpoints.down('md'));
  const [params, setParams] = useSearchParams();
  const [range, setRange, bounds] = useChartRange();
  const [instruments, setInstruments] = useState<Instrument[] | null>(null);
  const [stats, setStats] = useState<MetricStats | null>(null);
  const [filter, setFilter] = useState('');
  const selectedKey = params.get('m');
  const service = params.get('svc');
  const services = useServices();
  const selected = instruments?.find(i => i.key === selectedKey) ?? null;

  const setParam = useCallback((values: Record<string, string | null>) => setParams(prev => {
    const next = new URLSearchParams(prev);
    for (const [key, value] of Object.entries(values)) {
      if (value) next.set(key, value);
      else next.delete(key);
    }
    return next;
  }, { replace: true }), [setParams]);

  useEffect(() => {
    let active = true;
    const load = () => {
      fetchMetrics().then(list => { if (active) setInstruments(list); }).catch(() => { if (active) setInstruments(prev => prev ?? []); });
      fetchMetricStats().then(s => { if (active) setStats(s); }).catch(() => {});
    };
    load();
    const timer = window.setInterval(load, 15_000);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);

  const shown = useMemo(() => {
    const words = filter.toLowerCase().split(/\s+/).filter(Boolean);
    const matching = (instruments ?? []).filter(i => words.every(w => `${i.meter} ${i.name} ${i.description ?? ''}`.toLowerCase().includes(w)));
    const meters = new Map<string, Instrument[]>();
    for (const instrument of matching) meters.set(instrument.meter, [...(meters.get(instrument.meter) ?? []), instrument]);
    return [...meters.entries()];
  }, [instruments, filter]);

  const border = theme.palette.divider;
  const list = (
    <Box sx={{ width: compact ? '100%' : 340, flexShrink: 0, borderRight: compact ? undefined : `1px solid ${border}`, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
      <Box sx={{ p: 1.5, borderBottom: `1px solid ${border}` }}>
        <TextField size="small" fullWidth value={filter} onChange={e => setFilter(e.target.value)} placeholder={t.metricsSearch}
          slotProps={{ input: { startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 18 }} /></InputAdornment> } }} />
      </Box>
      <Box sx={{ flex: 1, overflow: 'auto' }}>
        {instruments?.length === 0 && <Typography sx={{ p: 2, fontSize: '0.8rem', color: theme.palette.text.secondary }}>{t.noMetrics}</Typography>}
        {shown.map(([meter, items]) => (
          <Box key={meter}>
            <Typography sx={{ px: 1.5, pt: 1.25, pb: 0.25, fontSize: '0.68rem', color: theme.palette.text.secondary, wordBreak: 'break-all' }}>{meter}</Typography>
            {items.map(item => (
              <Box key={item.key} data-testid="metric-item" data-key={item.key} role="button" tabIndex={0}
                onClick={() => setParam({ m: item.key, stat: null, g: null })}
                onKeyDown={e => { if (e.key === 'Enter') setParam({ m: item.key, stat: null, g: null }); }}
                sx={{
                  px: 1.5, py: 0.6, cursor: 'pointer', fontSize: '0.78rem',
                  background: item.key === selectedKey ? theme.palette.action.selected : undefined,
                  '&:hover': { background: item.key === selectedKey ? theme.palette.action.selected : theme.palette.action.hover },
                }}>
                <Box sx={{ wordBreak: 'break-all', fontWeight: item.key === selectedKey ? 700 : 400 }}>{item.name}</Box>
                <Box sx={{ fontSize: '0.68rem', color: theme.palette.text.secondary }}>
                  {t.kinds[item.kind]}{item.unit ? ` · ${item.unit}` : ''} · {t.seriesCount(item.series)}
                </Box>
              </Box>
            ))}
          </Box>
        ))}
      </Box>
    </Box>
  );

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={t.metrics}>
        <Box sx={{ flex: 1 }} />
        {stats && !compact && (
          <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
            {t.instrumentCount(stats.instruments)} · {t.seriesCount(stats.series)} · {t.memory} {formatBytes(stats.bytes)} / {formatBytes(stats.maxTotalBytes)}
            {stats.droppedSeries > 0 && ` · ${t.droppedSeries(stats.droppedSeries)}`}
          </Typography>
        )}
        <ServicePicker services={services} value={service} onChange={svc => setParam({ svc })} />
        <RangeButtons value={range} onChange={setRange} />
      </PageHeader>
      {stats && !stats.exported && <Alert severity="info" sx={{ borderRadius: 0 }}>{t.metricsNotExported}</Alert>}

      <Box sx={{ flex: 1, display: 'flex', minHeight: 0 }}>
        {(!compact || !selected) && list}
        {selected && (
          <InstrumentChart
            key={selected.key}
            instrument={selected}
            stat={params.get('stat') ?? (selected.kind === 'Histogram' && !params.get('g') ? 'percentiles' : selected.stats[0])}
            groupBy={params.get('g')}
            service={service}
            bounds={bounds}
            onStat={stat => setParam({ stat })}
            onGroupBy={g => setParam({ g, stat: g && params.get('stat') === 'percentiles' ? null : params.get('stat') })}
            onSelect={(from, to) => setRange({ from: new Date(from * 1000).toISOString(), to: new Date(to * 1000).toISOString() })}
            onBack={compact ? () => setParam({ m: null }) : undefined}
          />
        )}
        {!selected && !compact && (
          <Typography sx={{ p: 3, fontSize: '0.85rem', color: theme.palette.text.secondary }}>{t.chooseMetric}</Typography>
        )}
      </Box>
    </Box>
  );
};

interface ChartProps {
  instrument: Instrument;
  stat: string;
  groupBy: string | null;
  /** Only the series of this service (their service.name attribute). */
  service: string | null;
  bounds: () => { from: string; to?: string };
  onStat: (stat: string) => void;
  onGroupBy: (key: string | null) => void;
  onSelect: (from: number, to: number) => void;
  onBack?: () => void;
}

const InstrumentChart = ({ instrument, stat, groupBy, service, bounds, onStat, onGroupBy, onSelect, onBack }: ChartProps) => {
  const theme = useTheme();
  const { t } = useI18n();
  const [page, setPage] = useState<MetricSeriesPage | null>(null);
  const percentiles = stat === 'percentiles';

  useEffect(() => {
    let active = true;
    const load = () => {
      const { from, to } = bounds();
      fetchMetricSeries({
        key: instrument.key, from, to, stat, groupBy: groupBy ? [groupBy] : undefined,
        filter: service ? [{ key: 'service.name', value: service }] : undefined,
      })
        .then(p => { if (active) setPage(p); })
        .catch(() => {});
    };
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [instrument.key, stat, groupBy, service, bounds]);

  const unit = unitOf(stat, instrument.unit);
  const format = useCallback((v: number) => formatValue(v, unit, isRate(stat)), [unit, stat]);
  const series = useMemo(() => (page?.series ?? []).map((s, i) => ({
    label: s.label,
    values: s.values,
    color: (percentiles && PERCENTILE_COLORS[s.label]) || COLORS[i % COLORS.length],
  })), [page, percentiles]);
  const statOptions = instrument.kind === 'Histogram' ? ['percentiles', ...instrument.stats] : instrument.stats;
  const border = theme.palette.divider;

  return (
    <Box sx={{ flex: 1, minWidth: 0, overflow: 'auto', p: 2 }} data-testid="metric-chart">
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
        {onBack && <IconButton size="small" onClick={onBack} aria-label={t.close}><ArrowBackIcon sx={{ fontSize: 18 }} /></IconButton>}
        <Typography sx={{ fontWeight: 700, fontSize: '0.95rem', wordBreak: 'break-all' }}>{instrument.name}</Typography>
      </Box>
      {instrument.description && <Typography sx={{ fontSize: '0.8rem', mt: 0.5 }}>{instrument.description}</Typography>}
      <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, mt: 0.5 }}>
        {instrument.meter} · {t.kinds[instrument.kind]}{instrument.unit ? ` · ${instrument.unit}` : ''} · {t.seriesCount(instrument.series)}
        {instrument.droppedSeries > 0 && ` · ${t.droppedSeries(instrument.droppedSeries)}`}
        {page && page.resolutionSeconds > 0 && ` · ${t.resolution(formatDuration(page.resolutionSeconds))}`}
      </Typography>

      <Box sx={{ display: 'flex', gap: 1.5, mt: 2, flexWrap: 'wrap' }}>
        <TextField select size="small" label={t.stat} value={stat} onChange={e => onStat(e.target.value)} sx={{ minWidth: 170 }}
          data-testid="stat-select">
          {statOptions.map(s => <MenuItem key={s} value={s} disabled={s === 'percentiles' && !!groupBy}>{t.statNames[s] ?? s}</MenuItem>)}
        </TextField>
        <TextField select size="small" label={t.groupBy} value={groupBy ?? ''} onChange={e => onGroupBy(e.target.value || null)} sx={{ minWidth: 200 }}
          data-testid="group-select">
          <MenuItem value="">{t.noGrouping}</MenuItem>
          {instrument.attributes.map(a => <MenuItem key={a.key} value={a.key}>{a.key} ({a.values})</MenuItem>)}
        </TextField>
      </Box>

      {page?.error && <Alert severity="warning" sx={{ mt: 2, py: 0 }}>{page.error}</Alert>}

      <Box sx={{ mt: 2, background: theme.palette.background.paper, border: `1px solid ${border}`, borderRadius: 1, p: 1.5 }}>
        {page && <TimeChart times={page.times} series={series} format={format} height={260} bars={instrument.kind === 'Counter' && stat === 'sum'} onSelect={onSelect} />}
      </Box>

      {page && page.series.length > 0 && (
        <Box component="table" sx={{ mt: 2, width: '100%', borderCollapse: 'collapse', fontSize: '0.78rem' }}>
          <tbody>
            {page.series.map((s, i) => (
              <Box component="tr" key={`${s.label}-${i}`} data-testid="metric-group" sx={{ borderTop: `1px solid ${border}` }}>
                <Box component="td" sx={{ py: 0.5, width: 16 }}>
                  <Box sx={{ width: 10, height: 10, borderRadius: '2px', background: series[i]?.color }} />
                </Box>
                <Box component="td" sx={{ wordBreak: 'break-all' }}>{s.label}</Box>
                <Box component="td" sx={{ textAlign: 'right', color: theme.palette.text.secondary, whiteSpace: 'nowrap', px: 1 }}>
                  {!percentiles && t.seriesCount(s.series)}
                </Box>
                <Tooltip title={t.total}>
                  <Box component="td" sx={{ textAlign: 'right', fontWeight: 700, whiteSpace: 'nowrap' }}>{format(s.total ?? NaN)}</Box>
                </Tooltip>
              </Box>
            ))}
          </tbody>
        </Box>
      )}
      {page && page.hiddenGroups > 0 && (
        <Typography sx={{ mt: 1, fontSize: '0.72rem', color: theme.palette.text.secondary }}>{t.moreGroups(page.hiddenGroups)}</Typography>
      )}
    </Box>
  );
};

export default MetricsPage;
