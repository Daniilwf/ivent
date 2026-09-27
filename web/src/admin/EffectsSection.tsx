import { Sparkles } from 'lucide-react';
import { useCallback, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { TextArea } from '../ui/Field';
import { EmptyState, Notice } from '../ui/States';
import { commentProblem, refusal } from './actions';
import { newCommandId } from '../api/commands';
import { AsyncState } from '../ui/AsyncState';
import { answerOf, useLoaded } from '../app/useLoaded';

const t = ru.admin.effects;

type Effect = Schemas['AdminManualEffectView'];
type Outcome = 'applied' | 'notApplicable';

/** Pending manual effects of every player, oldest first: the admin resolves them for a player (D-102) */
export function EffectsSection({ seasonId, version }: { seasonId: string; version: number }) {
  const loaded = useLoaded(
    useCallback(
      async () =>
        answerOf(
          await api.GET('/api/admin/seasons/{seasonId}/effects', {
            params: { path: { seasonId } },
          }),
        ),
      [seasonId],
    ),
    { version },
  );
  const [done, setDone] = useState<string | null>(null);
  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(effects, reload) => (
        <div className="grid gap-4" data-testid="admin-effects">
          {done ? <Notice tone="success">{done}</Notice> : null}
          {effects.length === 0 ? (
            <EmptyState
              level={2}
              icon={<Sparkles size={28} aria-hidden />}
              title={t.emptyTitle}
              text={t.emptyText}
            />
          ) : (
            <ul className="grid gap-3">
              {effects.map((effect) => (
                <li key={effect.id}>
                  <EffectRow
                    seasonId={seasonId}
                    effect={effect}
                    onDone={() => {
                      setDone(t.resolved(effect.playerName));
                      reload();
                    }}
                  />
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </AsyncState>
  );
}

function EffectRow({
  seasonId,
  effect,
  onDone,
}: {
  seasonId: string;
  effect: Effect;
  onDone: () => void;
}) {
  const [comment, setComment] = useState('');
  const [commentError, setCommentError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<Outcome | null>(null);

  async function resolve(outcome: Outcome) {
    const problem = commentProblem(comment);
    setCommentError(problem);
    if (problem) return;
    setBusy(outcome);
    setError(undefined);
    try {
      const answer = await api.POST('/api/admin/seasons/{seasonId}/effects/{effectId}/resolve', {
        params: { path: { seasonId, effectId: effect.id } },
        body: { commandId: newCommandId(), outcome, comment: comment.trim() },
      });
      if (answer.data) onDone();
      else setError(refusal(answer));
    } catch {
      setError(ru.admin.failed);
    } finally {
      setBusy(null);
    }
  }

  return (
    <article className="grid gap-3 rounded-lg bg-card p-4" data-testid={`effect-${effect.id}`}>
      <div className="grid gap-1">
        <h2 className="font-display text-lg font-heavy">{effect.playerName}</h2>
        <p>{ru.effects.drawEvent(effect.drawEvent, effect.source)}</p>
      </div>
      <TextArea
        label={ru.admin.comment}
        hint={ru.admin.commentHint}
        rows={2}
        maxLength={500}
        value={comment}
        error={commentError}
        data-testid="effect-comment"
        onChange={(e) => {
          setComment(e.target.value);
        }}
      />
      {error ? <Notice tone="danger">{error}</Notice> : null}
      <div className="flex flex-wrap gap-3">
        <Button
          loading={busy === 'applied'}
          disabled={busy === 'notApplicable'}
          data-testid="effect-applied"
          onClick={() => void resolve('applied')}
        >
          {ru.effects.applied}
        </Button>
        <Button
          loading={busy === 'notApplicable'}
          disabled={busy === 'applied'}
          data-testid="effect-not-applicable"
          onClick={() => void resolve('notApplicable')}
        >
          {ru.effects.notApplicable}
        </Button>
      </div>
    </article>
  );
}
