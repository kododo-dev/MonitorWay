import { useRef, useState } from 'react';
import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';
import type { Overview } from '../api/api.model';
import { levelColor } from '../utils/levels';
import { formatCount } from '../utils/format';
import { useI18n } from '../i18n/i18n';

interface Props {
  overview: Overview | null;
  /** Ids of the first and last row in view. */
  visible: { first: number; last: number } | null;
  /** Called with a position in id space when the bar is clicked or dragged. */
  onSeek: (position: number) => void;
}

const WIDTH = 16;

/**
 * The whole source at a glance, next to the list: shading for how many entries there are, red and orange marks
 * where errors and warnings are, and a thumb for the part in view. Clicking or dragging jumps there, which is
 * how one moves through a file with millions of entries.
 */
const OverviewBar = ({ overview, visible, onSeek }: Props) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();
  const barRef = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState<number | null>(null);

  if (!overview || overview.end <= overview.start) return null;

  const span = overview.end - overview.start;
  const count = overview.buckets.length;
  const maxTotal = Math.max(1, ...overview.buckets.map(b => b.total));
  const fractionAt = (clientY: number) => {
    const rect = barRef.current!.getBoundingClientRect();
    return Math.min(1, Math.max(0, (clientY - rect.top) / rect.height));
  };
  const seek = (fraction: number) => onSeek(Math.round(overview.start + fraction * (span - 1)));

  const thumbTop = visible ? Math.max(0, (visible.first - overview.start) / span) : null;
  const thumbHeight = visible ? Math.max(0, (visible.last - visible.first) / span) : 0;

  return (
    <Box
      ref={barRef}
      onMouseDown={e => {
        e.preventDefault();
        setDrag(fractionAt(e.clientY));
        const move = (m: MouseEvent) => setDrag(fractionAt(m.clientY));
        const up = (u: MouseEvent) => {
          window.removeEventListener('mousemove', move);
          window.removeEventListener('mouseup', up);
          setDrag(null);
          seek(fractionAt(u.clientY));
        };
        window.addEventListener('mousemove', move);
        window.addEventListener('mouseup', up);
      }}
      sx={{
        width: WIDTH, flexShrink: 0, position: 'relative', cursor: 'pointer', userSelect: 'none',
        borderLeft: `1px solid ${theme.palette.divider}`,
        background: isDark ? '#181818' : '#f0f0f0',
      }}
    >
      {overview.buckets.map((bucket, i) => {
        const top = `${(i / count) * 100}%`;
        const height = `${100 / count}%`;
        const title = `${formatCount(bucket.total)} · ${t.overviewErrors} ${formatCount(bucket.errors)} · ${t.overviewWarnings} ${formatCount(bucket.warnings)}`;
        return (
          <Box key={i} title={title} sx={{ position: 'absolute', left: 0, right: 0, top, height }}>
            {bucket.total > 0 && (
              <Box sx={{
                position: 'absolute', inset: 0,
                background: isDark ? '#fff' : '#000',
                opacity: 0.04 + 0.12 * (bucket.total / maxTotal),
              }} />
            )}
            {bucket.warnings > 0 && (
              <Box sx={{ position: 'absolute', left: 2, width: 5, top: 0, bottom: 0, minHeight: 2, background: levelColor('Warning', isDark), opacity: 0.85 }} />
            )}
            {bucket.errors > 0 && (
              <Box sx={{ position: 'absolute', right: 2, width: 5, top: 0, bottom: 0, minHeight: 2, background: levelColor('Error', isDark) }} />
            )}
          </Box>
        );
      })}

      {thumbTop !== null && (
        <Box sx={{
          position: 'absolute', left: 0, right: 0,
          top: `${thumbTop * 100}%`, height: `max(4px, ${thumbHeight * 100}%)`,
          border: `2px solid ${theme.palette.primary.main}`, borderRadius: '2px',
          background: isDark ? 'rgba(144, 202, 249, 0.15)' : 'rgba(25, 118, 210, 0.12)',
          pointerEvents: 'none',
        }} />
      )}

      {drag !== null && (
        <Box sx={{
          position: 'absolute', left: -2, right: 0, top: `${drag * 100}%`, height: 2,
          background: theme.palette.primary.main, pointerEvents: 'none',
        }} />
      )}
    </Box>
  );
};

export default OverviewBar;
