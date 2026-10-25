import MenuItem from '@mui/material/MenuItem';
import TextField from '@mui/material/TextField';
import { useI18n } from '../i18n/i18n';

interface Props {
  services: string[];
  value: string | null;
  onChange: (service: string | null) => void;
}

/**
 * Narrows a view to one service. Shown only when there is a choice: the application alone, without services sending
 * over OTLP, needs no filter.
 */
const ServicePicker = ({ services, value, onChange }: Props) => {
  const { t } = useI18n();
  // A service chosen in the address that has not sent anything yet still shows as chosen.
  const options = value && !services.includes(value) ? [...services, value] : services;
  if (options.length < 2) return null;
  return (
    <TextField
      select
      size="small"
      value={value ?? ''}
      onChange={e => onChange(e.target.value || null)}
      data-testid="service-picker"
      sx={{ minWidth: 160, '& .MuiSelect-select': { py: 0.5, fontSize: '0.78rem' } }}
      slotProps={{ select: { displayEmpty: true } }}
    >
      <MenuItem value="">{t.allServices}</MenuItem>
      {options.map(service => <MenuItem key={service} value={service}>{service}</MenuItem>)}
    </TextField>
  );
};

export default ServicePicker;
