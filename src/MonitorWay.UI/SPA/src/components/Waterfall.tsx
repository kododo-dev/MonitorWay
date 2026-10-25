import { useMemo, useState } from 'react';
import Box from '@mui/material/Box';
import IconButton from '@mui/material/IconButton';
import Tooltip from '@mui/material/Tooltip';
import ChevronRightIcon from '@mui/icons-material/ChevronRight';
import ExpandMoreIcon from '@mui/icons-material/ExpandMore';
import { useTheme } from '@mui/material/styles';
import type { TraceDetail } from '../api/api.model';
import { useI18n } from '../i18n/i18n';
import { axisTicks, formatMs, serviceColor, visibleRows, waterfall } from '../utils/traces';

interface Props {
  trace: TraceDetail;
  selectedSpanId: string | null;
  onSelect: (spanId: string) => void;
  /** Log entries per span id, shown as a count on the span. */
  logCounts: ReadonlyMap<string, number>;
  compact: boolean;
}

const ROW_HEIGHT = 26;

/** The spans of a trace on a shared time axis, as a tree that can be folded. */
const Waterfall = ({ trace, selectedSpanId, onSelect, logCounts, compact }: Props) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());
  const rows = useMemo(() => waterfall(trace.spans), [trace.spans]);
  const shown = useMemo(() => visibleRows(rows, collapsed), [rows, collapsed]);
  const total = Math.max(trace.durationMs, 0.001);
  const ticks = useMemo(() => axisTicks(total, compact ? 3 : 6), [total, compact]);
  const nameWidth = compact ? '45%' : '34%';
  const border = theme.palette.divider;

  const toggle = (spanId: string) => setCollapsed(prev => {
    const next = new Set(prev);
    if (!next.delete(spanId)) next.add(spanId);
    return next;
  });

  const percent = (ms: number) => `${Math.min(100, Math.max(0, (ms / total) * 100))}%`;

  return (
    <Box sx={{ fontSize: '0.78rem' }} data-testid="waterfall">
      <Box sx={{ display: 'flex', height: 22, borderBottom: `1px solid ${border}`, color: theme.palette.text.secondary, fontSize: '0.68rem' }}>
        <Box sx={{ width: nameWidth, flexShrink: 0 }} />
        <Box sx={{ flex: 1, position: 'relative', mr: 7 }}>
          {ticks.map(tick => (
            <Box key={tick} sx={{ position: 'absolute', left: percent(tick), top: 4, transform: tick === 0 ? 'none' : 'translateX(-50%)', whiteSpace: 'nowrap' }}>
              {tick === 0 ? '0' : formatMs(tick)}
            </Box>
          ))}
        </Box>
      </Box>

      {shown.map(({ span, depth, descendants }) => {
        const selected = span.spanId === selectedSpanId;
        const failed = span.status === 'Error';
        const color = failed ? theme.palette.error.main : serviceColor(span.service);
        const logs = logCounts.get(span.spanId) ?? 0;
        return (
          <Box
            key={span.spanId}
            data-testid="span-row"
            data-span-id={span.spanId}
            onClick={() => onSelect(span.spanId)}
            sx={{
              display: 'flex', alignItems: 'center', height: ROW_HEIGHT, cursor: 'pointer',
              borderBottom: `1px solid ${isDark ? '#2c2c2c' : '#eee'}`,
              background: selected ? theme.palette.action.selected : undefined,
              '&:hover': { background: selected ? theme.palette.action.selected : theme.palette.action.hover },
            }}
          >
            <Box sx={{ width: nameWidth, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 0.5, pl: 0.5 + depth * 1.5, pr: 1, minWidth: 0 }}>
              {descendants > 0 ? (
                <Tooltip title={collapsed.has(span.spanId) ? t.expand : t.collapse}>
                  <IconButton size="small" sx={{ p: 0.1 }} onClick={e => { e.stopPropagation(); toggle(span.spanId); }}>
                    {collapsed.has(span.spanId) ? <ChevronRightIcon sx={{ fontSize: 16 }} /> : <ExpandMoreIcon sx={{ fontSize: 16 }} />}
                  </IconButton>
                </Tooltip>
              ) : <Box sx={{ width: 18, flexShrink: 0 }} />}
              <Box sx={{ width: 8, height: 8, borderRadius: '50%', background: color, flexShrink: 0 }} />
              <Box sx={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: failed ? theme.palette.error.main : undefined }}>
                {span.name}
              </Box>
              {collapsed.has(span.spanId) && (
                <Box sx={{ fontSize: '0.68rem', color: theme.palette.text.secondary, flexShrink: 0 }}>+{descendants}</Box>
              )}
              {logs > 0 && (
                <Box sx={{ ml: 'auto', flexShrink: 0, fontSize: '0.65rem', px: 0.5, borderRadius: 0.75, background: isDark ? '#333' : '#eee', color: theme.palette.text.secondary }}>
                  {logs}
                </Box>
              )}
            </Box>
            <Box sx={{ flex: 1, position: 'relative', height: '100%', mr: 7 }}>
              {ticks.map(tick => (
                <Box key={tick} sx={{ position: 'absolute', left: percent(tick), top: 0, bottom: 0, borderLeft: `1px dashed ${isDark ? '#333' : '#e6e6e6'}` }} />
              ))}
              <Box sx={{
                position: 'absolute', top: 7, height: ROW_HEIGHT - 14, borderRadius: 0.5, background: color,
                opacity: selected ? 1 : 0.8,
                left: percent(span.offsetMs),
                width: `max(2px, ${percent(span.durationMs)})`,
              }} />
              {span.events.map((e, i) => (
                <Tooltip key={i} title={`${e.name} · ${formatMs(e.offsetMs - span.offsetMs)}`}>
                  <Box sx={{
                    position: 'absolute', top: 9, width: 8, height: 8, ml: '-4px', transform: 'rotate(45deg)',
                    left: percent(e.offsetMs),
                    background: e.name === 'exception' ? theme.palette.error.main : theme.palette.background.paper,
                    border: `1px solid ${e.name === 'exception' ? theme.palette.error.dark : theme.palette.text.secondary}`,
                  }} />
                </Tooltip>
              ))}
              <Box sx={{
                position: 'absolute', top: 5, whiteSpace: 'nowrap', fontSize: '0.68rem', color: theme.palette.text.secondary,
                left: `calc(${percent(span.offsetMs + span.durationMs)} + 4px)`,
              }}>
                {formatMs(span.durationMs)}
              </Box>
            </Box>
          </Box>
        );
      })}
    </Box>
  );
};

export default Waterfall;
