import { useCallback, useRef } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths } from '../app/router';
import { answerOf, useLoaded } from '../app/useLoaded';
import { usePoolVersion, useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { NominationCandidate } from '../polls/NominationCandidate';
import { AsyncState } from '../ui/AsyncState';
import { Button } from '../ui/Button';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';

const t = ru.nominationResults;
const refreshEvents = new Set([
  'season-nominations-recorded',
  'season-status-changed',
  'gallery-moment-hidden',
  'gallery-proof-consent-withdrawn',
  'proof-submitted',
  'proof-rejected',
  'run-reviewed',
  'command-undone',
]);

type Props = { seasonId: string; onSignedOut: () => void };

export function NominationResults(props: Props) {
  const seasonVersion = useSeasonVersion(props.seasonId, {
    refreshOnJoin: true,
    relevant: ({ types }) => types.some((type) => refreshEvents.has(type)),
  });
  const poolVersion = usePoolVersion();
  return <ResultPanel key={props.seasonId} {...props} version={seasonVersion + poolVersion} />;
}

function ResultPanel({ seasonId, onSignedOut, version }: Props & { version: number }) {
  const load = useCallback(
    async () =>
      answerOf(
        await api.GET('/api/seasons/{seasonId}/nominations', { params: { path: { seasonId } } }),
      ),
    [seasonId],
  );
  const state = useLoaded(load, { onSignedOut, version });
  if (state.kind === 'notFound') return null;

  return (
    <Panel title={t.title} className="min-w-0 desk:col-start-1 desk:w-96">
      <AsyncState loaded={state} errorTitle={t.loadFailed} rows={2}>
        {({ automatic, voted }, retry) =>
          automatic.length || voted.length ? (
            <>
              <ul className="grid min-w-0 divide-y-2 divide-muted">
                {automatic.map(({ nomination, value, winners }) => (
                  <li key={nomination} className="grid min-w-0 gap-2 py-3 first:pt-0">
                    <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
                      <h3 className="font-bold">{t.automaticTitles[nomination]}</h3>
                      <span className="text-sm font-bold tabular-nums">
                        {metric(nomination, value)}
                      </span>
                    </div>
                    <ul className="grid gap-1">
                      {winners.map(({ playerId, profileUserId, name }) => (
                        <li key={playerId} className="min-w-0 wrap-anywhere">
                          {profileUserId ? (
                            <Link
                              to={paths.profile(profileUserId)}
                              className={cx(standaloneLink, 'font-medium text-action-strong')}
                            >
                              {name}
                            </Link>
                          ) : (
                            name
                          )}
                        </li>
                      ))}
                    </ul>
                  </li>
                ))}
                {voted.map((result) => (
                  <VotedResult
                    key={result.pollId}
                    result={result}
                    showMaterial={state.refresh.kind === 'ready'}
                  />
                ))}
              </ul>
              {voted.some(({ hasWinner }) => hasWinner) ? (
                state.refresh.kind === 'loading' ? (
                  <p role="status" className="text-sm text-ink-soft">
                    {t.refreshingMaterials}
                  </p>
                ) : state.refresh.kind === 'failed' ? (
                  <div className="grid gap-2">
                    <Notice tone="danger">
                      {state.refresh.tooOften ? ru.shell.tooOftenText : t.refreshFailed}
                    </Notice>
                    <Button className="justify-self-start" onClick={retry}>
                      {ru.ui.retry}
                    </Button>
                  </div>
                ) : null
              ) : null}
            </>
          ) : (
            <p className="text-sm text-ink-soft">{t.empty}</p>
          )
        }
      </AsyncState>
    </Panel>
  );
}

function VotedResult({
  result: { nomination, hasWinner, winner },
  showMaterial,
}: {
  result: Schemas['VotedNominationView'];
  showMaterial: boolean;
}) {
  const heading = useRef<HTMLHeadingElement>(null);
  const preserveFocus = useCallback((node: HTMLDivElement | null) => {
    if (!node) return;
    return () => {
      if (node.contains(node.ownerDocument.activeElement)) {
        heading.current?.focus({ preventScroll: true });
      }
    };
  }, []);
  return (
    <li className="grid min-w-0 gap-2 py-3 first:pt-0">
      <h3 ref={heading} tabIndex={-1} className="rounded-sm font-bold is-focus:focus-ring">
        {ru.nomination.votedTitles[nomination]}
      </h3>
      {winner && showMaterial ? (
        <NominationCandidate candidate={winner} nomination={nomination} ref={preserveFocus} />
      ) : !hasWinner || showMaterial ? (
        <p className="text-sm text-ink-soft">{hasWinner ? t.hiddenWinner : t.noWinner}</p>
      ) : null}
    </li>
  );
}

function metric(nomination: Schemas['AutoNomination'], value: number) {
  if (nomination === 'mostHours') return ru.hours.value(value);
  return nomination === 'unluckiestDice' ? t.luck(value) : t.count(value);
}
