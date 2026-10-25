import { useState } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { useShell } from '@kododo-dev/way-shell';
import { setApi } from './api/api';
import SideMenu from './components/SideMenu';
import ErrorPage from './pages/ErrorPage';
import ErrorsPage from './pages/ErrorsPage';
import LogsPage from './pages/LogsPage';
import MetricsPage from './pages/MetricsPage';
import OverviewPage from './pages/OverviewPage';
import TracePage from './pages/TracePage';
import TracesPage from './pages/TracesPage';

// The monitor inside Way.Shell: the shell provides the router, theme, language and layout; the signals go into the
// shell's side menu. The overview, then a view per signal.
function App() {
  const { api } = useShell();
  // Before the first render of a page, which fetches right away.
  useState(() => setApi(api));
  return (
    <>
      <SideMenu />
      <Routes>
        <Route index element={<Navigate to="overview" replace />} />
        <Route path="overview" element={<OverviewPage />} />
        <Route path="logs" element={<LogsPage />} />
        <Route path="traces" element={<TracesPage />} />
        <Route path="traces/:traceId" element={<TracePage />} />
        <Route path="errors" element={<ErrorsPage />} />
        <Route path="errors/:fingerprint" element={<ErrorPage />} />
        <Route path="metrics" element={<MetricsPage />} />
        <Route path="*" element={<Navigate to="overview" replace />} />
      </Routes>
    </>
  );
}

export default App;
