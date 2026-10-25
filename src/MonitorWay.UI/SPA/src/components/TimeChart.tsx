import { useEffect, useLayoutEffect, useRef } from 'react';
import Box from '@mui/material/Box';
import { useTheme } from '@mui/material/styles';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';

export interface ChartSeries {
  label: string;
  /** One per time; `null` where there is no data, drawn as a gap. */
  values: (number | null)[];
  color: string;
}

interface Props {
  /** Unix seconds. */
  times: number[];
  series: ChartSeries[];
  /** A value as people read it, for the axis and the legend. */
  format: (value: number) => string;
  height?: number;
  /** Bars rather than lines, for counts per period. */
  bars?: boolean;
  /** A range dragged on the chart, in Unix seconds. */
  onSelect?: (from: number, to: number) => void;
}

/** Time series drawn with uPlot: an axis in local time, a legend with the values under the cursor, drag to select. */
const TimeChart = ({ times, series, format, height = 200, bars, onSelect }: Props) => {
  const theme = useTheme();
  const target = useRef<HTMLDivElement>(null);
  const chart = useRef<uPlot | null>(null);
  // Read by the chart's callbacks, so new functions do not rebuild it.
  const latest = useRef({ times, series, format, onSelect });
  useLayoutEffect(() => {
    latest.current = { times, series, format, onSelect };
  });

  const isDark = theme.palette.mode === 'dark';
  const shape = JSON.stringify([series.map(s => [s.label, s.color]), height, bars, isDark]);

  useEffect(() => {
    const element = target.current;
    if (!element) return;
    const axis = {
      stroke: theme.palette.text.secondary,
      font: "11px 'IBM Plex Mono', monospace",
      grid: { stroke: isDark ? '#2e2e2e' : '#ececec', width: 1 },
      ticks: { stroke: isDark ? '#2e2e2e' : '#ececec', width: 1 },
    };
    const { series: lines } = latest.current;
    const options: uPlot.Options = {
      width: Math.max(200, element.clientWidth),
      height,
      scales: { x: { time: true }, y: { range: (_u, min, max) => [Math.min(0, min), max > 0 ? max * 1.1 : 1] } },
      axes: [
        axis,
        { ...axis, size: 72, values: (_u, splits) => splits.map(v => (v === 0 ? '0' : latest.current.format(v))) },
      ],
      series: [
        {},
        ...lines.map(s => ({
          label: s.label,
          stroke: s.color,
          fill: bars ? `${s.color}cc` : undefined,
          width: bars ? 0 : 1.5,
          spanGaps: false,
          points: { show: false },
          paths: bars ? uPlot.paths.bars!({ size: [0.7, 24], gap: 1 }) : undefined,
          value: (_u: uPlot, v: number | null) => (v === null || v === undefined ? '-' : latest.current.format(v)),
        })),
      ],
      // The selection is made below with pointer events: uPlot's own dragging does not react in WebKit, and pointer
      // events also work with a finger.
      cursor: { drag: { x: false, y: false, setScale: false } },
      legend: { show: true, live: true },
    };
    const data: uPlot.AlignedData = [latest.current.times, ...lines.map(s => s.values)];
    const plot = new uPlot(options, data, element);
    chart.current = plot;

    const over = plot.over;
    let start: number | null = null;
    const x = (e: PointerEvent) => Math.min(Math.max(e.clientX - over.getBoundingClientRect().left, 0), over.clientWidth);
    const select = (left: number, width: number) => plot.setSelect({ left, width, top: 0, height: over.clientHeight }, false);
    const down = (e: PointerEvent) => {
      if (e.button !== 0 || !latest.current.onSelect) return;
      start = x(e);
      over.setPointerCapture(e.pointerId);
    };
    const move = (e: PointerEvent) => {
      if (start === null) return;
      const end = x(e);
      select(Math.min(start, end), Math.abs(end - start));
    };
    const up = (e: PointerEvent) => {
      if (start === null) return;
      const [from, to] = [Math.min(start, x(e)), Math.max(start, x(e))];
      start = null;
      select(0, 0);
      if (to - from >= 5)
        latest.current.onSelect?.(plot.posToVal(from, 'x'), plot.posToVal(to, 'x'));
    };
    const cancel = () => {
      start = null;
      select(0, 0);
    };
    over.addEventListener('pointerdown', down);
    over.addEventListener('pointermove', move);
    over.addEventListener('pointerup', up);
    over.addEventListener('pointercancel', cancel);

    const resize = new ResizeObserver(() => plot.setSize({ width: Math.max(200, element.clientWidth), height }));
    resize.observe(element);
    return () => {
      resize.disconnect();
      over.removeEventListener('pointerdown', down);
      over.removeEventListener('pointermove', move);
      over.removeEventListener('pointerup', up);
      over.removeEventListener('pointercancel', cancel);
      plot.destroy();
      chart.current = null;
    };
    // The chart is built again only when its shape changes; data goes through setData.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [shape]);

  useEffect(() => {
    chart.current?.setData([times, ...series.map(s => s.values)]);
  }, [times, series]);

  return (
    <Box
      ref={target}
      data-testid="time-chart"
      sx={{
        width: '100%',
        cursor: onSelect ? 'crosshair' : undefined,
        '& .u-legend': { fontSize: '0.72rem', color: theme.palette.text.primary, textAlign: 'left', mt: 0.5 },
        '& .u-legend th': { fontWeight: 400 },
        '& .u-legend .u-marker': { width: 10, height: 10 },
        // A finger drags across to select and still scrolls the page up and down.
        '& .u-over': { touchAction: onSelect ? 'pan-y' : undefined },
        '& .u-select': { background: isDark ? 'rgba(144,202,249,0.15)' : 'rgba(25,118,210,0.12)' },
      }}
    />
  );
};

export default TimeChart;
