import { useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import Popover from '@mui/material/Popover';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import AccessTimeIcon from '@mui/icons-material/AccessTime';
import { useI18n } from '../i18n/i18n';
import { TIME_PRESETS, type TimePreset, type TimeRange } from '../utils/timeRange';

const toLocalInput = (iso?: string) => {
  if (!iso) return '';
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
};

const fromLocalInput = (value: string) => (value ? new Date(value).toISOString() : undefined);

const shortTime = (iso: string) => toLocalInput(iso).slice(5).replace('T', ' ');

interface Props {
  value: TimeRange;
  onChange: (range: TimeRange) => void;
  onJump: (time: string) => void;
}

const TimeRangePicker = ({ value, onChange, onJump }: Props) => {
  const { t } = useI18n();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [jump, setJump] = useState('');

  const open = (target: HTMLElement) => {
    setFrom(toLocalInput(value.from));
    setTo(toLocalInput(value.to));
    setJump(toLocalInput(new Date().toISOString()));
    setAnchor(target);
  };

  const label = value.preset
    ? t.timePresets[value.preset]
    : value.from && value.to
      ? `${shortTime(value.from)} - ${shortTime(value.to)}`
      : value.from
        ? `${t.timeSince} ${shortTime(value.from)}`
        : value.to
          ? `${t.timeUntil} ${shortTime(value.to)}`
          : t.timeAll;
  const active = !!(value.preset || value.from || value.to);

  const choose = (range: TimeRange) => {
    onChange(range);
    setAnchor(null);
  };

  const presetButton = (preset: TimePreset | null) => {
    const selected = preset ? value.preset === preset : !active;
    return (
      <Button
        key={preset ?? 'all'}
        size="small"
        variant={selected ? 'contained' : 'text'}
        onClick={() => choose(preset ? { preset } : {})}
        sx={{ justifyContent: 'flex-start', textTransform: 'none', fontSize: '0.8rem' }}
      >
        {preset ? t.timePresets[preset] : t.timeAll}
      </Button>
    );
  };

  const fieldProps = {
    size: 'small' as const,
    type: 'datetime-local',
    slotProps: { inputLabel: { shrink: true } },
    sx: { '& input': { fontSize: '0.8rem' } },
  };

  return (
    <>
      <Button
        size="small"
        variant={active ? 'contained' : 'outlined'}
        color={active ? 'primary' : 'inherit'}
        startIcon={<AccessTimeIcon sx={{ fontSize: 16 }} />}
        onClick={e => open(e.currentTarget)}
        sx={{ textTransform: 'none', fontSize: '0.75rem', whiteSpace: 'nowrap' }}
      >
        {label}
      </Button>
      <Popover
        open={anchor !== null}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      >
        <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 1, width: 300 }}>
          {presetButton(null)}
          {TIME_PRESETS.map(presetButton)}

          <Divider sx={{ my: 1 }} />
          <Typography sx={{ fontSize: '0.75rem', color: 'text.secondary' }}>{t.timeCustom}</Typography>
          <TextField {...fieldProps} label={t.timeFrom} value={from} onChange={e => setFrom(e.target.value)} />
          <TextField {...fieldProps} label={t.timeTo} value={to} onChange={e => setTo(e.target.value)} />
          <Button
            size="small"
            variant="outlined"
            disabled={!from && !to}
            onClick={() => choose({ from: fromLocalInput(from), to: fromLocalInput(to) })}
            sx={{ textTransform: 'none' }}
          >
            {t.timeApply}
          </Button>

          <Divider sx={{ my: 1 }} />
          <Typography sx={{ fontSize: '0.75rem', color: 'text.secondary' }}>{t.timeJumpHint}</Typography>
          <Box sx={{ display: 'flex', gap: 1 }}>
            <TextField {...fieldProps} label={t.timeJump} value={jump} onChange={e => setJump(e.target.value)} sx={{ ...fieldProps.sx, flex: 1 }} />
            <Button
              size="small"
              variant="outlined"
              disabled={!jump}
              onClick={() => {
                onJump(fromLocalInput(jump)!);
                setAnchor(null);
              }}
              sx={{ textTransform: 'none' }}
            >
              {t.timeGo}
            </Button>
          </Box>
        </Box>
      </Popover>
    </>
  );
};

export default TimeRangePicker;
