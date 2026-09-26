import { Moon, UserPlus, Users } from 'lucide-react';
import { useState, type SyntheticEvent } from 'react';
import { api, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { ConfirmDanger } from '../ui/Dialogs';
import { Checkbox, Field, Select, TextArea } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { Panel } from '../ui/Surface';
import { commentProblem, newCommandId, parseWhole, refusal } from './actions';
import { Loading } from './common';
import { useLoad } from './useLoad';

const t = ru.admin.players;

type Player = Schemas['AdminPlayerView'];
type Cell = Schemas['SeasonView']['cells'][number];
type Reason = NonNullable<Schemas['TechRerollReason']>;

/** The cells as the admin picks them: by their number along the chain, the start and the finish named */
function cellOptions(cells: readonly Cell[]) {
  return cells.map((cell, i) => ({
    id: cell.id,
    label: t.cellOption(i, t.cellKinds[cell.type] ?? ''),
  }));
}

/** The season's players: balances, turn, the inactivity hint, and the admin's corrections */
export function PlayersSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoad(
    async () => {
      const [players, season] = await Promise.all([
        api.GET('/api/admin/seasons/{seasonId}/players', { params: { path: { seasonId } } }),
        api.GET('/api/seasons/{seasonId}', { params: { path: { seasonId } } }),
      ]);
      if (!players.data || !season.data) return undefined;
      return { players: players.data, cells: season.data.cells };
    },
    [seasonId],
    version,
  );
  const [done, setDone] = useState<string | null>(null);

  return (
    <Loading loaded={loaded}>
      {({ players, cells }, reload) => {
        const finished = (message: string) => {
          setDone(message);
          reload();
        };
        const quiet = players.filter((p) => p.inactiveHint && !p.isInactive);
        return (
          <div className="grid gap-6" data-testid="admin-players">
            {done ? <Notice tone="success">{done}</Notice> : null}
            {quiet.length > 0 ? (
              <Panel
                title={t.hintTitle}
                className="border-2 border-warning"
                data-testid="inactive-hint"
              >
                <p className="text-ink-soft">{t.hintText}</p>
                <ul className="grid gap-2">
                  {quiet.map((p) => (
                    <li key={p.id} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                      <Moon size={18} aria-hidden className="text-warning" />
                      <span className="font-bold">{p.name}</span>
                      <span className="text-sm text-ink-soft">
                        {t.lastAction(p.lastActionAt ? moscowTime(p.lastActionAt) : null)}
                      </span>
                    </li>
                  ))}
                </ul>
              </Panel>
            ) : null}
            {players.length === 0 ? (
              <EmptyState
                level={2}
                icon={<Users size={28} aria-hidden />}
                title={t.emptyTitle}
                text={t.emptyText}
              />
            ) : (
              <ul className="grid gap-3">
                {players.map((p) => (
                  <li key={p.id}>
                    <PlayerRow seasonId={seasonId} player={p} cells={cells} onDone={finished} />
                  </li>
                ))}
              </ul>
            )}
            <AddPlayer seasonId={seasonId} cells={cells} players={players} onDone={finished} />
          </div>
        );
      }}
    </Loading>
  );
}

function PlayerRow({
  seasonId,
  player,
  cells,
  onDone,
}: {
  seasonId: string;
  player: Player;
  cells: readonly Cell[];
  onDone: (message: string) => void;
}) {
  const [open, setOpen] = useState<'adjust' | 'techReroll' | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const index = cells.findIndex((c) => c.id === player.cellId);
  const path = { seasonId, playerId: player.id };

  async function setInactive(isInactive: boolean) {
    setBusy(true);
    setError(undefined);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/players/{playerId}/inactive', {
        params: { path },
        body: { commandId: newCommandId(), isInactive },
      });
      setConfirming(false);
      if (answer.data)
        onDone(isInactive ? t.markedInactive(player.name) : t.markedActive(player.name));
      else setError(refusal(answer));
    } catch {
      setConfirming(false);
      setError(ru.admin.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <article
      className="grid gap-3 rounded-lg bg-card p-4 wrap-anywhere"
      data-testid={`player-${player.id}`}
    >
      <div className="grid gap-1">
        <div className="flex flex-wrap items-center gap-2">
          <h2 className="font-display text-lg font-heavy">{player.name}</h2>
          {player.isInactive ? (
            <span className="rounded-full bg-muted px-2 text-xs font-bold">{t.inactive}</span>
          ) : null}
        </div>
        <p className="tabular-nums">
          {[t.points(player.points), t.coins(player.coins), t.cell(index < 0 ? null : index)].join(
            ', ',
          )}
        </p>
        <p className="text-sm text-ink-soft">
          {t.phase[player.phase]}.{' '}
          {t.lastAction(player.lastActionAt ? moscowTime(player.lastActionAt) : null)}
        </p>
      </div>
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <div className="flex flex-wrap gap-3">
        <Button
          aria-expanded={open === 'adjust'}
          data-testid="adjust-open"
          onClick={() => {
            setOpen(open === 'adjust' ? null : 'adjust');
          }}
        >
          {t.adjust}
        </Button>
        {player.phase === 'playing' ? (
          <Button
            aria-expanded={open === 'techReroll'}
            data-testid="tech-reroll-open"
            onClick={() => {
              setOpen(open === 'techReroll' ? null : 'techReroll');
            }}
          >
            {t.techReroll}
          </Button>
        ) : null}
        {player.isInactive ? (
          <Button
            variant="link"
            loading={busy}
            data-testid="mark-active"
            onClick={() => void setInactive(false)}
          >
            {t.markActive}
          </Button>
        ) : (
          <ConfirmDanger
            open={confirming}
            onOpenChange={setConfirming}
            trigger={
              <Button variant="dangerLink" data-testid="mark-inactive">
                {t.markInactive}
              </Button>
            }
            title={t.inactiveTitle(player.name)}
            consequences={t.inactiveConsequences}
            confirm={t.inactiveConfirm}
            busy={busy}
            onConfirm={() => void setInactive(true)}
          />
        )}
      </div>
      {open === 'adjust' ? (
        <AdjustForm
          seasonId={seasonId}
          player={player}
          cells={cells}
          onDone={(message) => {
            setOpen(null);
            onDone(message);
          }}
        />
      ) : null}
      {open === 'techReroll' ? (
        <TechRerollForm
          seasonId={seasonId}
          player={player}
          onDone={(message) => {
            setOpen(null);
            onDone(message);
          }}
        />
      ) : null}
    </article>
  );
}

function AdjustForm({
  seasonId,
  player,
  cells,
  onDone,
}: {
  seasonId: string;
  player: Player;
  cells: readonly Cell[];
  onDone: (message: string) => void;
}) {
  const [cellId, setCellId] = useState('');
  const [points, setPoints] = useState('');
  const [coins, setCoins] = useState('');
  const [discard, setDiscard] = useState(false);
  const [comment, setComment] = useState('');
  const [errors, setErrors] = useState<{
    points?: string | undefined;
    coins?: string | undefined;
    comment?: string | undefined;
    form?: string | undefined;
  }>({});
  const [busy, setBusy] = useState(false);

  async function submit(e: SyntheticEvent) {
    e.preventDefault();
    const pointsDelta = parseWhole(points);
    const coinsDelta = parseWhole(coins);
    const found = {
      points: pointsDelta === null ? t.numberInvalid : undefined,
      coins: coinsDelta === null ? t.numberInvalid : undefined,
      comment: commentProblem(comment),
    };
    const nothing = cellId === '' && pointsDelta === 0 && coinsDelta === 0 && !discard;
    setErrors({ ...found, form: nothing ? t.nothing : undefined });
    if (found.points || found.coins || found.comment || nothing) return;
    setBusy(true);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/players/{playerId}/adjust', {
        params: { path: { seasonId, playerId: player.id } },
        body: {
          commandId: newCommandId(),
          comment: comment.trim(),
          cellId: cellId === '' ? null : cellId,
          pointsDelta: pointsDelta ?? 0,
          coinsDelta: coinsDelta ?? 0,
          discardOffer: discard,
        },
      });
      if (answer.data) onDone(t.adjusted(player.name));
      else setErrors({ form: refusal(answer) });
    } catch {
      setErrors({ form: ru.admin.failed });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="grid gap-3 rounded-md border-2 border-muted p-3"
      onSubmit={(e) => void submit(e)}
      aria-label={t.adjustTitle(player.name)}
      data-testid="adjust-form"
      noValidate
    >
      <h3 className="font-bold">{t.adjustTitle(player.name)}</h3>
      <Select
        label={t.moveTo}
        value={cellId}
        data-testid="adjust-cell"
        onChange={(e) => {
          setCellId(e.target.value);
        }}
      >
        <option value="">{t.noMove}</option>
        {cellOptions(cells).map((c) => (
          <option key={c.id} value={c.id}>
            {c.label}
          </option>
        ))}
      </Select>
      <div className="grid gap-3 desk:grid-cols-2">
        <Field
          label={t.pointsDelta}
          hint={t.deltaHint}
          inputMode="numeric"
          value={points}
          error={errors.points}
          data-testid="adjust-points"
          onChange={(e) => {
            setPoints(e.target.value);
          }}
        />
        <Field
          label={t.coinsDelta}
          hint={t.deltaHint}
          inputMode="numeric"
          value={coins}
          error={errors.coins}
          data-testid="adjust-coins"
          onChange={(e) => {
            setCoins(e.target.value);
          }}
        />
      </div>
      {player.phase === 'rolling' ? (
        <Checkbox
          label={t.discardOffer}
          checked={discard}
          data-testid="adjust-discard"
          onChange={(e) => {
            setDiscard(e.target.checked);
          }}
        />
      ) : null}
      <TextArea
        label={ru.admin.comment}
        hint={ru.admin.commentHint}
        rows={2}
        maxLength={500}
        value={comment}
        error={errors.comment}
        data-testid="adjust-comment"
        onChange={(e) => {
          setComment(e.target.value);
        }}
      />
      {errors.form ? <Notice tone="danger">{errors.form}</Notice> : null}
      <div>
        <Button type="submit" loading={busy} data-testid="adjust-submit">
          {t.adjustSave}
        </Button>
      </div>
    </form>
  );
}

const reasons: Reason[] = [
  'weakPc',
  'paidUnavailable',
  'doesNotLaunch',
  'emulatorTooSlow',
  'other',
];

function TechRerollForm({
  seasonId,
  player,
  onDone,
}: {
  seasonId: string;
  player: Player;
  onDone: (message: string) => void;
}) {
  const [reason, setReason] = useState<Reason | ''>('');
  const [comment, setComment] = useState('');
  const [errors, setErrors] = useState<{
    reason?: string | undefined;
    comment?: string | undefined;
    form?: string | undefined;
  }>({});
  const [busy, setBusy] = useState(false);

  async function submit(e: SyntheticEvent) {
    e.preventDefault();
    const found = {
      reason: reason === '' ? ru.turn.techRerollReasonPlaceholder : undefined,
      comment: commentProblem(comment),
    };
    setErrors(found);
    if (found.reason || found.comment || reason === '') return;
    setBusy(true);
    try {
      const answer = await api.POST(
        '/api/admin/seasons/{seasonId}/players/{playerId}/tech-reroll',
        {
          params: { path: { seasonId, playerId: player.id } },
          body: { commandId: newCommandId(), reason, comment: comment.trim() },
        },
      );
      if (answer.data) onDone(t.techRerollDone(player.name));
      else setErrors({ form: refusal(answer) });
    } catch {
      setErrors({ form: ru.admin.failed });
    } finally {
      setBusy(false);
    }
  }

  return (
    <form
      className="grid gap-3 rounded-md border-2 border-muted p-3"
      onSubmit={(e) => void submit(e)}
      aria-label={t.techRerollTitle(player.name)}
      data-testid="tech-reroll-form"
      noValidate
    >
      <h3 className="font-bold">{t.techRerollTitle(player.name)}</h3>
      <p className="text-sm text-ink-soft">{t.techRerollLead}</p>
      <Select
        label={ru.turn.techRerollReason}
        value={reason}
        error={errors.reason}
        data-testid="tech-reroll-reason"
        onChange={(e) => {
          setReason(e.target.value as Reason | '');
        }}
      >
        <option value="">{ru.turn.techRerollReasonPlaceholder}</option>
        {reasons.map((r) => (
          <option key={r} value={r}>
            {ru.turn.techRerollReasons[r]}
          </option>
        ))}
      </Select>
      <TextArea
        label={ru.admin.comment}
        hint={ru.admin.commentHint}
        rows={2}
        maxLength={500}
        value={comment}
        error={errors.comment}
        data-testid="tech-reroll-comment"
        onChange={(e) => {
          setComment(e.target.value);
        }}
      />
      {errors.form ? <Notice tone="danger">{errors.form}</Notice> : null}
      <div>
        <Button type="submit" loading={busy} data-testid="tech-reroll-submit">
          {ru.turn.techRerollSubmit}
        </Button>
      </div>
    </form>
  );
}

/** A player joins the season: an account with the player role, a starting cell, points and coins (SE4) */
function AddPlayer({
  seasonId,
  cells,
  players,
  onDone,
}: {
  seasonId: string;
  cells: readonly Cell[];
  players: readonly Player[];
  onDone: (message: string) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Panel title={t.addTitle}>
      {open ? (
        <AddPlayerForm
          seasonId={seasonId}
          cells={cells}
          players={players}
          onDone={(message) => {
            setOpen(false);
            onDone(message);
          }}
        />
      ) : (
        <div>
          <Button
            icon={<UserPlus size={20} aria-hidden />}
            data-testid="add-player-open"
            onClick={() => {
              setOpen(true);
            }}
          >
            {t.add}
          </Button>
        </div>
      )}
    </Panel>
  );
}

function AddPlayerForm({
  seasonId,
  cells,
  players,
  onDone,
}: {
  seasonId: string;
  cells: readonly Cell[];
  players: readonly Player[];
  onDone: (message: string) => void;
}) {
  const accounts = useLoad(async () => (await api.GET('/api/admin/accounts')).data, []);
  const [userId, setUserId] = useState('');
  const [cellId, setCellId] = useState('');
  const [points, setPoints] = useState('');
  const [coins, setCoins] = useState('');
  const [errors, setErrors] = useState<{
    account?: string | undefined;
    points?: string | undefined;
    coins?: string | undefined;
    form?: string | undefined;
  }>({});
  const [busy, setBusy] = useState(false);

  return (
    <Loading loaded={accounts} rows={1}>
      {(all) => {
        const inSeason = new Set(players.map((p) => p.userId));
        const free = all.filter((a) => a.role === 'player' && !a.isDeleted && !inSeason.has(a.id));
        if (free.length === 0) return <p className="text-ink-soft">{t.nobodyToAdd}</p>;

        async function submit(e: SyntheticEvent) {
          e.preventDefault();
          const start = parseWhole(points);
          const purse = parseWhole(coins);
          const found = {
            account: userId === '' ? t.accountRequired : undefined,
            points: start === null ? t.numberInvalid : undefined,
            coins: purse === null ? t.numberInvalid : undefined,
          };
          setErrors(found);
          if (found.account || found.points || found.coins) return;
          setBusy(true);
          try {
            const answer = await api.POST('/api/admin/seasons/{seasonId}/players', {
              params: { path: { seasonId } },
              body: {
                commandId: newCommandId(),
                userId,
                cellId: cellId === '' ? null : cellId,
                points: start ?? 0,
                coins: purse ?? 0,
              },
            });
            const name = free.find((a) => a.id === userId)?.name ?? '';
            if (answer.data) onDone(t.added(name));
            else setErrors({ form: refusal(answer) });
          } catch {
            setErrors({ form: ru.admin.failed });
          } finally {
            setBusy(false);
          }
        }

        return (
          <form
            className="grid gap-3"
            onSubmit={(e) => void submit(e)}
            data-testid="add-player-form"
            noValidate
          >
            <Select
              label={t.account}
              value={userId}
              error={errors.account}
              data-testid="add-player-account"
              onChange={(e) => {
                setUserId(e.target.value);
              }}
            >
              <option value="">{t.accountPick}</option>
              {free.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name} ({a.login})
                </option>
              ))}
            </Select>
            <Select
              label={t.startCell}
              value={cellId}
              data-testid="add-player-cell"
              onChange={(e) => {
                setCellId(e.target.value);
              }}
            >
              <option value="">{t.startAtStart}</option>
              {cellOptions(cells)
                .slice(1, -1)
                .map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.label}
                  </option>
                ))}
            </Select>
            <div className="grid gap-3 desk:grid-cols-2">
              <Field
                label={t.startPoints}
                hint={t.startHint}
                inputMode="numeric"
                value={points}
                error={errors.points}
                data-testid="add-player-points"
                onChange={(e) => {
                  setPoints(e.target.value);
                }}
              />
              <Field
                label={t.startCoins}
                hint={t.startHint}
                inputMode="numeric"
                value={coins}
                error={errors.coins}
                data-testid="add-player-coins"
                onChange={(e) => {
                  setCoins(e.target.value);
                }}
              />
            </div>
            {errors.form ? <Notice tone="danger">{errors.form}</Notice> : null}
            <div>
              <Button type="submit" loading={busy} data-testid="add-player-submit">
                {t.addSubmit}
              </Button>
            </div>
          </form>
        );
      }}
    </Loading>
  );
}
