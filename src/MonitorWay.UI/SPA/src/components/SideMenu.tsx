import { useParams } from 'react-router-dom';
import { WayMenu, useShell } from '@kododo-dev/way-shell';
import List from '@mui/material/List';
import ListItem from '@mui/material/ListItem';
import ListItemButton from '@mui/material/ListItemButton';
import ListItemIcon from '@mui/material/ListItemIcon';
import ListItemText from '@mui/material/ListItemText';
import BugReportOutlinedIcon from '@mui/icons-material/BugReportOutlined';
import DashboardOutlinedIcon from '@mui/icons-material/DashboardOutlined';
import NotesIcon from '@mui/icons-material/Notes';
import ShowChartIcon from '@mui/icons-material/ShowChart';
import TimelineIcon from '@mui/icons-material/Timeline';
import { useI18n } from '../i18n/i18n';

/** MonitorWay's views in the shell's side menu, one per signal. */
const SideMenu = () => {
  const { navigate, wayId } = useShell();
  const { t } = useI18n();
  const path = useParams()['*'] ?? '';

  const items = [
    { path: 'overview', label: t.overview, icon: <DashboardOutlinedIcon /> },
    { path: 'logs', label: t.logs, icon: <NotesIcon /> },
    { path: 'traces', label: t.traces, icon: <TimelineIcon /> },
    { path: 'errors', label: t.errors, icon: <BugReportOutlinedIcon /> },
    { path: 'metrics', label: t.metrics, icon: <ShowChartIcon /> },
  ];

  return (
    <WayMenu>
      <List dense disablePadding sx={{ py: 1 }}>
        {items.map(item => {
          const active = path === item.path || path.startsWith(`${item.path}/`);
          return (
            <ListItem disablePadding key={item.path}>
              <ListItemButton selected={active} onClick={() => navigate(wayId, item.path)}>
                <ListItemIcon sx={{ minWidth: 32, '& svg': { fontSize: 18 } }}>{item.icon}</ListItemIcon>
                <ListItemText primary={item.label} slotProps={{ primary: { sx: { fontSize: '0.85rem', fontWeight: active ? 600 : 400 } } }} />
              </ListItemButton>
            </ListItem>
          );
        })}
      </List>
    </WayMenu>
  );
};

export default SideMenu;
