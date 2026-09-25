import { useEffect, useState } from 'react';
import { api, MAINTENANCE_EVENT } from '../api/client';
import { ru } from '../i18n/ru';

/** How often the banner asks the site whether it only reads: a deploy's maintenance lasts about a minute. */
export const STATUS_POLL_MS = 30_000;

/**
 * The maintenance banner (SPEC «Режим обслуживания», A8, D-121): shown while the site only reads. It asks on start, every
 * half a minute and at once when a write meets a 503; it hides again when the site answers that it writes.
 */
export function MaintenanceBanner() {
  const [on, setOn] = useState(false);

  useEffect(() => {
    let alive = true;
    const ask = async () => {
      try {
        const { data } = await api.GET('/api/status');
        if (alive && data) setOn(data.maintenance);
      } catch {
        // No answer is not maintenance: the page says so where the request failed
      }
    };
    const asked = () => void ask();
    void ask();
    const timer = globalThis.setInterval(asked, STATUS_POLL_MS);
    globalThis.addEventListener(MAINTENANCE_EVENT, asked);
    return () => {
      alive = false;
      globalThis.clearInterval(timer);
      globalThis.removeEventListener(MAINTENANCE_EVENT, asked);
    };
  }, []);

  if (!on) return null;
  return (
    <div role="status" data-testid="maintenance-banner">
      {ru.maintenance.banner}
    </div>
  );
}
