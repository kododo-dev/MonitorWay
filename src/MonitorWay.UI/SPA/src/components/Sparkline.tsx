import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';

interface Props {
  /** Counts per period, oldest first. */
  values: number[];
  width: number;
  height: number;
  /** Describes a bar for its tooltip, by its index. */
  label?: (index: number, value: number) => string;
}

/** Counts over time as small bars on a shared scale; an empty period shows as a thin line. */
const Sparkline = ({ values, width, height, label }: Props) => {
  const theme = useTheme();
  const max = Math.max(1, ...values);
  const step = width / Math.max(1, values.length);
  const bar = Math.max(1, step - (step > 3 ? 1 : 0));
  return (
    <Box component="svg" width={width} height={height} viewBox={`0 0 ${width} ${height}`} sx={{ display: 'block', flexShrink: 0 }} aria-hidden>
      <line x1={0} x2={width} y1={height - 0.5} y2={height - 0.5} stroke={theme.palette.divider} />
      {values.map((value, i) => {
        if (!value) return null;
        const h = Math.max(2, (value / max) * (height - 1));
        return (
          <rect key={i} x={i * step} y={height - h} width={bar} height={h} fill={theme.palette.error.main} opacity={0.85}>
            {label && <title>{label(i, value)}</title>}
          </rect>
        );
      })}
    </Box>
  );
};

export default Sparkline;
