import { lazy, Suspense } from 'react';
import { ru } from '../i18n/ru';

// Loaded apart from the site: the players never download the styleguide
const Styleguide = lazy(async () => ({ default: (await import('./Styleguide')).Styleguide }));

export function StyleguidePage() {
  return (
    <Suspense fallback={<p className="p-4">{ru.ui.loading}</p>}>
      <Styleguide />
    </Suspense>
  );
}
