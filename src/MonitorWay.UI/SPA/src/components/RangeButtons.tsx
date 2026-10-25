import Chip from '@mui/material/Chip';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import { useI18n } from '../i18n/i18n';
import { formatRowTime } from '../utils/format';
import { TIME_PRESETS, type TimePreset, type TimeRange } from '../utils/timeRange';

/** The presets as buttons, and the dragged range as a chip that goes back to the preset. */
const RangeButtons = ({ value, onChange }: { value: TimeRange; onChange: (range: TimeRange) => void }) => {
  const { t } = useI18n();
  if (!value.preset && value.from && value.to) {
    return (
      <Chip
        size="small"
        color="primary"
        variant="outlined"
        label={t.selectedRange(formatRowTime(value.from).slice(0, 14), formatRowTime(value.to).slice(0, 14))}
        onDelete={() => onChange({ preset: '1h' })}
        sx={{ fontSize: '0.72rem' }}
      />
    );
  }
  return (
    <ToggleButtonGroup size="small" exclusive value={value.preset} onChange={(_, preset: TimePreset | null) => preset && onChange({ preset })}>
      {TIME_PRESETS.map(preset => (
        <ToggleButton key={preset} value={preset} sx={{ py: 0.2, px: 1, fontSize: '0.72rem', textTransform: 'none' }}>
          {preset}
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  );
};

export default RangeButtons;
