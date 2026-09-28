import {
  Bug,
  CalendarCog,
  ClipboardCheck,
  FileJson,
  FlaskConical,
  History,
  Library,
  Menu as MenuIcon,
  ServerCrash,
  Sparkles,
  UserCog,
  Users,
  Wrench,
} from 'lucide-react';
import { useCallback, useRef, useState, type ReactNode } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { NotFoundPage } from '../app/NotFound';
import { siteEnvironment } from '../app/siteStatus';
import { navigate, paths, usePageHeading } from '../app/router';
import { ru } from '../i18n/ru';
import { Badge } from '../ui/Marks';
import { Button } from '../ui/Button';
import { BottomSheet } from '../ui/Dialogs';
import { AsyncState } from '../ui/AsyncState';
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
import { TestToolsSection } from './TestToolsSection';
import { SectionHead } from './common';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';

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
  // Only where the test endpoints exist (Development and Test, H9): the live site never lists it nor opens it
  { id: 'test', icon: <FlaskConical size={20} aria-hidden />, season: false },
];

/** The section an address opens: /admin is the proof queue, the admin's most frequent job (App shows no page for an unknown one) */
function adminSection(name: string | null): AdminSectionId {
  return sections.find((s) => s.id === name)?.id ?? 'proofs';
}

const href = (id: AdminSectionId) => paths.admin(id === 'proofs' ? undefined : id);

// Season updates come in bursts while people play: the admin's reads are rate-limited, so they are refreshed at most
// this often
const refreshEveryMs = 10_000;

/** The admin's pages: a list of sections (a side column on a desktop, a sheet on a phone) and the open section */
export function AdminScreen({
  section: sectionName,
  currentSeasonId,
  user,
}: {
  /** The router's `admin` route's section */
  section: string | null;
  currentSeasonId: string | null;
  user: Schemas['CurrentUser'];
}) {
  const section = adminSection(sectionName);
  const desk = useDesk();
  const [seasonId, setSeasonId] = useState(currentSeasonId);
  const version = useSeasonVersion(seasonId, { throttleMs: refreshEveryMs });
  const [sheet, setSheet] = useState(false);
  // A section picked in the sheet takes the focus to its heading, not back to the «Разделы» button
  const picked = useRef(false);

  const proofs = useLoaded(
    useCallback(async () => {
      if (!seasonId) return { kind: 'ready' as const, value: [] };
      return answerOf(
        await api.GET('/api/admin/seasons/{seasonId}/proofs', { params: { path: { seasonId } } }),
      );
    }, [seasonId]),
    { version },
  );
  const waiting = proofs.kind === 'ready' ? proofs.value.length : null;

  // Which copy of the site this is: the test tools' section is there only where the test endpoints are
  const status = useLoaded(
    useCallback(async () => {
      const answer = await siteEnvironment();
      return answer ? { kind: 'ready' as const, value: answer } : { kind: 'failed' as const };
    }, []),
  );
  const testTools = status.kind === 'ready' && status.value.testTools;
  const shown = sections.filter((s) => s.id !== 'test' || testTools);

  // The admin's pages opened from the menu, and a new section, take the focus to the heading, so a screen reader and
  // the keyboard start there
  const heading = usePageHeading(true, section);

  const links = () => (
    <ul className="grid gap-1">
      {shown.map((s) => {
        return (
          <li key={s.id}>
            <Link
              to={href(s.id)}
              data-testid={`admin-nav-${s.id}`}
              aria-current={s.id === section ? 'page' : undefined}
              onClick={() => {
                picked.current = true;
                setSheet(false);
              }}
              className={cx(
                'flex min-h-11 items-center gap-3 rounded-md px-3 font-medium is-hover:bg-muted',
                s.id === section ? 'bg-muted font-bold' : 'text-ink',
              )}
            >
              {s.icon}
              <span className="mr-auto">{t.sections[s.id]}</span>
              {s.id === 'proofs' && waiting ? (
                <Badge tone="ink" label={t.waiting(waiting)}>
                  {waiting}
                </Badge>
              ) : null}
            </Link>
          </li>
        );
      })}
    </ul>
  );

  const needsSeason = sections.find((s) => s.id === section)?.season ?? false;

  // The test tools' address on a copy without them is an address the site does not have; until the site answered,
  // nothing is said about the section at all
  if (section === 'test' && !testTools)
    return status.kind === 'ready' ? (
      <NotFoundPage />
    ) : (
      <main className="mx-auto grid max-w-110 px-4 py-10">
        <AsyncState loaded={status} rows={2} errorTitle={t.loadErrorTitle} level={1}>
          {() => null}
        </AsyncState>
      </main>
    );

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
                    {waiting ? <Badge tone="ink">{waiting}</Badge> : null}
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
      case 'test':
        return <TestToolsSection seasonId={seasonId} />;
    }
  }
}
