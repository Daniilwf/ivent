import type { Schemas } from '../api/client';
import { ProofCard } from '../admin/ProofQueue';
import { ru } from '../i18n/ru';
import { SelectField } from '../ui/Field';
import { Notice } from '../ui/States';

const t = ru.styleguide.admin;

// The styleguide's cards act on nothing
const noop = () => undefined;

const base: Schemas['ProofQueueItemView'] = {
  runId: 'demo-run-1',
  playerId: 'demo-player-1',
  playerName: 'Сова',
  gameTitle: 'The Legend of Zelda: Tears of the Kingdom — Master Mode',
  completedAt: '2026-09-20T09:00:00+00:00',
  reachedFinish: true,
  status: 'pending',
  links: ['https://youtu.be/credits-and-final-boss'],
  note: 'Финальный бой и титры в одном видео',
  witnessName: 'Ворон',
  difficulty: 'hard',
  hours: 62.5,
  diceTotal: 23,
  decidesFinish: true,
  files: [],
  rollClosed: false,
};

/** The admin's shared pieces: a run in the proof queue (the finish, the roll closed by the limit), a list choice */
export function AdminDemos() {
  return (
    <div className="grid gap-4">
      <div className="grid gap-4 desk:grid-cols-2">
        <ProofCard seasonId="demo" item={base} first onDone={noop} />
        <ProofCard
          seasonId="demo"
          item={{
            ...base,
            runId: 'demo-run-2',
            playerName: 'Барсук',
            gameTitle: 'Celeste',
            reachedFinish: false,
            decidesFinish: false,
            status: null,
            links: [],
            note: null,
            witnessName: null,
            difficulty: 'easy',
            hours: null,
            diceTotal: 4,
            rollClosed: true,
          }}
          first={false}
          onDone={noop}
        />
      </div>
      <Notice tone="warning">{ru.admin.proofs.rollClosedSummary('Барсук')}</Notice>
      <div className="grid max-w-110 gap-4">
        <SelectField label={t.select} hint={t.selectHint} defaultValue="">
          <option value="">{ru.admin.players.noMove}</option>
          <option value="c1">{ru.admin.players.cellOption(1, '')}</option>
        </SelectField>
        <SelectField label={t.select} error={t.selectError} defaultValue="">
          <option value="">{ru.turn.techRerollReasonPlaceholder}</option>
        </SelectField>
      </div>
    </div>
  );
}
