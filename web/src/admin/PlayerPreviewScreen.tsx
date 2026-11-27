import { ArrowLeft, Eye, ExternalLink } from 'lucide-react';
import { useCallback, useEffect, useRef } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { paths, usePageHeading } from '../app/router';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { useSeasonVersion } from '../app/useSeasonVersion';
import { useIsPast } from '../app/useIsPast';
import { Cover, RunCard } from '../board/GameCards';
import { Leaderboard } from '../board/Leaderboard';
import { MapLegend } from '../board/MapLegend';
import { MapView } from '../board/MapView';
import { seasonBoard } from '../board/seasonBoard';
import { branchOptions } from '../board/graphBoard';
import { cellsToFinish } from '../board/geometry';
import { GlobalEventBanner } from '../globalEvents/GlobalEventBanner';
import { ru } from '../i18n/ru';
import { GameFacts } from '../pool/GameFacts';
import { DrawnEventCard } from '../season/DrawnEventCard';
import { RunConditions } from '../season/RunConditions';
import { GameMarks } from '../season/GameMarks';
import { WaitingEvents } from '../season/WaitingEvents';
import { MyFinish } from '../season/MyFinish';
import { parseIrlLink } from '../season/irlLink';
import { seasonPicture } from '../season/seasonView';
import { PageHeader } from '../ui/PageHeader';
import { AsyncState } from '../ui/AsyncState';
import { buttonClass, standaloneLink } from '../ui/buttonStyles';
import { Summary } from '../ui/Disclosure';
import { Badge, Chip } from '../ui/Marks';
import { Notice } from '../ui/States';
import { Panel } from '../ui/Surface';

type Preview = Schemas['PlayerPreviewView'];
type Proof = Schemas['ProofView'];
type IrlProof = Schemas['IrlProofView'];
const t = ru.playerPreview;

/** A private read of one explicit target. No account, mode or API middleware is changed. */
export function PlayerPreviewScreen({
  seasonId,
  playerId,
  onSignedOut,
}: {
  seasonId: string;
  playerId: string;
  onSignedOut: () => void;
}) {
  const request = useRef<AbortController | null>(null);
  const version = useSeasonVersion(seasonId, { refreshOnJoin: true, throttleMs: 8_000 });
  const load = useCallback(async () => {
    request.current?.abort();
    const abort = new AbortController();
    request.current = abort;
    const answer = await api.GET('/api/admin/seasons/{seasonId}/players/{playerId}/preview', {
      params: { path: { seasonId, playerId } },
      headers: { 'X-GameEvent-Player-Preview': 'true' },
      signal: abort.signal,
    });
    // A permission change must clear private data rather than retain the last successful refresh.
    if (answer.response.status === 403) return { kind: 'notFound' } as const;
    if (
      answer.data &&
      (answer.data.playerId.toLowerCase() !== playerId.toLowerCase() ||
        answer.data.season.id.toLowerCase() !== seasonId.toLowerCase() ||
        answer.data.season.me?.playerId.toLowerCase() !== playerId.toLowerCase())
    )
      return { kind: 'notFound' } as const;
    return answerOf(answer);
  }, [seasonId, playerId]);
  const loaded = useLoaded(load, { version, onSignedOut });
  useEffect(() => () => request.current?.abort(), []);
  const heading = usePageHeading(true, `${seasonId}:${playerId}`);

  return (
    <main className="mx-auto grid max-w-300 gap-6 px-4 py-6 desk:px-8" data-testid="player-preview">
      <header className="grid gap-3">
        <PageHeader ref={heading} tabIndex={-1}>
          {t.title}
        </PageHeader>
        {loaded.kind === 'ready' ? (
          <p className="flex flex-wrap gap-2">
            <Chip>{ru.seasonStatus[loaded.value.season.status]}</Chip>
            {loaded.value.season.deadline ? (
              <Chip>{t.deadline(moscowTime(loaded.value.season.deadline))}</Chip>
            ) : null}
          </p>
        ) : null}
        <p className="max-w-prose text-sm text-ink-soft">{t.lead}</p>
      </header>
      <section
        aria-label={t.context}
        data-testid="player-preview-context"
        className="sticky top-0 z-20 grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-4 gap-y-2 rounded-lg border-2 border-info bg-info-soft p-3 desk:grid-cols-[auto_minmax(0,1fr)_auto]"
      >
        {loaded.kind === 'ready' ? (
          <p className="col-span-2 row-start-2 flex min-w-0 flex-wrap gap-x-4 gap-y-1 text-sm desk:col-span-1 desk:col-start-2 desk:row-start-1">
            <strong data-testid="preview-player-name" className="wrap-anywhere">
              {loaded.value.playerName}
            </strong>
            <span data-testid="preview-season-name" className="wrap-anywhere">
              {loaded.value.season.name}
            </span>
          </p>
        ) : null}
        <div className="col-start-1 row-start-1">
          <Badge tone="muted" icon={<Eye size={16} aria-hidden />}>
            {t.readOnly}
          </Badge>
        </div>
        <Link
          to={paths.adminPlayers(seasonId)}
          aria-label={t.exit}
          className={buttonClass(
            'quiet',
            'col-start-2 row-start-1 justify-self-end desk:col-start-3',
          )}
        >
          <ArrowLeft size={20} aria-hidden />
          {t.exitShort}
        </Link>
      </section>
      <AsyncState
        loaded={loaded}
        errorTitle={t.failed}
        notFound={
          <Panel title={t.unavailable}>
            <p>{t.unavailableText}</p>
          </Panel>
        }
      >
        {(value) => (
          <>
            {loaded.refresh.kind === 'failed' ? (
              <Notice tone="warning">{t.refreshFailed}</Notice>
            ) : null}
            <Snapshot value={value} />
          </>
        )}
      </AsyncState>
    </main>
  );
}

function Snapshot({ value }: { value: Preview }) {
  const { season, economy } = value;
  const deadlinePast = useIsPast(season.deadline);
  const me = season.me;
  const map = seasonBoard(season);
  const picture = seasonPicture(season, map);
  const branches =
    me?.choice?.kind === 'branch' ? branchOptions(me.choice, season, map.cellNumber) : null;
  const mine = picture.players.find((player) => player.id === value.playerId);
  const row = season.leaderboard.find((player) => player.playerId === value.playerId);
  const proofs = [me?.lastCompleted, ...(me?.checks ?? [])].filter(
    (run): run is Schemas['CompletedRunView'] => run != null,
  );
  const effects = me?.manualEffects ?? [];

  return (
    <div className="grid min-w-0 gap-6 desk:grid-cols-3" data-testid="player-preview-content">
      <Panel title={t.turn} className="min-w-0 desk:col-start-1">
        {me ? (
          <>
            <p className="flex flex-wrap gap-2">
              <Chip>{t.phase[me.phase]}</Chip>
              {me.coins != null ? <Chip>{ru.inventory.coins(me.coins)}</Chip> : null}
            </p>
            {me.finish ? (
              <MyFinish
                finish={me.finish}
                first={row?.isFirst === true}
                place={row?.place ?? null}
                rollsOpen={season.status === 'active' && !deadlinePast}
              />
            ) : null}
            {me.activeRun ? (
              <RunCard
                game={{
                  title: me.activeRun.game.title,
                  hours: me.activeRun.game.hours,
                  cover: me.activeRun.game.cover?.thumbnailUrl,
                  tags: [],
                }}
                left={row?.cellsToFinish ?? null}
                total={cellsToFinish(
                  picture.board,
                  picture.board.cells.find((cell) => cell.kind === 'start')?.id ?? 0,
                )}
                actions={null}
                level={3}
              />
            ) : null}
            {me.activeRun?.conditions?.length ? (
              <RunConditions conditions={me.activeRun.conditions} />
            ) : null}
            {me.offer ? (
              <section aria-label={t.offer}>
                <PreviewGame game={me.offer} />
                <RunConditions conditions={me.offer.conditions ?? null} />
              </section>
            ) : null}
            {me.choice ? (
              <section
                className="grid gap-2"
                aria-label={branches ? ru.map.branch.title : t.choice}
              >
                <h3 className="font-display font-heavy">{me.choice.name ?? t.choice}</h3>
                {me.choice.description ? (
                  <p className="whitespace-pre-wrap wrap-anywhere">{me.choice.description}</p>
                ) : null}
                {me.choice.prompt ? (
                  <p className="font-medium whitespace-pre-wrap wrap-anywhere">
                    {me.choice.prompt}
                  </p>
                ) : null}
                {branches ? (
                  <>
                    <p className="text-ink-soft">{ru.map.branch.lead(me.choice.steps ?? 1)}</p>
                    <ol className="grid gap-3">
                      {branches.map((option) => (
                        <li key={option.id} className="grid gap-2 rounded-md bg-muted p-3">
                          <h4 className="font-bold">{option.end}</h4>
                          {option.zone ? (
                            <p>
                              <Chip>{option.zone}</Chip>
                            </p>
                          ) : null}
                          {option.rules.map((rule) => (
                            <p key={rule} className="text-sm text-ink-soft">
                              {rule}
                            </p>
                          ))}
                          <p className="text-sm text-ink-soft">
                            {ru.map.branch.firstCell(option.cell)}
                          </p>
                        </li>
                      ))}
                    </ol>
                  </>
                ) : (
                  <ol className="grid gap-3">
                    {me.choice.options.map((option, index) => (
                      <li key={option.id}>
                        {option.game ? (
                          <PreviewGame game={option.game} />
                        ) : (
                          <p>
                            {option.label ??
                              (option.direction
                                ? ru.inventory[option.direction]
                                : t.option(index + 1))}
                          </p>
                        )}
                      </li>
                    ))}
                  </ol>
                )}
              </section>
            ) : null}
            {!me.offer && !me.activeRun && !me.choice ? (
              <p className="text-ink-soft">{t.noTurn}</p>
            ) : null}
            {me.nextReroll ? (
              <p className="text-sm text-ink-soft">
                {t.reroll}: {t.rerollPayment[me.nextReroll.payment]}
                {me.nextReroll.payment === 'coins'
                  ? `, ${ru.inventory.coins(me.nextReroll.coins)}`
                  : ''}
              </p>
            ) : null}
            {me.dropPenalty ? (
              <p className="text-sm text-ink-soft">
                {t.dropPenalty(me.dropPenalty.count, me.dropPenalty.sides)}
              </p>
            ) : null}
          </>
        ) : (
          <p className="text-ink-soft">{t.noTurn}</p>
        )}
      </Panel>
      <section
        className="grid min-w-0 content-start gap-2 desk:col-span-2 desk:col-start-2 desk:row-span-6 desk:row-start-1"
        aria-label={ru.map.title}
      >
        <MapView
          board={picture.board}
          players={picture.players}
          focus={mine?.cell}
          className="h-105 rounded-lg border-3 border-ink desk:h-190"
        />
        <MapLegend zones={season.zones} kinds={new Set(season.cells.map((cell) => cell.type))} />
      </section>
      <Panel title={ru.leaderboard.title} className="min-w-0 desk:col-start-1">
        <Leaderboard rows={picture.rows} />
      </Panel>
      {season.globalEvents?.length ? (
        <div className="min-w-0 desk:col-start-1">
          <GlobalEventBanner events={season.globalEvents} />
        </div>
      ) : null}
      <Panel title={t.effects} className="min-w-0 desk:col-start-1">
        {effects.length ? (
          <ul className="grid gap-3">
            {effects.map((effect) => (
              <li key={effect.id} className="grid gap-2">
                <h3 className="font-bold wrap-anywhere">
                  {effect.name ?? ru.effects.drawEvent(effect.drawEvent, effect.source)}
                </h3>
                {effect.description ? (
                  <p className="whitespace-pre-wrap wrap-anywhere">{effect.description}</p>
                ) : null}
                {effect.irl ? (
                  <section className="grid gap-2" aria-label={t.irl}>
                    {effect.irlProof ? (
                      <IrlMaterials proof={effect.irlProof} />
                    ) : (
                      <p className="text-sm text-ink-soft">{t.noProof}</p>
                    )}
                  </section>
                ) : null}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-ink-soft">{t.noEffects}</p>
        )}
        <WaitingEvents
          events={me?.waitingEvents ?? null}
          conditions={me?.waitingConditions ?? null}
        />
        {me?.lastEvent ? <DrawnEventCard drawn={me.lastEvent} fresh={false} level={3} /> : null}
      </Panel>
      {economy ? <EconomySnapshot value={economy} /> : null}
      {me?.partnerRequest || me?.activeDuels?.length ? (
        <Panel title={ru.turn.interactions} className="min-w-0 desk:col-start-1">
          {me.partnerRequest ? (
            <section className="grid gap-1" aria-label={ru.interactions.ownRequest}>
              <h3 className="font-bold">{ru.interactions.ownRequest}</h3>
              {me.partnerRequest.gameTitle ? <p>{me.partnerRequest.gameTitle}</p> : null}
              <p>
                {t.partnerStatus[me.partnerRequest.status]},{' '}
                {t.until(moscowTime(me.partnerRequest.expiresAt))}
              </p>
            </section>
          ) : null}
          {me.activeDuels?.length ? (
            <section className="grid gap-2" aria-label={ru.interactions.duels}>
              <h3 className="font-bold">{ru.interactions.duels}</h3>
              <ul className="grid gap-2">
                {me.activeDuels.map((duel) => (
                  <li key={duel.id} className="grid gap-1">
                    <p>
                      {ru.interactions.duelPair(
                        season.players.find((player) => player.id === duel.challengerId)?.name ??
                          t.unknownPlayer,
                        season.players.find((player) => player.id === duel.opponentId)?.name ??
                          t.unknownPlayer,
                      )}
                    </p>
                    <p className="text-sm text-ink-soft">
                      {ru.interactions.duelStatus[duel.status]},{' '}
                      {t.until(moscowTime(duel.expiresAt))}
                    </p>
                  </li>
                ))}
              </ul>
            </section>
          ) : null}
        </Panel>
      ) : null}
      <Panel title={t.proofs} className="min-w-0 desk:col-start-1">
        {proofs.length ? (
          <ul className="grid gap-4">
            {proofs.map((run) => (
              <li key={run.id} className="grid gap-2">
                <h3 className="font-display font-heavy wrap-anywhere">{run.game.title}</h3>
                <p>
                  {ru.difficulty[run.difficulty]}, {t.total(run.total)}
                </p>
                {run.dice.length ? (
                  <p className="flex flex-wrap gap-2 tabular-nums">
                    {run.dice.map((die, index) => (
                      <Chip key={index}>{t.die(die.sides, die.value)}</Chip>
                    ))}
                  </p>
                ) : null}
                {run.challengeDice.length ? (
                  <section aria-label={t.challengeDice} className="grid gap-1">
                    <h4 className="text-sm font-bold">{t.challengeDice}</h4>
                    <p className="flex flex-wrap gap-2 tabular-nums">
                      {run.challengeDice.map((die, index) => (
                        <Chip key={index}>{t.die(die.sides, die.value)}</Chip>
                      ))}
                    </p>
                  </section>
                ) : null}
                <RunConditions
                  conditions={run.conditions ?? null}
                  check={run.conditionCheck ?? null}
                />
                {run.proof ? (
                  <ProofMaterials proof={run.proof} players={season.players} />
                ) : (
                  <p className="text-sm text-ink-soft">{t.noProof}</p>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-ink-soft">{t.noProofs}</p>
        )}
      </Panel>
    </div>
  );
}

function PreviewGame({ game }: { game: Schemas['OfferedGameView'] }) {
  return (
    <article className="grid grid-cols-[auto_minmax(0,1fr)] gap-3 rounded-md bg-muted p-3">
      <Cover game={{ title: game.title, cover: game.cover?.thumbnailUrl }} />
      <div className="grid content-start gap-2 min-w-0">
        <h3 className="font-display text-lg font-heavy wrap-anywhere">{game.title}</h3>
        <GameFacts game={{ hours: game.hours, tags: [] }} />
        <GameMarks marks={game.marks} />
      </div>
    </article>
  );
}

function ProofMaterials({ proof, players }: { proof: Proof; players: Schemas['PlayerView'][] }) {
  const witness = proof.witnessId ? players.find((player) => player.id === proof.witnessId) : null;
  return (
    <section className="grid gap-2" aria-label={ru.proof.title}>
      <p className="font-medium">{ru.proof.status[proof.status]}</p>
      {proof.comment ? (
        <p className="whitespace-pre-wrap wrap-anywhere">{ru.proof.reviewComment(proof.comment)}</p>
      ) : null}
      <Materials files={proof.files} links={proof.links} />
      {proof.note ? (
        <p className="whitespace-pre-wrap wrap-anywhere">{t.note(proof.note)}</p>
      ) : null}
      {proof.witnessId ? <p>{witness ? t.witness(witness.name) : t.witnessUnknown}</p> : null}
    </section>
  );
}

function IrlMaterials({ proof }: { proof: IrlProof }) {
  return (
    <div className="grid gap-2">
      <p className="font-medium">{ru.proof.status[proof.status]}</p>
      <p className="text-sm text-ink-soft">{t.submitted(moscowTime(proof.submittedAt))}</p>
      {proof.comment ? (
        <p className="whitespace-pre-wrap wrap-anywhere">{ru.proof.reviewComment(proof.comment)}</p>
      ) : null}
      <Materials files={proof.files} links={proof.link ? [proof.link] : []} irl />
    </div>
  );
}

function Materials({
  files,
  links,
  irl = false,
}: {
  files: Schemas['FileLinkView'][];
  links: readonly string[];
  irl?: boolean;
}) {
  return (
    <>
      {links.length ? (
        <ul className="grid gap-1">
          {links.map((link, index) => (
            <li key={`${link}:${String(index)}`}>
              {(irl ? parseIrlLink(link) !== null : safeWebAddress(link)) ? (
                <a
                  href={link}
                  target="_blank"
                  rel="noreferrer noopener"
                  className={`${standaloneLink} gap-2 break-all`}
                >
                  <ExternalLink size={16} aria-hidden className="shrink-0" />
                  {t.link(index + 1)}: {link}
                </a>
              ) : (
                <span className="break-all">{link}</span>
              )}
            </li>
          ))}
        </ul>
      ) : null}
      {files.length ? (
        <ul className="flex flex-wrap gap-2">
          {files.map((file, index) => (
            <li key={file.id}>
              <a
                href={file.url}
                target="_blank"
                rel="noreferrer noopener"
                className="block rounded-md is-focus:focus-ring"
              >
                <img
                  src={file.thumbnailUrl}
                  alt={ru.proof.shotAlt(index + 1)}
                  width={96}
                  height={96}
                  className="size-24 rounded-md border-2 border-ink object-cover"
                />
              </a>
            </li>
          ))}
        </ul>
      ) : null}
    </>
  );
}

function safeWebAddress(address: string) {
  try {
    const url = new URL(address);
    return (
      (url.protocol === 'https:' || url.protocol === 'http:') && !url.username && !url.password
    );
  } catch {
    return false;
  }
}

function EconomySnapshot({ value }: { value: Schemas['EconomyView'] }) {
  return (
    <>
      {value.items !== null ? (
        <Panel title={ru.inventory.title} className="min-w-0 desk:col-start-1">
          <p className="tabular-nums">
            {ru.inventory.coins(value.coins)},{' '}
            {ru.inventory.count(value.items.length, value.inventoryLimit)}
          </p>
          {value.items.length ? (
            <ul className="grid gap-3">
              {value.items.map((item) => (
                <li key={item.instanceId} className="grid gap-2 rounded-md bg-muted p-3">
                  <h3 className="font-bold wrap-anywhere">{item.name}</h3>
                  <div className="flex flex-wrap gap-2">
                    <Chip>{ru.inventory.kinds[item.kind]}</Chip>
                    {item.rarity ? <Chip>{ru.inventory.rarity[item.rarity]}</Chip> : null}
                  </div>
                  {item.fromPlayer ? (
                    <p className="text-sm text-ink-soft">{ru.inventory.from(item.fromPlayer)}</p>
                  ) : null}
                  {item.usesLeft != null ? <p>{ru.inventory.usesLeft(item.usesLeft)}</p> : null}
                  {item.runsLeft != null ? <p>{ru.inventory.runsLeft(item.runsLeft)}</p> : null}
                  {item.expiresAt ? <p>{ru.inventory.until(moscowTime(item.expiresAt))}</p> : null}
                  <details>
                    <Summary>{t.details(item.name)}</Summary>
                    <p className="max-w-prose pt-2 whitespace-pre-wrap wrap-anywhere">
                      {item.description}
                    </p>
                  </details>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-ink-soft">{ru.inventory.emptyTitle}</p>
          )}
        </Panel>
      ) : null}
      {value.shop ? (
        <Panel title={ru.shop.title} className="min-w-0 desk:col-start-1">
          {value.shop.lots.length ? (
            <ul className="grid gap-3">
              {value.shop.lots.map((lot) => (
                <li key={lot.lot} className="grid gap-1">
                  <h3 className="font-bold wrap-anywhere">{lot.name}</h3>
                  <p>
                    {ru.inventory.coins(lot.price)}
                    {lot.sold ? `, ${t.sold}` : ''}
                  </p>
                  <p className="whitespace-pre-wrap wrap-anywhere">{lot.description}</p>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-ink-soft">{t.shopEmpty}</p>
          )}
        </Panel>
      ) : null}
      {value.bets ? (
        <Panel title={ru.bets.title} className="min-w-0 desk:col-start-1">
          {value.bets.bets.length ? (
            <ul className="grid gap-3">
              {value.bets.bets.map((bet) => (
                <li key={bet.betId} className="grid gap-1">
                  <h3 className="font-bold wrap-anywhere">
                    {t.betOn(bet.onPlayer, bet.gameTitle)}
                  </h3>
                  <p>{t.betStake(ru.inventory.coins(bet.stake), bet.multiplier)}</p>
                  <p>{t.betStatus[bet.status]}</p>
                  <p>{t.betPayout(ru.inventory.coins(bet.payout))}</p>
                </li>
              ))}
            </ul>
          ) : (
            <p className="text-ink-soft">{t.betsEmpty}</p>
          )}
        </Panel>
      ) : null}
    </>
  );
}
