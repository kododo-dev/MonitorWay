import { useEffect, useState } from 'react';
import { fetchStats } from '../api/api';
import type { Stats } from '../api/api.model';

const REFRESH_MS = 5_000;

/** Level counts and retention pools of a source, refreshed periodically. */
export function useStats(sourceId: string | null): Stats | null {
  const [stats, setStats] = useState<Stats | null>(null);

  useEffect(() => {
    if (!sourceId) return;
    let active = true;
    const load = () =>
      fetchStats(sourceId)
        .then(s => { if (active) setStats(s); })
        .catch(() => { /* keep the last known stats */ });

    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [sourceId]);

  return stats;
}
