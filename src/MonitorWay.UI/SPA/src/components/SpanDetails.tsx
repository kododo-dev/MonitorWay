import type { ReactNode } from 'react';
import Box from '@mui/material/Box';
import Link from '@mui/material/Link';
import Typography from '@mui/material/Typography';
import { useTheme } from '@mui/material/styles';
import { useShell } from '@kododo-dev/way-shell';
import type { Property, SpanInfo } from '../api/api.model';
import { useI18n } from '../i18n/i18n';
import { formatFullTime } from '../utils/format';
import { formatMs, serviceColor } from '../utils/traces';

/** Everything about one span: timing, status, attributes, events (exceptions with their stack) and links. */
const SpanDetails = ({ span }: { span: SpanInfo }) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const { navigate, wayId } = useShell();
  const failed = span.status === 'Error';

  const label = { fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.06em', color: theme.palette.text.secondary, mt: 2, mb: 0.5 } as const;
  const block = {
    m: 0, p: 1.25, borderRadius: 1, fontSize: '0.75rem', whiteSpace: 'pre', overflowX: 'auto',
    fontFamily: "'IBM Plex Mono', monospace", background: isDark ? '#1b1b1b' : '#f7f7f7',
    border: `1px solid ${theme.palette.divider}`,
  } as const;

  return (
    <Box data-testid="span-details">
      <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', wordBreak: 'break-word', color: failed ? theme.palette.error.main : undefined }}>
        {span.name}
      </Typography>
      <Table rows={[
        [t.spanService, span.service && (
          <Box component="span" sx={{ display: 'inline-flex', alignItems: 'center', gap: 0.5 }}>
            <Box component="span" sx={{ width: 8, height: 8, borderRadius: '50%', background: serviceColor(span.service) }} />
            {span.service}
          </Box>
        )],
        [t.spanKind, span.kind],
        [t.spanStatus, (
          <Box component="span" sx={{ color: failed ? theme.palette.error.main : undefined }}>
            {span.status}{span.statusDescription ? `: ${span.statusDescription}` : ''}
          </Box>
        )],
        [t.spanStart, `${formatFullTime(span.start)} (+${formatMs(span.offsetMs)})`],
        [t.spanDuration, formatMs(span.durationMs)],
        [t.spanScope, span.scope],
        ['SpanId', span.spanId],
      ]} />

      {span.attributes.length > 0 && (
        <>
          <Typography sx={label}>{t.attributes}</Typography>
          <Table rows={span.attributes.map(a => [a.key, a.value])} />
        </>
      )}

      {span.events.length > 0 && (
        <>
          <Typography sx={label}>{t.events}</Typography>
          {span.events.map((e, i) => {
            const value = (key: string) => e.attributes.find(a => a.key === key)?.value;
            const exception = e.name === 'exception';
            const stack = exception ? value('exception.stacktrace') : null;
            const rest: Property[] = exception ? e.attributes.filter(a => a.key !== 'exception.stacktrace') : e.attributes;
            return (
              <Box key={i} sx={{ mb: 1.5 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  <Typography sx={{ fontSize: '0.78rem', fontWeight: 600, color: exception ? theme.palette.error.main : undefined }}>
                    {e.name} <Box component="span" sx={{ fontWeight: 400, color: theme.palette.text.secondary }}>+{formatMs(e.offsetMs - span.offsetMs)}</Box>
                  </Typography>
                  {e.errorFingerprint && (
                    <Link component="button" onClick={() => navigate(wayId, `errors/${e.errorFingerprint}`)} sx={{ fontFamily: 'inherit', fontSize: '0.75rem' }}>
                      {t.openError}
                    </Link>
                  )}
                </Box>
                {rest.length > 0 && <Table rows={rest.map(a => [a.key, a.value])} />}
                {stack && <Box component="pre" sx={{ ...block, mt: 0.5, color: theme.palette.error.main }}>{stack}</Box>}
              </Box>
            );
          })}
        </>
      )}

      {span.links.length > 0 && (
        <>
          <Typography sx={label}>{t.links}</Typography>
          {span.links.map(l => (
            <Box key={`${l.traceId}-${l.spanId}`} sx={{ fontSize: '0.75rem' }}>
              <Link component="button" onClick={() => navigate(wayId, `traces/${l.traceId}`)} sx={{ fontFamily: 'inherit' }}>
                {l.traceId}
              </Link>
              {' · '}{l.spanId}
            </Box>
          ))}
        </>
      )}
    </Box>
  );
};

const Table = ({ rows }: { rows: [string, ReactNode][] }) => {
  const theme = useTheme();
  const cell = { py: 0.4, verticalAlign: 'top', borderBottom: `1px solid ${theme.palette.divider}` } as const;
  return (
    <Box component="table" sx={{ borderCollapse: 'collapse', width: '100%', fontSize: '0.75rem', mt: 0.5 }}>
      <tbody>
        {rows.filter(([, value]) => value !== null && value !== undefined && value !== '').map(([name, value]) => (
          <tr key={name}>
            <Box component="td" sx={{ ...cell, pr: 2, color: theme.palette.text.secondary, whiteSpace: 'nowrap' }}>{name}</Box>
            <Box component="td" sx={{ ...cell, wordBreak: 'break-all' }}>{value}</Box>
          </tr>
        ))}
      </tbody>
    </Box>
  );
};

export default SpanDetails;
