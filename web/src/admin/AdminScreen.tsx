import {
  Bug,
  CalendarCog,
  ClipboardCheck,
  FileJson,
  History,
  Library,
  Menu as MenuIcon,
  ServerCrash,
  Sparkles,
  UserCog,
  Users,
  Wrench,
} from 'lucide-react';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { api, type Schemas } from '../api/client';
import { watchSeason } from '../api/realtime';
import { Link } from '../app/Link';
import { navigate, paths, usePageHeading } from '../app/router';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { BottomSheet } from '../ui/Dialogs';
import { EmptyState } from '../ui/States';
import { cx } from '../ui/cx';
import { useDesk } from '../ui/useDesk';
import { AccountsSection } from './AccountsSection';
import { BugsSection } from './BugsSection';
import { EffectsSection } from './EffectsSection';
import { ErrorsSection } from './ErrorsSection';
import { LogSection } from './LogSection';
import { PlayersSection } from './PlayersSection';
import { PoolSection } from './PoolSection';
import { ProofQueue } from './ProofQueue';
import { RulesSection } from './RulesSection';
import { SeasonSection } from './SeasonSection';
import { SiteSection } from './SiteSection';
import { SectionHead } from './common';
import { useLoad } from './useLoad';

const t = ru.admin;

export type AdminSectionId = keyof typeof t.sections;

const sections: { id: AdminSectionId; icon: ReactNode; season: boolean }[] = [
  { id: 'proofs', icon: <ClipboardCheck size={20} aria-hidden />, season: true },
  { id: 'players', icon: <Users size={20} aria-hidden />, season: true },
  { id: 'log', icon: <History size={20} aria-hidden />, season: true },
  { id: 'effects', icon: <Sparkles size={20} aria-hidden />, season: true },
  { id: 'rules', icon: <FileJson size={20} aria-hidden />, season: true },
  { id: 'season', icon: <CalendarCog size={20} aria-hidden />, season: false },
  { id: 'pool', icon: <Library size={20} aria-hidden />, season: false },
  { id: 'accounts', icon: <UserCog size={20} aria-hidden />, season: false },
  { id: 'bugs', icon: <Bug size={20} aria-hidden />, season: false },
  { id: 'errors', icon: <ServerCrash size={20} aria-hidden />, season: false },
  { id: 'site', icon: <Wrench size={20} aria-hidden />, season: false },
];

/** The section an address opens: /admin is the proof queue, the admin's most frequent job */
function adminSection(path: string): AdminSectionId {
  const name = path.replace(/^\/admin\/?/, '').split('/')[0] ?? '';
  return sections.find((s) => s.id === name)?.id ?? 'proofs';
}

const href = (id: AdminSectionId) => paths.admin(id === 'proofs' ? undefined : id);

// Season updates come in bursts while people play: the admin's reads are rate-limited, so they are refreshed at most
// this often
const refreshEveryMs = 10_000;

/** The season's updates, thinned out: a number that grows at most once per refreshEveryMs */
function useSeasonVersion(seasonId: string | null) {
  const [version, setVersion] = useState(0);
  const waiting = useRef<ReturnType<typeof setTimeout> | null>(null);
  const last = useRef(0);
  useEffect(() => {
    if (!seasonId) return;
    let first = true;
    const stop = watchSeason(seasonId, () => {
      // The first answer is the join itself: the data was just loaded
      if (first) {
        first = false;
        return;
      }
      if (waiting.current) return;
      const wait = Math.max(0, last.current + refreshEveryMs - Date.now());
      waiting.current = setTimeout(() => {
        waiting.current = null;
        last.current = Date.now();
        setVersion((v) => v + 1);
      }, wait);
    });
    return () => {
      stop();
      if (waiting.current) clearTimeout(waiting.current);
      waiting.current = null;
    };
  }, [seasonId]);
  return version;
}

/** The admin's pages: a list of sections (a side column on a desktop, a sheet on a phone) and the open section */
export function AdminScreen({
  path,
  currentSeasonId,
  user,
}: {
  path: string;
  currentSeasonId: string | null;
  user: Schemas['CurrentUser'];
}) {
  const section = adminSection(path);
  const desk = useDesk();
  const [seasonId, setSeasonId] = useState(currentSeasonId);
  const version = useSeasonVersion(seasonId);
  const [sheet, setSheet] = useState(false);
  // A section picked in the sheet takes the focus to its heading, not back to the «Разделы» button
  const picked = useRef(false);

  const proofs = useLoad(
    async () => {
      if (!seasonId) return [];
      const { data } = await api.GET('/api/admin/seasons/{seasonId}/proofs', {
        params: { path: { seasonId } },
      });
      return data;
    },
    [seasonId],
    version,
  );
  const waiting = proofs.status === 'ready' ? proofs.data.length : null;

  // The admin's pages opened from the menu, and a new section, take the focus to the heading, so a screen reader and
  // the keyboard start there
  const heading = usePageHeading();
  const opened = useRef(false);
  useEffect(() => {
    if (opened.current) heading.current?.focus();
    opened.current = true;
  }, [section, heading]);

  const links = () => (
    <ul className="grid gap-1">
      {sections.map((s) => {
        return (
          <li key={s.id}>
            <Link
              to={href(s.id)}
              data-testid={`admin-nav-${s.id}`}
              aria-current={s.id === section ? 'page' : undefined}
              onClick={() => {
                picked.current = true;
                setSheet(false);
                requestAnimationFrame(() => {
                  heading.current?.focus();
                });
              }}
              className={cx(
                'flex min-h-11 items-center gap-3 rounded-md px-3 font-medium is-hover:bg-muted',
                s.id === section ? 'bg-muted font-bold' : 'text-ink',
              )}
            >
              {s.icon}
              <span className="mr-auto">{t.sections[s.id]}</span>
              {s.id === 'proofs' && waiting ? (
                <span
                  className="rounded-full bg-ink px-2 text-sm font-bold text-on-color tabular-nums"
                  aria-label={t.waiting(waiting)}
                >
                  {waiting}
                </span>
              ) : null}
            </Link>
          </li>
        );
      })}
    </ul>
  );

  const needsSeason = sections.find((s) => s.id === section)?.season ?? false;

  return (
    <main
      className="mx-auto grid max-w-300 gap-6 px-4 py-6 desk:grid-cols-[auto_minmax(0,1fr)] desk:gap-8 desk:px-8"
      data-testid="admin"
    >
      {desk ? (
        <nav aria-label={t.nav} className="w-60 self-start desk:sticky desk:top-20">
          <p className="mb-2 px-3 font-display text-lg font-heavy">{t.title}</p>
          {links()}
        </nav>
      ) : null}
      <div className="grid min-w-0 content-start gap-6">
        <SectionHead
          id="admin-heading"
          headingRef={heading}
          title={t.sections[section]}
          lead={t.leads[section]}
          action={
            desk ? null : (
              <BottomSheet
                title={t.nav}
                open={sheet}
                onOpenChange={setSheet}
                returnFocus={() => {
                  const back = !picked.current;
                  picked.current = false;
                  return back;
                }}
                trigger={
                  <Button icon={<MenuIcon size={20} aria-hidden />} data-testid="admin-sections">
                    {t.sectionsButton}
                    {waiting ? (
                      <span className="rounded-full bg-ink px-2 text-sm text-on-color tabular-nums">
                        {waiting}
                      </span>
                    ) : null}
                  </Button>
                }
              >
                <nav aria-label={t.nav}>{links()}</nav>
              </BottomSheet>
            )
          }
        />
        {needsSeason && !seasonId ? (
          <EmptyState
            level={2}
            icon={<CalendarCog size={28} aria-hidden />}
            title={t.noSeasonTitle}
            text={t.noSeasonText}
            action={
              <Button
                onClick={() => {
                  navigate(href('season'));
                }}
              >
                {t.toSeason}
              </Button>
            }
          />
        ) : (
          content()
        )}
      </div>
    </main>
  );

  function content() {
    const id = seasonId ?? '';
    switch (section) {
      case 'proofs':
        return <ProofQueue seasonId={id} loaded={proofs} />;
      case 'players':
        return <PlayersSection seasonId={id} version={version} />;
      case 'log':
        return <LogSection seasonId={id} version={version} />;
      case 'effects':
        return <EffectsSection seasonId={id} version={version} />;
      case 'rules':
        return <RulesSection seasonId={id} version={version} />;
      case 'season':
        return (
          <SeasonSection
            seasonId={seasonId}
            version={version}
            onPick={(picked) => {
              setSeasonId(picked);
            }}
          />
        );
      case 'pool':
        return <PoolSection seasonId={seasonId} />;
      case 'accounts':
        return <AccountsSection me={user.id} />;
      case 'bugs':
        return <BugsSection />;
      case 'errors':
        return <ErrorsSection />;
      case 'site':
        return <SiteSection />;
    }
  }
}
