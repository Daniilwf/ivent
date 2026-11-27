import { ChartNoAxesColumn, Download } from 'lucide-react';
import { useCallback, useId, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { playerToken } from '../design/players';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { PageHeader } from '../ui/PageHeader';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
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
  'proof-approved',
  'proof-rejected',
  'coop-reward-split',
  'hostile-received',
  'points-changed',
  'player-finished',
  'player-finish-revoked',
  'player-frozen',
  'season-player-added',
  'season-result-recorded',
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
                <PageHeader ref={heading} tabIndex={-1} className="outline-none">
                  {t.title}
                </PageHeader>
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
        {card.standing && (
          <p className="flex flex-wrap items-center gap-x-2 gap-y-1 font-bold tabular-nums">
            {card.playerToken !== null && (
              <span
                className="h-1 w-8 shrink-0 rounded-full"
                style={{ background: playerToken(card.playerToken).fill }}
                data-testid="recap-player-accent"
                aria-hidden
              />
            )}
            <span>{card.standing.place}</span>
            <span className="text-ink-soft">{card.standing.points}</span>
          </p>
        )}
      </header>
      <MetricPair metrics={[card.completed, card.hours]} featured />
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
      <footer className="border-t border-muted pt-2 text-xs text-ink-soft">{ru.app.title}</footer>
    </article>
  );
}

function MetricPair({ metrics, featured = false }: { metrics: RecapMetric[]; featured?: boolean }) {
  return (
    <dl
      className={
        featured
          ? 'flex flex-wrap gap-4 rounded-md bg-me p-4 text-on-color desk:p-5'
          : 'grid grid-cols-2 gap-x-2 desk:gap-x-3'
      }
    >
      {metrics.map(({ label, value, unit, detail }) => (
        <div
          key={label}
          className={cx('grid content-start gap-1', featured ? 'max-w-full grow' : 'min-w-0')}
        >
          <dt
            className={
              featured ? 'font-medium' : 'text-xs wrap-anywhere text-ink-soft desk:text-sm'
            }
          >
            {label}
          </dt>
          <dd className="flex flex-wrap items-baseline gap-x-1">
            <span
              className={cx(
                'font-display font-heavy wrap-anywhere tabular-nums',
                featured ? 'text-2xl' : 'text-lg',
              )}
            >
              {value}
            </span>
            {unit ? (
              <>
                {' '}
                <span className={featured ? 'text-sm font-medium' : 'text-xs text-ink-soft'}>
                  {unit}
                </span>
              </>
            ) : null}
            {detail ? (
              <span className="basis-full text-xs wrap-anywhere text-ink-soft">{detail}</span>
            ) : null}
          </dd>
        </div>
      ))}
    </dl>
  );
}
