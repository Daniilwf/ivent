import { Sparkles, X } from 'lucide-react';
import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { ru } from '../i18n/ru';
import { Button, IconButton } from '../ui/Button';
import { BottomSheet } from '../ui/Dialogs';
import changelog from '../whatsNew/changelog.json';

const t = ru.whatsNew;

/** A release in «Что нового»: its tag, its day and the lines for the players (scripts/release-notes.mjs) */
export type Release = { version: string; date: string; items: string[] };

/** The version this browser last saw; kept per viewer, may be missing or refused (a private window) */
export const SEEN_KEY = 'seen-version';

function seen(): string | null {
  try {
    return localStorage.getItem(SEEN_KEY);
  } catch {
    return null;
  }
}

function remember(version: string) {
  try {
    localStorage.setItem(SEEN_KEY, version);
  } catch {
    // Without storage the banner shows again next time: nothing is lost
  }
}

/** The list of releases, newest first */
export function ReleaseList({ releases }: { releases: Release[] }) {
  if (releases.length === 0) return <p className="text-ink-soft">{t.empty}</p>;
  return (
    <ol className="grid gap-5" data-testid="whats-new-list">
      {releases.map((release) => (
        <li key={release.version} className="grid gap-2">
          <h3 className="font-display font-heavy">{t.release(release.version, release.date)}</h3>
          {release.items.length > 0 ? (
            <ul className="grid list-disc gap-1 pl-5">
              {release.items.map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          ) : (
            <p className="text-sm text-ink-soft">{t.quiet}</p>
          )}
        </li>
      ))}
    </ol>
  );
}

/**
 * After an update (J4, SPEC «Версии»): a quiet banner under the page's top — «Сайт обновился» with «Что нового» — once
 * per new version. The first visit only remembers the version: there is nothing new to someone who just came.
 */
export function UpdateBanner({ releases = changelog }: { releases?: Release[] }) {
  const [version, setVersion] = useState<string | null>(null);

  useEffect(() => {
    const page = { alive: true };
    void (async () => {
      try {
        const { data } = await api.GET('/api/status');
        if (!page.alive || !data) return;
        const before = seen();
        if (before === null) remember(data.version);
        else if (before !== data.version) setVersion(data.version);
      } catch {
        // No answer, no banner: the page says what failed where it matters
      }
    })();
    return () => {
      page.alive = false;
    };
  }, []);

  if (version === null) return null;
  const close = () => {
    remember(version);
    setVersion(null);
  };
  return (
    <div
      role="status"
      data-testid="update-banner"
      className="flex flex-wrap items-center justify-center gap-x-3 gap-y-1 border-b-2 border-muted bg-card px-4 py-1 text-sm font-medium"
    >
      <Sparkles size={18} aria-hidden className="shrink-0 text-action" />
      <span>{t.banner}</span>
      <BottomSheet
        title={t.title}
        trigger={
          <Button variant="link" data-testid="whats-new-open">
            {t.open}
          </Button>
        }
      >
        <ReleaseList releases={releases} />
      </BottomSheet>
      <IconButton label={t.close} data-testid="update-banner-close" onClick={close}>
        <X size={18} />
      </IconButton>
    </div>
  );
}
