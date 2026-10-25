import { useEffect, useState } from 'react';
import { fetchOverview } from '../api/api';
import type { Overview } from '../api/api.model';

const REFRESH_MS = 5_000;
const BUCKETS = 150;

/** The overview of a source, refreshed while the index grows and new entries arrive. */
export function useOverview(sourceId: string | null): Overview | null {
  // Kept with the source it belongs to, so switching sources never shows the previous source's overview.
  const [loaded, setLoaded] = useState<{ sourceId: string; overview: Overview | null } | null>(null);

  useEffect(() => {
    if (!sourceId) return;
    let active = true;
    const load = () =>
      fetchOverview(sourceId, BUCKETS)
        .then(overview => { if (active) setLoaded({ sourceId, overview }); })
        .catch(() => { /* keep the last known overview */ });

    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, [sourceId]);

  return loaded?.sourceId === sourceId ? loaded.overview : null;
}
