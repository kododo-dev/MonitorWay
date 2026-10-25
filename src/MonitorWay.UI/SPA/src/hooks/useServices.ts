import { useEffect, useState } from 'react';
import { fetchServices } from '../api/api';

const REFRESH_MS = 30_000;

/** Names of the services telemetry came from, refreshed now and then: others start sending over OTLP at any time. */
export function useServices(): string[] {
  const [services, setServices] = useState<string[]>([]);
  useEffect(() => {
    let active = true;
    const load = () => fetchServices()
      .then(list => { if (active) setServices(list.map(s => s.name)); })
      .catch(() => { /* keep the last known services */ });
    load();
    const timer = window.setInterval(load, REFRESH_MS);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);
  return services;
}
