import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import InputAdornment from '@mui/material/InputAdornment';
import TextField from '@mui/material/TextField';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import useMediaQuery from '@mui/material/useMediaQuery';
import HelpOutlineIcon from '@mui/icons-material/HelpOutlineOutlined';
import SearchIcon from '@mui/icons-material/Search';
import { useTheme } from '@mui/material/styles';
import { PageHeader, useShell } from '@kododo-dev/way-shell';
import { fetchErrors, fetchErrorStats } from '../api/api';
import type { ErrorRow, ErrorStats } from '../api/api.model';
import SearchHelp from '../components/SearchHelp';
import ServicePicker from '../components/ServicePicker';
import Sparkline from '../components/Sparkline';
import { useServices } from '../hooks/useServices';
import { useI18n } from '../i18n/i18n';
import { formatCount, formatDuration, firstLine } from '../utils/format';
import { shortType } from '../utils/errors';
import { serviceOf, withService } from '../utils/search';

const REFRESH_MS = 5_000;
type Sort = 'recent' | 'count';

/** The exceptions of the application grouped by type and place, the latest or the most frequent first. */
const ErrorsPage = () => {
  const theme = useTheme();
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const compact = useMediaQuery(theme.breakpoints.down('md'));

  const [params, setParams] = useSearchParams();
  const [text, setText] = useState(() => params.get('q') ?? '');
  const [sort, setSort] = useState<Sort>(() => (params.get('sort') === 'count' ? 'count' : 'recent'));
  const [query, setQuery] = useState(() => text.trim());
  // The rows with the query and moment they were read for: ages are counted from that moment.
  const [loaded, setLoaded] = useState<{ key: string; rows: ErrorRow[]; error: string | null; at: number } | null>(null);
  const [stats, setStats] = useState<ErrorStats | null>(null);
  const services = useServices();
  const key = `${sort}:${query}`;

  useEffect(() => {
    const timer = window.setTimeout(() => setQuery(text.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [text]);

  useEffect(() => {
    const next = new URLSearchParams();
    if (query) next.set('q', query);
    if (sort === 'count') next.set('sort', 'count');
    if (next.toString() !== params.toString()) setParams(next, { replace: true });
  }, [query, sort, params, setParams]);

  useEffect(() => {
    let active = true;
    const load = () => {
      fetchErrors({ text: query || undefined }, sort)
        .then(page => { if (active) setLoaded({ key, rows: page.errors, error: page.error ?? null, at: Date.now() }); })
        .catch(e => { if (active) setLoaded(prev => ({ key, rows: prev?.key === key ? prev.rows : [], error: String(e), at: Date.now() })); });
      fetchErrorStats().then(s => { if (active) setStats(s); }).catch(() => {});
    };
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [query, sort, key]);

  const rows = loaded?.key === key ? loaded.rows : [];
  const now = loaded?.at ?? 0;
  const border = theme.palette.divider;
  const columns = compact ? '1fr 56px 80px' : 'minmax(0, 1fr) minmax(0, 220px) 72px 110px 100px';
  const emptyText = loaded?.key !== key ? t.loading : query ? t.noErrorMatches : t.noErrors;
  const open = (row: ErrorRow) => navigate(wayId, `errors/${row.fingerprint}`);

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden', background: theme.palette.background.default }}>
      <PageHeader title={t.errors}>
        <TextField
          size="small"
          value={text}
          onChange={e => setText(e.target.value)}
          placeholder={t.errorsSearchPlaceholder}
          sx={{ flex: 1, minWidth: 160, mx: compact ? 0 : 1 }}
          slotProps={{
            input: {
              startAdornment: <InputAdornment position="start"><SearchIcon sx={{ fontSize: 18 }} /></InputAdornment>,
              endAdornment: (
                <InputAdornment position="end">
                  <Tooltip title={<SearchHelp rows={t.errorsSearchHelp} />} placement="bottom-end">
                    <HelpOutlineIcon sx={{ fontSize: 16, color: theme.palette.text.secondary, cursor: 'help' }} />
                  </Tooltip>
                </InputAdornment>
              ),
            },
          }}
        />
      </PageHeader>

      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, px: 2, py: 0.75, borderBottom: `1px solid ${border}`, flexWrap: 'wrap' }}>
        <ServicePicker services={services} value={serviceOf(text)} onChange={s => setText(prev => withService(prev, s))} />
        <ToggleButtonGroup size="small" exclusive value={sort} onChange={(_, value: Sort | null) => value && setSort(value)}>
          <ToggleButton value="recent" sx={{ py: 0.2, px: 1, fontSize: '0.72rem', textTransform: 'none' }}>{t.sortRecent}</ToggleButton>
          <ToggleButton value="count" sx={{ py: 0.2, px: 1, fontSize: '0.72rem', textTransform: 'none' }}>{t.sortCount}</ToggleButton>
        </ToggleButtonGroup>
        <Box sx={{ flex: 1 }} />
        {stats && (
          <Typography sx={{ fontSize: '0.72rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
            {t.groupsKept(formatCount(stats.groups))} · {t.occurrenceCount(stats.occurrences)}
          </Typography>
        )}
      </Box>

      {stats && !stats.exported && <Alert severity="info" sx={{ borderRadius: 0 }}>{t.errorsNotExported}</Alert>}
      {loaded?.key === key && loaded.error && <Alert severity="error" sx={{ borderRadius: 0, py: 0 }}>{loaded.error}</Alert>}

      <Box sx={{ flex: 1, overflow: 'auto' }}>
        <Box sx={{
          display: 'grid', gridTemplateColumns: columns, gap: 1.5, px: 2, py: 0.5, position: 'sticky', top: 0, zIndex: 1,
          background: theme.palette.background.paper, borderBottom: `1px solid ${border}`,
          fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.05em', color: theme.palette.text.secondary,
        }}>
          <span>{t.errorColumns.error}</span>
          {!compact && <span>{t.errorColumns.where}</span>}
          <Box component="span" sx={{ textAlign: 'right' }}>{t.errorColumns.count}</Box>
          {!compact && <span>{t.errorColumns.trend}</span>}
          <span>{t.errorColumns.last}</span>
        </Box>

        {rows.length === 0 && (
          <Typography sx={{ p: 3, fontSize: '0.85rem', color: theme.palette.text.secondary }}>{emptyText}</Typography>
        )}

        {rows.map(row => (
          <Box
            key={row.fingerprint}
            data-testid="error-row"
            data-fingerprint={row.fingerprint}
            role="link"
            tabIndex={0}
            onClick={() => open(row)}
            onKeyDown={e => { if (e.key === 'Enter') open(row); }}
            sx={{
              display: 'grid', gridTemplateColumns: columns, gap: 1.5, alignItems: 'center', px: 2, py: 0.75,
              borderBottom: `1px solid ${border}`, cursor: 'pointer', fontSize: '0.8rem',
              '&:hover': { background: theme.palette.action.hover },
            }}
          >
            <Box sx={{ minWidth: 0 }}>
              <Tooltip title={row.type} placement="top-start">
                <Box component="span" sx={{ fontWeight: 700, color: theme.palette.error.main }}>{shortType(row.type)}</Box>
              </Tooltip>
              <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontSize: '0.75rem', color: theme.palette.text.secondary }}>
                {firstLine(row.message)}
              </Box>
            </Box>
            {!compact && (
              <Box sx={{ minWidth: 0, fontSize: '0.75rem' }}>
                <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{row.endpoints[0] ?? ''}</Box>
                <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: theme.palette.text.secondary }}>
                  {row.services.join(', ')}
                </Box>
              </Box>
            )}
            <Box sx={{ textAlign: 'right', fontWeight: 700 }}>{formatCount(row.count)}</Box>
            {!compact && <Sparkline values={row.trend} width={96} height={20} />}
            <Box sx={{ fontSize: '0.75rem', color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
              {t.ago(formatDuration(Math.max(0, (now - Date.parse(row.lastSeen)) / 1000)))}
            </Box>
          </Box>
        ))}
      </Box>
    </Box>
  );
};

export default ErrorsPage;
