import { ChartNoAxesColumn, Download } from 'lucide-react';
import { useCallback, useId, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { standaloneLink } from '../ui/buttonStyles';
import { EmptyState } from '../ui/States';
import { recapCard, type RecapCard, type RecapMetric } from './recapCard';

const t = ru.seasonRecap;
const refreshEvents = new Set([
  'ruleset-changed',
  'command-undone',
  'run-completed',
  'completion-rolled',
  'run-hours-corrected',
  'run-difficulty-changed',
  'run-condition-checked',
  'run-dice-modified',
  'run-dice-rerolled',
  'run-reviewed',
  'proof-rejected',
  'coop-reward-split',
  'hostile-received',
]);

export function SeasonRecap({
  seasonId,
  user,
  onSignedOut,
}: {
  seasonId: string;
  user: Pick<Schemas['CurrentUser'], 'id' | 'name'>;
  onSignedOut: () => void;
}) {
  const load = useCallback(async () => {
    return answerOf(
      await api.GET('/api/seasons/{seasonId}/my-statistics', {
        params: { path: { seasonId } },
      }),
    );
  }, [seasonId]);
  const version = useSeasonVersion(seasonId, {
    relevant: (update) => update.types.some((type) => refreshEvents.has(type)),
    throttleMs: 2000,
    refreshOnJoin: true,
  });
  const state = useLoaded(load, { onSignedOut, version });
  const heading = usePageHeading(state.kind === 'ready');
  const [exportState, setExportState] = useState<'idle' | 'saving' | 'failed'>('idle');

  async function save(card: RecapCard) {
    setExportState('saving');
    try {
      const { downloadRecap } = await import('./recapPng');
      await downloadRecap(card);
      setExportState('idle');
    } catch {
      setExportState('failed');
    }
  }

  return (
    <main className="mx-auto max-w-256 px-4 pt-4 pb-10 desk:px-8 desk:pt-12">
      <AsyncState
        loaded={state}
        errorTitle={t.loadError}
        level={1}
        rows={2}
        notFound={
          <EmptyState
            icon={<ChartNoAxesColumn size={28} aria-hidden />}
            level={1}
            title={t.unavailable}
            text={t.unavailableText}
            action={
              <Link to={paths.profile(user.id)} className={standaloneLink}>
                {t.toProfile}
              </Link>
            }
          />
        }
      >
        {(stats) => {
          const card = recapCard(stats, user.name);
          return (
            <div className="grid items-center gap-6 desk:grid-cols-2 desk:gap-12">
              <header className="grid min-w-0 gap-3 desk:justify-items-start">
                <h1
                  ref={heading}
                  tabIndex={-1}
                  className="font-display text-2xl font-heavy outline-none desk:text-3xl"
                >
                  {t.title}
                </h1>
                <p className="text-sm text-ink-soft">{t.preview}</p>
                <Button
                  variant="main"
                  className="mt-1"
                  icon={<Download size={20} aria-hidden />}
                  loading={exportState === 'saving'}
                  onClick={() => {
                    void save(card);
                  }}
                >
                  {t.download}
                </Button>
                {exportState === 'failed' ? (
                  <p className="text-sm text-danger" role="alert">
                    {t.downloadError}
                  </p>
                ) : null}
              </header>
              <RecapPreview card={card} />
            </div>
          );
        }}
      </AsyncState>
    </main>
  );
}

export function RecapPreview({ card }: { card: RecapCard }) {
  const playerHeading = useId();
  return (
    <article
      className="grid min-w-0 gap-4 rounded-lg border-2 border-ink bg-card p-4 shadow-lift desk:gap-5 desk:p-8"
      aria-labelledby={playerHeading}
    >
      <header className="grid gap-1 wrap-anywhere">
        <p className="text-sm text-ink-soft">{card.season}</p>
        <h2 id={playerHeading} className="font-display text-xl font-heavy">
          {card.player}
        </h2>
      </header>
      <dl className="flex flex-wrap gap-4 rounded-md bg-me p-4 text-on-color desk:p-5">
        {[card.completed, card.hours].map(({ label, value }) => (
          <div
            key={label}
            className="flex max-w-full shrink-0 grow flex-col-reverse justify-end gap-1"
          >
            <dt className="font-medium">{label}</dt>
            <dd className="font-display text-2xl font-heavy wrap-anywhere tabular-nums">{value}</dd>
          </div>
        ))}
      </dl>
      <div className="grid grid-cols-2 divide-x divide-muted">
        <section className="grid min-w-0 content-start gap-2 pr-2 desk:pr-4">
          <h3 className="font-display text-sm font-heavy">{t.ratings}</h3>
          {card.ratings.length ? (
            <MetricPair metrics={card.ratings} />
          ) : (
            <p className="text-sm text-ink-soft">{t.noRatings}</p>
          )}
        </section>
        <section className="grid min-w-0 content-start gap-2 pl-2 desk:pl-4">
          <h3 className="font-display text-sm font-heavy">{t.attacks}</h3>
          <MetricPair metrics={card.attacks} />
        </section>
      </div>
      {card.rolls !== null && (
        <section className="grid min-w-0 gap-2 border-t border-muted pt-4 desk:pt-5">
          <h3 className="font-display text-sm font-heavy">{t.rolls}</h3>
          {card.rolls.length ? (
            <MetricPair metrics={card.rolls} />
          ) : (
            <p className="text-sm text-ink-soft">{t.noRolls}</p>
          )}
        </section>
      )}
    </article>
  );
}

function MetricPair({ metrics }: { metrics: RecapMetric[] }) {
  return (
    <dl className="grid grid-cols-2 gap-x-2 desk:gap-x-3">
      {metrics.map(({ label, value, unit, detail }) => (
        <div key={label} className="flex min-w-0 flex-col-reverse gap-1">
          <dt className="text-xs wrap-anywhere text-ink-soft desk:text-sm">{label}</dt>
          <dd className="flex flex-wrap items-baseline gap-x-1">
            <span className="font-display text-lg font-heavy wrap-anywhere tabular-nums">
              {value}
            </span>
            {unit ? <span className="text-xs text-ink-soft">{unit}</span> : null}
            {detail ? (
              <span className="basis-full text-xs wrap-anywhere text-ink-soft">{detail}</span>
            ) : null}
          </dd>
        </div>
      ))}
    </dl>
  );
}
