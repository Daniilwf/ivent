import { Gamepad2, Trophy } from 'lucide-react';
import { useRef, type ReactNode, type Ref } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths, usePageHeading, useRestoredScroll } from '../app/router';
import { answerOf, useLoaded } from '../app/useLoaded';
import { userToken } from '../design/players';
import { ru } from '../i18n/ru';
import { PageHeader } from '../ui/PageHeader';
import { AsyncState } from '../ui/AsyncState';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { EmptyState } from '../ui/States';
import { Sticker } from '../ui/Sticker';

const t = ru.hallOfFame;
const winnersTitleId = 'hall-of-fame-winners-title';
const recordTitleId = 'hall-of-fame-record-title';
const fastestTitleId = 'hall-of-fame-fastest-title';
const largestTitleId = 'hall-of-fame-largest-title';

const loadWinners = async () => answerOf(await api.GET('/api/hall-of-fame'));
const loadRecords = async () => answerOf(await api.GET('/api/hall-of-fame/records'));

export function HallOfFameScreen({ onSignedOut }: { onSignedOut: () => void }) {
  const winners = useLoaded(loadWinners, { onSignedOut });
  const records = useLoaded(loadRecords, { onSignedOut });
  const heading = usePageHeading();
  const recordHeading = useRef<HTMLHeadingElement>(null);
  // Tabs opened before a server rollback may still receive the previous records response.
  const availableRecords: Partial<Schemas['HallOfFameRecordsView']> =
    records.kind === 'ready' ? records.value : {};
  const fastest = availableRecords.fastestFinishes ?? [];
  const largest = availableRecords.largestRolls ?? [];
  const hasLongest = Boolean(availableRecords.longestGames?.length);
  const hasFastest = fastest.length > 0;
  const hasLargest = largest.length > 0;
  const recordTitles = [
    hasLongest && recordTitleId,
    hasFastest && fastestTitleId,
    hasLargest && largestTitleId,
  ].filter(Boolean);
  const recordTarget = recordTitles[0];
  const noResults =
    winners.kind === 'ready' &&
    winners.value.length === 0 &&
    records.kind === 'ready' &&
    !recordTarget;
  useRestoredScroll(winners.kind !== 'loading' && records.kind !== 'loading');

  return (
    <main className="mx-auto grid max-w-280 gap-4 px-4 pt-4 pb-10 desk:gap-6 desk:px-8 desk:pt-8">
      <header className="grid justify-items-start gap-2">
        <PageHeader ref={heading} tabIndex={-1} className="outline-none">
          {ru.nav.hallOfFame}
        </PageHeader>
        {recordTarget ? (
          <a
            href={`#${recordTarget}`}
            className={cx(standaloneLink, 'font-bold text-action-strong desk:hidden')}
            onClick={(event) => {
              const target = recordHeading.current;
              if (!target || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey)
                return;
              // A native hash change would run the router's scroll restoration.
              event.preventDefault();
              target.focus({ preventScroll: true });
              target.scrollIntoView({ block: 'start' });
            }}
          >
            {recordTitles.length > 1 ? t.toRecords : t.toRecord}
          </a>
        ) : null}
      </header>
      {noResults ? (
        <EmptyState
          icon={<Trophy size={28} aria-hidden />}
          title={t.emptyTitle}
          text={t.emptyText}
          level={2}
        />
      ) : (
        <div className="grid min-w-0 items-start gap-4 desk:grid-cols-[minmax(0,3fr)_minmax(0,2fr)] desk:gap-5">
          <section className="grid min-w-0 gap-3" aria-labelledby={winnersTitleId}>
            <h2 id={winnersTitleId} className="font-display text-lg font-heavy">
              {t.winners}
            </h2>
            <AsyncState loaded={winners} rows={2} errorTitle={t.winnersError}>
              {(seasons) => <WinningSeasons seasons={seasons} />}
            </AsyncState>
          </section>
          <div className="grid min-w-0 gap-4 desk:gap-5">
            {hasLongest || !recordTarget ? (
              <RecordPanel title={t.record} titleId={recordTitleId} heading={recordHeading}>
                <AsyncState loaded={records} rows={1} errorTitle={t.recordError}>
                  {({ longestGames }) => <LongestGames games={longestGames} />}
                </AsyncState>
              </RecordPanel>
            ) : null}
            {hasFastest ? (
              <RecordPanel
                title={t.fastestFinish}
                titleId={fastestTitleId}
                heading={recordTarget === fastestTitleId ? recordHeading : null}
              >
                <FastestFinishes finishes={fastest} />
              </RecordPanel>
            ) : null}
            {hasLargest ? (
              <RecordPanel
                title={t.largestRoll}
                titleId={largestTitleId}
                heading={recordTarget === largestTitleId ? recordHeading : null}
              >
                <LargestRolls rolls={largest} />
              </RecordPanel>
            ) : null}
          </div>
        </div>
      )}
    </main>
  );
}

function RecordPanel({
  title,
  titleId,
  heading,
  children,
}: {
  title: string;
  titleId: string;
  heading?: Ref<HTMLHeadingElement>;
  children: ReactNode;
}) {
  return (
    <section
      aria-labelledby={titleId}
      className="grid min-w-0 gap-3 rounded-lg border-2 border-muted bg-info-soft p-4 desk:p-5"
    >
      <h2
        id={titleId}
        ref={heading}
        tabIndex={-1}
        className="scroll-mt-44 rounded-sm font-display text-lg font-heavy is-focus:focus-ring"
      >
        {title}
      </h2>
      {children}
    </section>
  );
}

function WinningSeasons({ seasons }: { seasons: Schemas['HallOfFameSeasonView'][] }) {
  if (seasons.length === 0)
    return (
      <EmptyState
        icon={<Trophy size={28} aria-hidden />}
        title={t.noWinnersTitle}
        text={t.noWinnersText}
      />
    );

  return (
    <ol className="divide-y-2 divide-muted rounded-lg border-2 border-muted bg-card px-4">
      {seasons.map(({ seasonId, seasonName, winners }) => (
        <li
          key={seasonId}
          className="grid min-w-0 items-start gap-2 py-3 desk:grid-cols-2 desk:gap-4"
        >
          <div className="grid gap-1 wrap-anywhere desk:pt-2">
            <h3 className="font-bold">
              <Link
                to={paths.season(seasonId)}
                className={cx(standaloneLink, 'max-w-full min-w-11 text-action-strong')}
              >
                {seasonName}
              </Link>
            </h3>
            {winners.length > 1 ? <p className="text-sm text-ink-soft">{t.tiedFirst}</p> : null}
          </div>
          <ul className="grid min-w-0">
            {winners.map(({ playerId, name, points, profileUserId }) => (
              <li
                key={playerId}
                className="grid min-h-11 grid-cols-[minmax(0,1fr)_auto] items-center gap-2"
              >
                <div className="flex min-w-0 items-center gap-2">
                  <Sticker
                    player={{ name, token: userToken(profileUserId ?? playerId) }}
                    size={28}
                  />
                  {profileUserId ? (
                    <Link
                      to={paths.profile(profileUserId)}
                      className={cx(
                        standaloneLink,
                        'min-w-11 font-bold wrap-anywhere text-action-strong',
                      )}
                    >
                      {name}
                    </Link>
                  ) : (
                    <span className="min-w-0 font-bold wrap-anywhere">{name}</span>
                  )}
                </div>
                <span className="text-sm tabular-nums">{ru.profile.points(points)}</span>
              </li>
            ))}
          </ul>
        </li>
      ))}
    </ol>
  );
}

function FastestFinishes({ finishes }: { finishes: Schemas['HallOfFameFinishView'][] }) {
  const first = finishes[0];
  if (!first) return null;

  return (
    <>
      <p className="font-display text-2xl font-heavy wrap-anywhere tabular-nums desk:text-3xl">
        {t.finishTime(first.elapsedSeconds)}
      </p>
      <RecordHolders holders={finishes} />
    </>
  );
}

function LargestRolls({ rolls }: { rolls: Schemas['HallOfFameRollView'][] }) {
  const first = rolls[0];
  if (!first) return null;

  return (
    <>
      <p className="font-display text-2xl font-heavy wrap-anywhere tabular-nums desk:text-3xl">
        {first.sum.toLocaleString('ru-RU')}
      </p>
      <RecordHolders holders={rolls} />
    </>
  );
}

function RecordHolders({
  holders,
}: {
  holders: (Schemas['HallOfFameFinishView'] | Schemas['HallOfFameRollView'])[];
}) {
  return (
    <ul className="grid gap-2">
      {holders.map((holder) => {
        const { seasonId, seasonName, playerId, name, profileUserId } = holder;
        const key = 'runId' in holder ? holder.runId : `${seasonId}:${playerId}`;
        return (
          <li key={key} className="min-w-0 wrap-anywhere">
            {profileUserId ? (
              <Link
                to={paths.profile(profileUserId)}
                className={cx(standaloneLink, 'min-w-11 font-bold text-action-strong')}
              >
                {name}
              </Link>
            ) : (
              <p className="font-bold">{name}</p>
            )}
            <p className="text-sm text-ink-soft">{seasonName}</p>
          </li>
        );
      })}
    </ul>
  );
}

function LongestGames({ games }: { games: Schemas['HallOfFameGameView'][] }) {
  const first = games[0];
  if (!first)
    return (
      <EmptyState
        icon={<Gamepad2 size={28} aria-hidden />}
        title={t.noRecordTitle}
        text={t.noRecordText}
      />
    );

  return (
    <>
      <p className="font-display text-2xl font-heavy wrap-anywhere tabular-nums desk:text-3xl">
        {ru.hours.value(first.hours)}
      </p>
      <ul className="grid gap-2">
        {games.map(({ runId, gameId, title, seasonName }) => (
          <li key={runId} className="min-w-0 wrap-anywhere">
            <Link
              to={paths.game(gameId)}
              className={cx(standaloneLink, 'min-w-11 font-bold text-action-strong')}
            >
              {title}
            </Link>
            <p className="text-sm text-ink-soft">{seasonName}</p>
          </li>
        ))}
      </ul>
    </>
  );
}
