import type { ThemeOptions } from '@mui/material';

const MONO = "'IBM Plex Mono', 'Fira Code', monospace";

/** MonitorWay's look on top of the shell's theme: monospace type, its own greys and dense inputs. */
export function monitorWayTheme(mode: 'light' | 'dark'): ThemeOptions {
  const isDark = mode === 'dark';
  return {
    palette: {
      background: {
        default: isDark ? '#1a1a1a' : '#f5f5f5',
        paper: isDark ? '#242424' : '#ffffff',
      },
      primary: { main: isDark ? '#90caf9' : '#1976d2' },
      secondary: { main: isDark ? '#ce93d8' : '#9c27b0' },
      error: { main: '#f44336' },
      success: { main: '#4caf50' },
      divider: isDark ? '#333' : '#ddd',
      text: {
        primary: isDark ? '#e0e0e0' : '#1a1a1a',
        secondary: isDark ? '#888' : '#555',
      },
    },
    typography: { fontFamily: MONO },
    components: {
      MuiOutlinedInput: {
        styleOverrides: {
          root: {
            '& fieldset': { borderColor: isDark ? '#444' : '#ccc' },
            '&:hover fieldset': { borderColor: isDark ? '#666' : '#999' },
          },
          input: { fontFamily: MONO, fontSize: '0.875rem' },
        },
      },
      MuiChip: {
        styleOverrides: { root: { fontFamily: MONO, fontSize: '0.7rem' } },
      },
      MuiTooltip: {
        styleOverrides: { tooltip: { fontSize: '0.75rem' } },
      },
    },
  };
}
