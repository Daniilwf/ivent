import { FlaskConical } from 'lucide-react';
import { useEffect, useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { siteEnvironment } from './siteStatus';

type Environment = Schemas['SiteEnvironment'];

/**
 * The strip on every page of a copy that is not the live site (H9, D-220): the test copy, a developer's machine, the
 * tests. The environment does not change while the page is open, so the site is asked once; no answer shows nothing.
 */
export function EnvironmentBanner() {
  const [environment, setEnvironment] = useState<Environment | null>(null);

  useEffect(() => {
    let alive = true;
    // No answer is no strip: the live site must never show one by mistake, and a copy says so on the next load
    void siteEnvironment().then((status) => {
      if (alive && status) setEnvironment(status.environment);
    });
    return () => {
      alive = false;
    };
  }, []);

  // Only a copy the dictionary names: an answer without the environment is no strip either
  if (
    environment === null ||
    environment === 'production' ||
    !Object.hasOwn(ru.environment, environment)
  )
    return null;
  return (
    <aside
      aria-label={ru.environment.label}
      data-testid="environment-banner"
      data-environment={environment}
      className="flex items-start justify-center gap-2 border-b-2 border-info bg-info-soft px-4 py-2 text-sm font-bold text-ink"
    >
      <FlaskConical size={18} aria-hidden className="mt-px shrink-0 text-info" />
      {ru.environment[environment]}
    </aside>
  );
}
