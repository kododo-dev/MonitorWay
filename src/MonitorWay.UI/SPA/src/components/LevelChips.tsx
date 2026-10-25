import Box from '@mui/material/Box';
import Chip from '@mui/material/Chip';
import Tooltip from '@mui/material/Tooltip';
import { useTheme } from '@mui/material/styles';
import { LEVELS, type Level, type Pool, type Stats } from '../api/api.model';
import { LEVEL_SHORT, levelColor } from '../utils/levels';
import { formatBytes, formatCount, formatDuration } from '../utils/format';
import { useI18n } from '../i18n/i18n';
import type { TranslationKeys } from '../i18n/translations';

interface Props {
  selected: ReadonlySet<Level>;
  onToggle: (level: Level) => void;
  stats: Stats | null;
}

function retentionLines(pool: Pool, level: Level, stats: Stats, t: TranslationKeys): string[] {
  const limits: string[] = [];
  if (pool.maxEntries != null) limits.push(t.retentionEntries(formatCount(pool.maxEntries)));
  if (pool.maxBytes != null) limits.push(t.retentionBytes(formatBytes(pool.maxBytes)));
  if (pool.maxAgeSeconds != null) limits.push(t.retentionAge(formatDuration(pool.maxAgeSeconds)));

  const lines = [`${t.retention}: ${limits.length ? limits.join(', ') : t.retentionGlobal}`];
  if (pool.oldest) {
    const age = (new Date(stats.now).getTime() - new Date(pool.oldest).getTime()) / 1000;
    lines.push(`${t.oldest}: ${t.ago(formatDuration(Math.max(0, age)))}`);
  }
  if (pool.evicted > 0) lines.push(`${t.evicted}: ${formatCount(pool.evicted)}`);
  const others = pool.levels.filter(l => l !== level);
  if (others.length) lines.push(`${t.sharedPool}: ${others.join(', ')}`);
  return lines;
}

/** Short summary of how far back a level reaches, shown on the chip itself. */
function retentionHint(pool: Pool, stats: Stats): string | null {
  if (!pool.oldest || pool.count === 0) return null;
  const age = (new Date(stats.now).getTime() - new Date(pool.oldest).getTime()) / 1000;
  return formatDuration(Math.max(0, age));
}

const LevelChips = ({ selected, onToggle, stats }: Props) => {
  const theme = useTheme();
  const isDark = theme.palette.mode === 'dark';
  const { t } = useI18n();

  return (
    <Box sx={{ display: 'flex', gap: 0.75, flexWrap: 'wrap' }}>
      {LEVELS.map(level => {
        const levelStats = stats?.levels.find(l => l.level === level);
        const pool = levelStats && stats ? stats.pools[levelStats.pool] : undefined;
        const active = selected.has(level);
        const color = levelColor(level, isDark);
        const hint = pool && stats ? retentionHint(pool, stats) : null;

        const chip = (
          <Chip
            size="small"
            onClick={() => onToggle(level)}
            label={
              <Box component="span" sx={{ display: 'inline-flex', gap: 0.75, alignItems: 'baseline' }}>
                <Box component="span" sx={{ fontWeight: 700 }}>{LEVEL_SHORT[level]}</Box>
                {levelStats && <Box component="span">{formatCount(levelStats.count)}</Box>}
                {hint && <Box component="span" sx={{ opacity: 0.6 }}>· {hint}</Box>}
              </Box>
            }
            variant={active ? 'filled' : 'outlined'}
            sx={{
              borderColor: color,
              color: active ? (isDark ? '#111' : '#fff') : color,
              background: active ? color : 'transparent',
              opacity: active ? 1 : 0.7,
              '&:hover': { background: active ? color : undefined, opacity: 1 },
            }}
          />
        );

        return pool && stats ? (
          <Tooltip
            key={level}
            title={<Box sx={{ whiteSpace: 'pre-line' }}>{`${level}\n${retentionLines(pool, level, stats, t).join('\n')}`}</Box>}
            enterDelay={400}
          >
            {chip}
          </Tooltip>
        ) : (
          <Box key={level} component="span">{chip}</Box>
        );
      })}
    </Box>
  );
};

export default LevelChips;
