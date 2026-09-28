import { BookOpen, CalendarClock } from 'lucide-react';
import { useCallback, useEffect, type ReactNode } from 'react';
import { api, type Schemas } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { usePageHeading } from '../app/router';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded, type Answer } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { Chip } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { AsyncState } from '../ui/AsyncState';
import { cx } from '../ui/cx';
import { EmptyState } from '../ui/States';
import { fieldName, rulesPage, valueText } from './rulesText';

const t = ru.rules;

/** What the rules page shows can change: the rules, the deadline, or an undo of either */
const changesRules = (update: SeasonUpdate) =>
  update.types.some((type) =>
    ['ruleset-changed', 'season-deadline-set', 'command-undone'].includes(type),
  );

type SectionId = keyof typeof t.sections;
const order: SectionId[] = [
  'win',
  'roll',
  'completion',
  'reward',
  'drop',
  'finish',
  'deadline',
  'history',
];

/**
 * The season's rules (H7; SPEC «Правила на сайте»): every number from the ruleset in force, in words, and the history of
 * its changes — when, who, what was and what is. A change of the rules shows up without a reload.
 */
export function RulesScreen({
  seasonId,
  onSignedOut,
}: {
  seasonId: string | null;
  onSignedOut: () => void;
}) {
  const heading = usePageHeading();
  // Load now and again when the rules or the deadline change (or are undone), not on every roll. A failed refresh
  // keeps the rules on the screen; only the first load turns into the error
  const version = useSeasonVersion(seasonId, { relevant: changesRules });
  const loaded = useLoaded(
    useCallback(
      async (): Promise<Answer<Schemas['RulesView']>> =>
        seasonId
          ? answerOf(
              await api.GET('/api/seasons/{seasonId}/rules', { params: { path: { seasonId } } }),
            )
          : { kind: 'loading' },
      [seasonId],
    ),
    { onSignedOut, version },
  );

  const title = (
    <h1
      ref={heading}
      tabIndex={-1}
      className="font-display text-2xl font-heavy outline-none desk:text-3xl"
    >
      {t.title}
    </h1>
  );

  if (!seasonId)
    return (
      <main className="mx-auto grid max-w-110 gap-4 px-4 py-10" data-testid="rules-no-season">
        <EmptyState
          level={1}
          icon={<BookOpen size={28} aria-hidden />}
          title={t.noSeasonTitle}
          text={t.noSeasonText}
        />
      </main>
    );

  const frame = 'mx-auto grid max-w-300 gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-6';
  // The heading stays the same element from the skeleton to the rules: the focus given to it stays too
  const header = (
    <header className="grid justify-items-start gap-2 desk:col-span-2">
      {title}
      <p className="max-w-prose text-ink-soft">{t.lead}</p>
      {loaded.kind === 'ready' ? <Chip>{t.version(loaded.value.version)}</Chip> : null}
    </header>
  );

  // The page's frame and heading stay; the rules come in place of the skeleton (two columns on a desktop)
  return (
    <main
      className={cx(
        frame,
        loaded.kind === 'ready' &&
          'desk:grid-cols-[auto_minmax(0,1fr)] desk:items-start desk:gap-x-8',
      )}
    >
      {header}
      <AsyncState
        loaded={loaded}
        skeleton={
          <div className="grid max-w-200 gap-4" aria-busy="true" data-testid="rules-loading">
            <p className="sr-only">{ru.ui.loading}</p>
            {[0, 1, 2].map((i) => (
              <Skeleton key={i} className="h-40 w-full rounded-lg" />
            ))}
          </div>
        }
        errorTitle={t.loadErrorTitle}
      >
        {(rules) => <RulesContent rules={rules} />}
      </AsyncState>
    </main>
  );
}

/** The rules in force, section by section, with the contents beside them and the history at the end */
export function RulesContent({ rules }: { rules: Schemas['RulesView'] }) {
  const page = rulesPage(rules.ruleset);
  const deadline = rules.deadline ?? null;
  // A link to a section (D-207: «Что считается прохождением» from a run or the proof queue) came before the rules did:
  // the browser found nothing to scroll to, so the section is shown once it is here
  useEffect(() => {
    const target = decodeURIComponent(globalThis.location.hash.slice(1));
    if (!target.startsWith('rules-')) return;
    const section = document.getElementById(target);
    if (section && typeof section.scrollIntoView === 'function')
      section.scrollIntoView({ block: 'start' });
  }, []);
  return (
    <>
      <nav
        aria-label={t.contents}
        className="rounded-lg bg-card p-4 desk:sticky desk:top-24 desk:w-64"
      >
        <h2 className="mb-2 text-sm font-bold">{t.contents}</h2>
        <ul className="grid gap-1">
          {order.map((id) => (
            <li key={id}>
              <a
                href={`#rules-${id}`}
                className="inline-flex min-h-11 items-center rounded-md px-2 underline-offset-4 is-hover:underline is-focus:focus-ring"
              >
                {t.sections[id]}
              </a>
            </li>
          ))}
        </ul>
      </nav>

      <div className="grid min-w-0 gap-4">
        <RulesSection id="win">
          <Lines lines={page.win} />
        </RulesSection>
        <RulesSection id="roll">
          <Lines lines={page.roll} />
        </RulesSection>
        <RulesSection id="completion">
          <CompletionCriteria />
        </RulesSection>
        <RulesSection id="reward">
          <Lines lines={page.reward} />
          <table className="w-full border-collapse text-left" data-testid="rules-dice">
            <caption className="mb-2 text-left font-bold">{t.reward.byDifficulty}</caption>
            <thead>
              <tr className="border-b-2 border-muted text-sm text-ink-soft">
                <th scope="col" className="py-2 pr-3 font-medium">
                  {t.reward.difficulty}
                </th>
                <th scope="col" className="py-2 font-medium">
                  {t.reward.die}
                </th>
              </tr>
            </thead>
            <tbody>
              {page.dice.map((row) => (
                <tr key={row.difficulty} className="border-b-2 border-muted last:border-b-0">
                  <th scope="row" className="py-2 pr-3 font-regular">
                    {row.label}
                  </th>
                  <td className="py-2 font-display font-heavy tabular-nums">{row.die}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <Lines lines={page.rewardAfter} />
        </RulesSection>
        <RulesSection id="drop">
          <Lines lines={page.drop} />
        </RulesSection>
        <RulesSection id="finish">
          {page.bonuses.length > 0 ? (
            <>
              <p>{t.finish.bonus}</p>
              <ul className="grid gap-1" data-testid="rules-bonuses">
                {page.bonuses.map((row) => (
                  <li
                    key={row.place}
                    className="flex justify-between gap-3 border-b-2 border-muted py-2 last:border-b-0"
                  >
                    <span>{row.place}</span>
                    <span className="font-display font-heavy tabular-nums">{row.points}</span>
                  </li>
                ))}
              </ul>
            </>
          ) : null}
          <Lines lines={page.finish} />
        </RulesSection>
        <RulesSection id="deadline">
          <p className="flex items-center gap-2 font-bold" data-testid="rules-deadline">
            <CalendarClock size={20} aria-hidden className="shrink-0" />
            {deadline ? t.deadline.at(moscowTime(deadline)) : t.deadline.none}
          </p>
          <Lines lines={[t.deadline.after, t.deadline.results]} />
        </RulesSection>
        <RulesSection id="history">
          <RulesHistory history={rules.history} />
        </RulesSection>
      </div>
    </>
  );
}

function RulesSection({ id, children }: { id: SectionId; children: ReactNode }) {
  return (
    <section
      id={`rules-${id}`}
      aria-labelledby={`rules-${id}-title`}
      className="grid scroll-mt-28 gap-3 rounded-lg bg-card p-4 desk:p-6"
    >
      <h2 id={`rules-${id}-title`} className="font-display text-xl font-heavy">
        {t.sections[id]}
      </h2>
      {children}
    </section>
  );
}

/** «Что считается прохождением» (D-207, RGG 14): the first condition that fits, top to bottom; the same for every season */
export function CompletionCriteria() {
  const c = t.completion;
  return (
    <div className="grid max-w-prose gap-3" data-testid="rules-completion">
      <p>{c.lead}</p>
      <ol className="grid list-decimal gap-2 pl-5">
        {c.steps.map((step) => (
          <li key={step.title}>
            <strong>{step.title}</strong> {step.text}
          </li>
        ))}
      </ol>
      <p>
        {/* The text starts with a no-break space and its dash: the dash stays by the title */}
        <strong>{c.doubtTitle}</strong>
        {c.doubtText}
      </p>
      <p>
        <strong>{c.proofTitle}</strong> {c.proofText}
      </p>
    </div>
  );
}

function Lines({ lines }: { lines: string[] }) {
  return (
    <ul className="grid max-w-prose list-disc gap-2 pl-5">
      {lines.map((line) => (
        <li key={line}>{line}</li>
      ))}
    </ul>
  );
}

/** The versions of the rules, newest first: when, who, and each changed number as «было → стало» */
export function RulesHistory({ history }: { history: Schemas['RulesVersionView'][] }) {
  // A save that changed nothing is still a version: it stays in the list, marked so, and the number on the chip is there
  const changed = history.filter((v) => v.version > 1);
  const created = history.find((v) => v.version === 1);
  return (
    <div className="grid gap-4" data-testid="rules-history">
      {changed.length === 0 ? <p className="text-ink-soft">{t.history.empty}</p> : null}
      <ol className="grid gap-4">
        {changed.map((version) => (
          <li key={version.version} className="grid gap-2 border-l-3 border-muted pl-4">
            <h3 className="font-bold">
              {t.history.version(version.version)}
              <span className="block text-sm font-regular text-ink-soft">
                {version.authorName ? t.history.by(version.authorName) : t.history.byAdmin},{' '}
                {moscowTime(version.at)}
              </span>
            </h3>
            {version.changes.length === 0 ? (
              <p className="text-sm text-ink-soft">{t.history.unchanged}</p>
            ) : null}
            <ul className="grid gap-2">
              {version.changes.map((change) => (
                <li key={change.path} className="grid gap-1 rounded-md bg-page px-3 py-2">
                  <span className="font-medium wrap-anywhere">{fieldName(change.path)}</span>
                  <span className="flex flex-wrap gap-x-4 gap-y-1 text-sm tabular-nums">
                    <span>
                      <span className="text-ink-soft">{t.history.was}: </span>
                      <del className="wrap-anywhere">{valueText(change.before)}</del>
                    </span>
                    <span>
                      <span className="text-ink-soft">{t.history.now}: </span>
                      <ins className="font-bold no-underline wrap-anywhere">
                        {valueText(change.after)}
                      </ins>
                    </span>
                  </span>
                </li>
              ))}
            </ul>
          </li>
        ))}
        {created ? (
          <li className="grid gap-1 border-l-3 border-muted pl-4">
            <h3 className="font-bold">
              {t.history.version(created.version)}
              <span className="block text-sm font-regular text-ink-soft">
                {t.history.created}, {moscowTime(created.at)}
              </span>
            </h3>
          </li>
        ) : null}
      </ol>
    </div>
  );
}
