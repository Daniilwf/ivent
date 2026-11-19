import { Handshake } from 'lucide-react';
import { useId, useState, type ReactNode } from 'react';
import type { Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { useIsPast } from '../app/useIsPast';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Summary } from '../ui/Disclosure';
import { Select } from '../ui/Field';
import { EmptyState } from '../ui/States';
import type { Duel, InteractionAction, InteractionSnapshot, Invitation } from './interactionData';

type Controls = {
  pending: InteractionAction | null;
  disabled: boolean;
  onAction: (action: InteractionAction) => void;
};
type Player = Schemas['PlayerView'];

const t = ru.interactions;

const cardClass = 'grid min-w-0 gap-2 rounded-lg border-2 border-muted px-3 py-3';
const detailsClass = 'grid min-w-0 gap-2 rounded-md text-sm text-ink';

function Panel({ title, children }: { title: string; children: ReactNode }) {
  const heading = useId();
  return (
    <section
      aria-labelledby={heading}
      className="grid min-w-0 gap-3 rounded-lg bg-card p-4 desk:p-6"
    >
      <h2 id={heading} className="font-display text-lg font-heavy">
        {title}
      </h2>
      {children}
    </section>
  );
}

function matchingAction(action: InteractionAction, pending: InteractionAction | null) {
  return (
    action.kind === pending?.kind &&
    (!('id' in action) || ('id' in pending && action.id === pending.id))
  );
}

function ActionButton({
  action,
  controls,
  primary,
  children,
}: {
  action: InteractionAction;
  controls: Controls;
  primary?: boolean;
  children: ReactNode;
}) {
  const loading = matchingAction(action, controls.pending);
  return (
    <Button
      variant={primary ? 'main' : 'quiet'}
      disabled={controls.disabled && !loading}
      loading={loading}
      onClick={() => {
        controls.onAction(action);
      }}
    >
      {children}
    </Button>
  );
}

function Expiration({ until }: { until: string }) {
  return (
    <p className="text-sm text-ink-soft">
      {t.expires} <time dateTime={until}>{moscowTime(until)}</time>
    </p>
  );
}

function PlayerChoice({
  players,
  kind,
  controls,
}: {
  players: Player[];
  kind: 'invite' | 'challenge';
  controls: Controls;
}) {
  const [selected, setSelected] = useState('');
  const playerId = players.some(({ id }) => id === selected) ? selected : '';
  const action = { kind, playerId };
  const loading = matchingAction(action, controls.pending);
  if (players.length === 0) return null;
  return (
    <form
      className="grid min-w-0 gap-2 desk:grid-cols-[minmax(0,1fr)_auto] desk:items-end"
      onSubmit={(event) => {
        event.preventDefault();
        if (playerId && !controls.disabled) controls.onAction(action);
      }}
    >
      <Select
        label={kind === 'invite' ? t.partner : t.opponent}
        value={playerId}
        disabled={controls.disabled}
        onChange={(event) => {
          setSelected(event.target.value);
        }}
      >
        <option value="">{t.choose}</option>
        {players.map(({ id, name }) => (
          <option key={id} value={id}>
            {name}
          </option>
        ))}
      </Select>
      <Button
        type="submit"
        disabled={!playerId || (controls.disabled && !loading)}
        loading={loading}
      >
        {kind === 'invite' ? t.invite : t.challenge}
      </Button>
    </form>
  );
}

function InvitationList({
  items,
  me,
  name,
  snapshot,
  active,
  firstFinisher,
  controls,
}: {
  items: Invitation[];
  me: string;
  name: (playerId: string) => string;
  snapshot: InteractionSnapshot;
  active: boolean;
  firstFinisher: boolean;
  controls: Controls;
}) {
  const firstAccept =
    active && !firstFinisher
      ? items.find(
          (item) =>
            item.status === 'pending' &&
            item.toPlayerId === me &&
            (item.kind === 'coop' ? snapshot.features.coop : snapshot.features.duels),
        )?.id
      : undefined;
  return (
    <ul className="grid min-w-0 gap-2">
      {items.map((item) => {
        const incoming = item.toPlayerId === me;
        const actions =
          active &&
          item.status === 'pending' &&
          (item.kind === 'coop' || (snapshot.features.duels && !firstFinisher));
        const canAccept =
          actions && incoming && !firstFinisher && (item.kind === 'duel' || snapshot.features.coop);
        return (
          <li key={item.id} className={cardClass}>
            <p className="font-bold wrap-anywhere">{t.kind[item.kind]}</p>
            <p className="wrap-anywhere">
              {incoming ? t.incoming(name(item.fromPlayerId)) : t.outgoing(name(item.toPlayerId))}
            </p>
            <p className="text-sm text-ink-soft">{t.invitationStatus[item.status]}</p>
            {item.status === 'pending' ? (
              <Expiration until={item.effectiveExpiresAt ?? item.expiresAt} />
            ) : null}
            {actions ? (
              <div className="flex flex-wrap gap-2">
                {canAccept ? (
                  <ActionButton
                    action={{ kind: 'accept', id: item.id }}
                    controls={controls}
                    primary={item.id === firstAccept}
                  >
                    {t.accept}
                  </ActionButton>
                ) : null}
                {incoming ? (
                  <ActionButton action={{ kind: 'decline', id: item.id }} controls={controls}>
                    {t.decline}
                  </ActionButton>
                ) : (
                  <ActionButton action={{ kind: 'withdraw', id: item.id }} controls={controls}>
                    {t.withdraw}
                  </ActionButton>
                )}
              </div>
            ) : null}
          </li>
        );
      })}
    </ul>
  );
}

function DuelList({ items, name }: { items: Duel[]; name: (playerId: string) => string }) {
  return (
    <ul className="grid min-w-0 gap-2">
      {items.map((duel) => (
        <li key={duel.id} className={cardClass}>
          <p className="font-bold wrap-anywhere">
            {t.duelPair(name(duel.challengerId), name(duel.opponentId))}
          </p>
          <p>{t.duelStatus[duel.status]}</p>
          {duel.status === 'active' ? <Expiration until={duel.expiresAt} /> : null}
          {duel.status === 'won' && duel.winnerId ? (
            <>
              <p className="wrap-anywhere">{t.duelWinner(name(duel.winnerId))}</p>
              {duel.coins > 0 ? <p>{t.duelCoins(duel.coins)}</p> : null}
            </>
          ) : null}
          {duel.end ? <p className="text-sm text-ink-soft">{t.duelEnd[duel.end]}</p> : null}
        </li>
      ))}
    </ul>
  );
}

export function InteractionsContent({
  snapshot,
  ...controls
}: { snapshot: InteractionSnapshot } & Controls) {
  const { season, features, invitations, coop, board, duels } = snapshot;
  const me = season.me;
  const meId = me?.playerId;
  const pastDeadline = useIsPast(season.deadline);
  const active = season.status === 'active' && !pastDeadline;
  const firstFinisher = season.players.some(
    ({ id, finishOrder }) => id === meId && finishOrder === 1,
  );
  const canStartInteraction = active && !firstFinisher;
  const name = (playerId: string) =>
    season.players.find(({ id }) => id === playerId)?.name ?? t.player;
  const candidates = season.players.filter(
    ({ id, finishOrder, isInactive }) => id !== meId && finishOrder !== 1 && isInactive !== true,
  );
  const pendingInvitations = invitations.filter(({ status }) => status === 'pending');
  const invitationHistory = invitations.filter(({ status }) => status !== 'pending');
  const ownRequest = me?.partnerRequest ?? board.find(({ playerId }) => playerId === meId);
  const boardRequests = board.filter(({ id }) => id !== ownRequest?.id);
  const activeDuels = duels.filter(({ status }) => status === 'active');
  const settledDuels = duels.filter(({ status }) => status !== 'active');
  const ownCoopGame =
    canStartInteraction && me?.activeRun?.game.isCoop === true && !coop && !me.isCoopPartner;
  const activeRunId = me?.activeRun?.id;
  const mayInvite =
    ownCoopGame &&
    features.coop &&
    !pendingInvitations.some(
      ({ fromPlayerId, kind, runId }) =>
        fromPlayerId === meId && kind === 'coop' && runId === activeRunId,
    );

  if (
    !(meId && features.coop) &&
    !features.duels &&
    !features.partnerBoard &&
    !pendingInvitations.length &&
    !coop &&
    !ownRequest
  ) {
    return <EmptyState title={t.unavailable} icon={<Handshake aria-hidden />} level={2} />;
  }

  return (
    <>
      {meId && (features.coop || features.duels || pendingInvitations.length > 0) ? (
        <Panel title={t.invitations}>
          {pendingInvitations.length ? (
            <InvitationList
              items={pendingInvitations}
              me={meId}
              name={name}
              snapshot={snapshot}
              active={active}
              firstFinisher={firstFinisher}
              controls={controls}
            />
          ) : (
            <p className="text-ink-soft">{t.emptyInvitations}</p>
          )}
          {invitationHistory.length ? (
            <details className={detailsClass}>
              <Summary className="font-bold">{t.recentInvitations}</Summary>
              <InvitationList
                items={invitationHistory}
                me={meId}
                name={name}
                snapshot={snapshot}
                active={active}
                firstFinisher={firstFinisher}
                controls={controls}
              />
            </details>
          ) : null}
        </Panel>
      ) : null}
      {coop || mayInvite ? (
        <Panel title={t.coop}>
          {coop ? (
            <>
              <p className="wrap-anywhere">
                {coop.partnerId === meId
                  ? t.withRoller(name(coop.rollerId))
                  : t.withPartner(name(coop.partnerId))}
              </p>
              {active && coop.partnerId === meId ? (
                <div>
                  <ActionButton action={{ kind: 'leave' }} controls={controls}>
                    {t.leave}
                  </ActionButton>
                </div>
              ) : null}
            </>
          ) : (
            <PlayerChoice kind="invite" players={candidates} controls={controls} />
          )}
        </Panel>
      ) : null}
      {features.partnerBoard || ownRequest ? (
        <Panel title={t.board}>
          {ownRequest ? (
            <div className={cardClass}>
              <p className="font-bold">{t.ownRequest}</p>
              <p className="wrap-anywhere">
                {ownRequest.gameTitle ?? me?.activeRun?.game.title ?? t.noGameTitle}
              </p>
              <Expiration until={ownRequest.expiresAt} />
              {!features.partnerBoard ? <p className="text-ink-soft">{t.boardOff}</p> : null}
              {active ? (
                <div>
                  <ActionButton action={{ kind: 'close', id: ownRequest.id }} controls={controls}>
                    {t.close}
                  </ActionButton>
                </div>
              ) : null}
            </div>
          ) : features.partnerBoard && ownCoopGame ? (
            <div>
              <ActionButton action={{ kind: 'publish' }} controls={controls}>
                {t.publish}
              </ActionButton>
            </div>
          ) : null}
          {features.partnerBoard && boardRequests.length ? (
            <ul className="grid min-w-0 gap-2">
              {boardRequests.map((request) => {
                const responded = pendingInvitations.some(
                  ({ fromPlayerId, toPlayerId, kind, runId }) =>
                    kind === 'coop' &&
                    fromPlayerId === meId &&
                    toPlayerId === request.playerId &&
                    runId === request.runId,
                );
                return (
                  <li key={request.id} className={cardClass}>
                    <p className="font-bold wrap-anywhere">{request.gameTitle ?? t.noGameTitle}</p>
                    <p className="wrap-anywhere">{name(request.playerId)}</p>
                    <Expiration until={request.expiresAt} />
                    {responded ? (
                      <p className="text-sm text-ink-soft">{t.invitationStatus.pending}</p>
                    ) : null}
                    {canStartInteraction && me?.phase === 'idle' && !coop && !responded ? (
                      <div>
                        <ActionButton
                          action={{ kind: 'respond', id: request.id }}
                          controls={controls}
                        >
                          {t.respond}
                        </ActionButton>
                      </div>
                    ) : null}
                  </li>
                );
              })}
            </ul>
          ) : features.partnerBoard && !ownRequest ? (
            <p className="text-ink-soft">{t.boardEmpty}</p>
          ) : null}
        </Panel>
      ) : null}
      {features.duels ? (
        <Panel title={t.duels}>
          {canStartInteraction && meId ? (
            <PlayerChoice kind="challenge" players={candidates} controls={controls} />
          ) : null}
          {activeDuels.length ? (
            <DuelList items={activeDuels} name={name} />
          ) : (
            <p className="text-ink-soft">{t.noDuels}</p>
          )}
          {settledDuels.length ? (
            <details className={detailsClass}>
              <Summary className="font-bold">{t.duelHistory}</Summary>
              <DuelList items={settledDuels} name={name} />
            </details>
          ) : null}
        </Panel>
      ) : null}
    </>
  );
}
