import React, { useEffect, useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import Typography from '@mui/material/Typography';
import AccountTreeOutlinedIcon from '@mui/icons-material/AccountTreeOutlined';
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrong';
import CloseIcon from '@mui/icons-material/Close';
import ContentCopyIcon from '@mui/icons-material/ContentCopy';
import FilterAltOutlinedIcon from '@mui/icons-material/FilterAltOutlined';
import TimelineIcon from '@mui/icons-material/Timeline';
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined';
import { useShell } from '@kododo-dev/way-shell';
import { useTheme } from '@mui/material/styles';
import { areTracesExported, fetchEntry } from '../api/api';
import type { EntryDetail } from '../api/api.model';
import { levelColor } from '../utils/levels';
import { formatFullTime } from '../utils/format';
import { fieldToken } from '../utils/search';
import { useI18n } from '../i18n/i18n';
import Highlight from './Highlight';

interface Props {
  sourceId: string;
  id: number;
  onClose: () => void;
  /** Adds a search token, e.g. `OrderId:1008`. */
  onFilter: (token: string) => void;
  onOnlyCategory: (category: string) => void;
  onContext: (id: number) => void;
  /** Shows every entry of the request (trace). */
  onRequest: (traceId: string) => void;
  highlight: RegExp | null;
  overlay: boolean;
}

const EntryDetails = ({ sourceId, id, onClose, onFilter, onOnlyCategory, onContext, onRequest, highlight, overlay }: Props) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const [traces, setTraces] = useState(false);
  useEffect(() => {
    let active = true;
    areTracesExported().then(exported => { if (active) setTraces(exported); });
    return () => { active = false; };
  }, []);
  // Kept with the entry it was loaded for; while another entry loads, `entry` is undefined.
  const [loaded, setLoaded] = useState<{ key: string; entry: EntryDetail | null } | null>(null);
  const key = `${sourceId}:${id}`;
  const entry = loaded?.key === key ? loaded.entry : undefined;

  useEffect(() => {
    let active = true;
    const current = `${sourceId}:${id}`;
    fetchEntry(sourceId, id)
      .then(e => { if (active) setLoaded({ key: current, entry: e }); })
      .catch(() => { if (active) setLoaded({ key: current, entry: null }); });
    return () => { active = false; };
  }, [sourceId, id]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const copy = () => {
    if (!entry) return;
    const text = [entry.message, entry.exception].filter(Boolean).join('\n');
    navigator.clipboard?.writeText(text).catch(() => {});
  };

  const traceId = entry?.traceId ?? entry?.properties.find(p => p.key === 'TraceId')?.value ?? null;

  const label = { fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary, mt: 2, mb: 0.5 } as const;
  const block = {
    m: 0, p: 1.25, borderRadius: 1, fontSize: '0.78rem', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
    fontFamily: "'IBM Plex Mono', monospace", background: isDark ? '#1b1b1b' : '#f7f7f7',
    border: `1px solid ${theme.palette.divider}`,
  } as const;
  const actionButton = { textTransform: 'none', fontSize: '0.72rem', py: 0.25 } as const;

  let content: React.ReactNode;
  if (entry === undefined) {
    content = null;
  } else if (entry === null) {
    content = <Typography sx={{ fontSize: '0.85rem', color: theme.palette.text.secondary }}>{t.entryGone}</Typography>;
  } else {
    content = (
      <>
        <Box sx={{ display: 'flex', gap: 1.5, alignItems: 'baseline', flexWrap: 'wrap' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: levelColor(entry.level, isDark) }}>{entry.level}</Typography>
          <Typography sx={{ fontSize: '0.8rem', color: theme.palette.text.secondary }}>{formatFullTime(entry.timestamp)}</Typography>
        </Box>
        {entry.category && (
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mt: 0.5 }}>
            <Typography sx={{ fontSize: '0.78rem', color: theme.palette.text.secondary, wordBreak: 'break-all' }}>{entry.category}</Typography>
            <FilterButton title={t.onlyCategory} onClick={() => onOnlyCategory(entry.category!)} />
          </Box>
        )}

        <Box sx={{ display: 'flex', gap: 1, mt: 1.5, flexWrap: 'wrap' }}>
          <Button size="small" variant="outlined" startIcon={<CenterFocusStrongIcon sx={{ fontSize: 16 }} />}
            onClick={() => onContext(entry.id)} sx={actionButton}>
            {t.showContext}
          </Button>
          {traceId && (
            <Button size="small" variant="outlined" startIcon={<AccountTreeOutlinedIcon sx={{ fontSize: 16 }} />}
              onClick={() => onRequest(traceId)} sx={actionButton}>
              {t.sameRequest}
            </Button>
          )}
          {traceId && traces && (
            <Button size="small" variant="outlined" startIcon={<TimelineIcon sx={{ fontSize: 16 }} />}
              onClick={() => navigate(wayId, `traces/${traceId}`)} sx={actionButton}>
              {t.openTrace}
            </Button>
          )}
          {entry.errorFingerprint && sourceId === 'memory' && (
            <Button size="small" variant="outlined" color="error" startIcon={<BugReportOutlinedIcon sx={{ fontSize: 16 }} />}
              onClick={() => navigate(wayId, `errors/${entry.errorFingerprint}`)} sx={actionButton}>
              {t.openError}
            </Button>
          )}
        </Box>

        <Typography sx={label}>{t.message}</Typography>
        <Box component="pre" sx={block}><Highlight text={entry.message} pattern={highlight} /></Box>

        {entry.exception && (
          <>
            <Typography sx={label}>{t.exception}</Typography>
            <Box component="pre" sx={{ ...block, color: levelColor('Error', isDark), whiteSpace: 'pre', overflowX: 'auto', wordBreak: 'normal' }}>
              {entry.exception}
            </Box>
          </>
        )}

        {entry.messageTemplate && entry.messageTemplate !== entry.message && (
          <>
            <Typography sx={label}>{t.template}</Typography>
            <Box component="pre" sx={block}>{entry.messageTemplate}</Box>
          </>
        )}

        {(entry.properties.length > 0 || entry.traceId) && (
          <>
            <Typography sx={label}>{t.properties}</Typography>
            <Box component="table" sx={{ borderCollapse: 'collapse', width: '100%', fontSize: '0.78rem' }}>
              <tbody>
                {entry.properties.map((p, i) => (
                  <PropertyRow key={i} name={p.key} value={p.value} onFilter={onFilter} />
                ))}
                {/* ASP.NET Core's activity tracking may already put them in the scope. */}
                {entry.traceId && !entry.properties.some(p => p.key === 'TraceId') && <PropertyRow name="TraceId" value={entry.traceId} onFilter={onFilter} />}
                {entry.spanId && !entry.properties.some(p => p.key === 'SpanId') && <PropertyRow name="SpanId" value={entry.spanId} onFilter={onFilter} />}
              </tbody>
            </Box>
          </>
        )}
      </>
    );
  }

  return (
    <Box sx={{
      width: overlay ? '100%' : '42%',
      minWidth: overlay ? undefined : 360,
      maxWidth: overlay ? undefined : 760,
      position: overlay ? 'absolute' : 'relative',
      inset: overlay ? 0 : undefined,
      zIndex: overlay ? 2 : undefined,
      display: 'flex', flexDirection: 'column',
      borderLeft: `1px solid ${theme.palette.divider}`,
      background: theme.palette.background.paper,
    }}>
      <Box sx={{ display: 'flex', alignItems: 'center', px: 2, height: 40, borderBottom: `1px solid ${theme.palette.divider}`, flexShrink: 0 }}>
        <Typography sx={{ flex: 1, fontSize: '0.8rem', fontWeight: 700 }}>{t.details}</Typography>
        <Tooltip title={t.copy}>
          <span>
            <IconButton size="small" onClick={copy} disabled={!entry}><ContentCopyIcon sx={{ fontSize: 16 }} /></IconButton>
          </span>
        </Tooltip>
        <Tooltip title={t.close}>
          <IconButton size="small" onClick={onClose}><CloseIcon sx={{ fontSize: 18 }} /></IconButton>
        </Tooltip>
      </Box>
      <Box sx={{ flex: 1, overflow: 'auto', p: 2 }}>{content}</Box>
    </Box>
  );
};

const FilterButton = ({ title, onClick }: { title: string; onClick: () => void }) => (
  <Tooltip title={title}>
    <IconButton size="small" onClick={onClick} sx={{ p: 0.25, opacity: 0.6, '&:hover': { opacity: 1 } }}>
      <FilterAltOutlinedIcon sx={{ fontSize: 14 }} />
    </IconButton>
  </Tooltip>
);

const PropertyRow = ({ name, value, onFilter }: { name: string; value: string | null; onFilter: (token: string) => void }) => {
  const theme = useTheme();
  const { t } = useI18n();
  const cell = { py: 0.5, verticalAlign: 'top', borderBottom: `1px solid ${theme.palette.divider}` } as const;
  const token = value === null ? null : fieldToken(name === 'TraceId' ? 'trace' : name, value);
  return (
    <Box component="tr" sx={{ '& .filter': { visibility: 'hidden' }, '&:hover .filter': { visibility: 'visible' } }}>
      <Box component="td" sx={{ ...cell, pr: 2, color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>
        {name}
      </Box>
      <Box component="td" sx={{ ...cell, wordBreak: 'break-all' }}>
        {value ?? <Box component="span" sx={{ color: theme.palette.text.secondary }}>null</Box>}
      </Box>
      <Box component="td" sx={{ ...cell, width: 24 }}>
        {token && (
          <Box className="filter">
            <FilterButton title={`${t.filterBy} ${token}`} onClick={() => onFilter(token)} />
          </Box>
        )}
      </Box>
    </Box>
  );
};

export default EntryDetails;
