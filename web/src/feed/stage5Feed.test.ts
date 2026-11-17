import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { emptyRefs, feedDays, withRefs, type FeedItem } from './feedModel';

// SPEC «Ивенты», «Награда за прохождение», common choices; D-124/D-814/D-820.
// Scenarios use the public event payload and generated feed contract, without the rule implementation.
type Entry = Schemas['FeedEntryView'];
const playerId = '11111111-1111-1111-1111-111111111111';
const gameId = '22222222-2222-2222-2222-222222222222';
const runId = '33333333-3333-3333-3333-333333333333';
const effectId = '44444444-4444-4444-4444-444444444444';
const choiceId = '55555555-5555-5555-5555-555555555555';
const pollId = '66666666-6666-6666-6666-666666666666';
const now = new Date('2026-11-17T18:00:00Z');
const page: Schemas['FeedView'] = {
  entries: [],
  nextBefore: null,
  players: [
    { id: playerId, userId: 'owner', name: 'Вася', avatar: null, hasProfile: true, token: 0 },
  ],
  games: [{ id: gameId, title: 'Hollow Knight', hasPage: true }],
  runs: [{ id: runId, gameId }],
  objects: [
    { id: 'karaoke', name: 'Караоке' },
    { id: 'question', name: 'Нелёгкий выбор' },
    // A choice/effect id matching a content id is not evidence of an object reference.
    { id: choiceId, name: 'Чужая карточка выбора' },
    { id: effectId, name: 'Чужая карточка пруфа' },
  ],
};
let sequence = 10_000;
function entry(type: string, data: Record<string, unknown>, commandId = 'command'): Entry {
  return {
    sequence: sequence--,
    commandId,
    occurredAt: '2026-11-17T12:00:00Z',
    type,
    data,
    author: 'owner',
    undone: false,
  };
}
function line(item: FeedItem) {
  return item.line
    .map((part) =>
      typeof part === 'string' ? part : part.kind === 'player' ? part.player.name : part.game.title,
    )
    .join('');
}
function visible(item: FeedItem) {
  return [line(item), ...item.facts, item.quote?.text ?? ''].join('\n');
}
function one(entries: Entry[], referencePage: Schemas['FeedView'] = page) {
  const items = feedDays(entries, withRefs(emptyRefs(), referencePage), now).flatMap(
    (day) => day.items,
  );
  expect(items).toHaveLength(1);
  const item = items[0];
  if (!item) throw new Error('The command must have a feed row.');
  return item;
}
const completed = () =>
  entry('run-completed', {
    runId,
    playerId,
    difficulty: 'hard',
    hours: 7,
    completedAt: '2026-11-17T12:00:00Z',
  });
const drawn = () =>
  entry('event-drawn', {
    playerId,
    objectId: 'karaoke',
    deck: 'bad',
    kind: 'bad',
    source: 'cell',
  });
const suspended = (data: Record<string, unknown> = {}) =>
  entry('effect-suspended', { playerId, choiceId, objectId: 'question', ...data });
const resumed = (optionId: string) => entry('effect-resumed', { playerId, choiceId, optionId });
const submitted = (data: Record<string, unknown> = {}) =>
  entry('irl-proof-submitted', {
    effectId,
    playerId,
    submittedAt: '2026-11-17T12:00:00Z',
    ...data,
  });
const reviewed = (data: Record<string, unknown> = {}) =>
  entry('irl-proof-reviewed', {
    effectId,
    playerId,
    reviewedAt: '2026-11-17T12:00:00Z',
    ...data,
  });

describe('stage 5 feed: suspended questions and accepted continuations', () => {
  it('shows a named event waiting for the player’s answer', () => {
    const item = one([suspended()]);

    expect(line(item)).toContain('Вася');
    expect(line(item)).toContain('Нелёгкий выбор');
    expect(line(item)).toMatch(/жд[её]т ответа/i);
    expect(item.actor?.id).toBe(playerId);
    expect(visible(item)).not.toContain(choiceId);
  });

  it('describes a poll suspension as waiting for voting, not a personal answer', () => {
    const item = one([suspended({ pollId })]);

    expect(line(item)).toContain('Нелёгкий выбор');
    expect(line(item)).toMatch(/жд[её]т.*голосован/i);
    expect(visible(item)).not.toMatch(/жд[её]т ответа|ответь|выбери/i);
    expect(visible(item)).not.toContain(pollId);
  });

  it.each(['draw', 'completion'] as const)(
    'keeps a suspension as a fact of the %s command, without a second row',
    (main) => {
      const item = one([suspended(), main === 'draw' ? drawn() : completed()]);

      expect(line(item)).toContain(main === 'draw' ? 'Караоке' : 'Hollow Knight');
      expect(item.facts.join('\n')).toMatch(/жд[её]т ответа/i);
    },
  );

  it('keeps the poll waiting fact in a completion without asking the player to answer', () => {
    const item = one([suspended({ pollId }), drawn(), completed()]);

    expect(line(item)).toBe('Вася проходит Hollow Knight');
    expect(item.facts.join('\n')).toMatch(/жд[её]т.*голосован/i);
    expect(visible(item)).not.toMatch(/жд[её]т ответа|ответь|выбери/i);
    expect(item.facts).toContain(ru.feed.facts.eventDrawn('Караоке'));
  });

  it.each([pollId, 'forward', 'do-not-publish-this-option'])(
    'describes resumed %s neutrally without claiming a manual answer or publishing the option',
    (optionId) => {
      const item = one([resumed(optionId)]);

      expect(line(item)).toMatch(/ответ ивента принят/i);
      expect(line(item)).toContain('Вася');
      expect(visible(item)).not.toMatch(/ответил|выбрал|вручную|Чужая карточка/i);
      expect(visible(item)).not.toContain(optionId);
      expect(visible(item)).not.toContain(choiceId);
    },
  );

  it('leads a continuation over its new draw and money, while retaining actual consequences', () => {
    const item = one([
      drawn(),
      entry('coins-changed', { playerId, delta: 3, reason: 'effect' }),
      resumed('forward'),
    ]);

    expect(line(item)).toMatch(/ответ ивента принят/i);
    expect(line(item)).not.toContain('Караоке');
    expect(item.facts).toContain(ru.feed.facts.eventDrawn('Караоке'));
    expect(item.facts).toContain(ru.feed.facts.coins(3));
    expect(visible(item)).not.toContain('forward');
  });

  it('falls back safely when a suspended object or player is absent from references', () => {
    const item = one([suspended({ objectId: 'unknown-event' })], {
      ...page,
      players: [],
      objects: [],
    });

    expect(line(item)).toMatch(/жд[её]т ответа/i);
    expect(visible(item)).not.toContain(playerId);
    expect(visible(item)).not.toContain(choiceId);
  });
});

describe('stage 5 feed: IRL proof facts and privacy', () => {
  it('shows a redacted proof submission without inventing its object name', () => {
    const item = one([submitted()]);

    expect(line(item)).toContain('Вася');
    expect(line(item)).toMatch(/IRL-пруф/i);
    expect(line(item)).toMatch(/отправлен|подан/i);
    expect(item.quote).toBeNull();
    expect(visible(item)).not.toMatch(/Чужая карточка|Караоке/);
    expect(visible(item)).not.toContain(effectId);
  });

  it('never exposes submitted photos or video links even from the owner’s richer payload', () => {
    const item = one([
      submitted({
        files: ['private-photo-id'],
        link: 'https://video.example/private-proof',
      }),
    ]);

    expect(line(item)).toMatch(/IRL-пруф/i);
    expect(visible(item)).not.toContain('private-photo-id');
    expect(visible(item)).not.toContain('https://video.example/private-proof');
    expect(item.quote).toBeNull();
  });

  it.each([
    { approved: true, verdict: /одобрен/i },
    { approved: false, verdict: /отклон[её]н/i },
  ])('shows the explicit boolean verdict $approved', ({ approved, verdict }) => {
    const item = one([reviewed({ approved })]);

    expect(line(item)).toMatch(/IRL-пруф/i);
    expect(line(item)).toMatch(verdict);
    expect(visible(item)).not.toMatch(/Чужая карточка|Караоке/);
    expect(visible(item)).not.toContain(effectId);
    expect(item.quote).toBeNull();
  });

  it.each([undefined, null, 'false', 'true', 0, 1])(
    'uses a generic checked status for missing or malformed approved=%s',
    (approved) => {
      const item = one([reviewed(approved === undefined ? {} : { approved })]);

      expect(line(item)).toMatch(/IRL-пруф.*проверен/i);
      expect(line(item)).not.toMatch(/одобрен|отклон[её]н/i);
      expect(item.quote).toBeNull();
    },
  );

  it('leads approval over manual resolution and coins without losing the real money fact', () => {
    const item = one([
      entry('coins-changed', { playerId, delta: 2, reason: 'effect' }),
      entry('manual-effect-resolved', {
        effectId,
        playerId,
        runId: null,
        outcome: 'applied',
        comment: '',
      }),
      reviewed({ approved: true }),
    ]);

    expect(line(item)).toMatch(/IRL-пруф.*одобрен/i);
    expect(item.facts).toContain(ru.feed.facts.coins(2));
    expect(item.quote).toBeNull();
  });

  it('does not turn a manual applied outcome into IRL approval', () => {
    const item = one([
      entry('manual-effect-resolved', {
        effectId,
        playerId,
        runId: null,
        outcome: 'applied',
        comment: '',
      }),
    ]);

    expect(line(item)).toContain('Вася');
    expect(line(item)).not.toMatch(/IRL|пруф.*одобрен/i);
  });

  it('keeps a public review without a quote and ignores richer private review comments', () => {
    const publicItem = one([reviewed({ approved: false })]);
    const ownerItem = one([reviewed({ approved: false, comment: 'Private moderation details' })]);

    expect(publicItem.quote).toBeNull();
    expect(ownerItem.quote).toBeNull();
    expect(visible(ownerItem)).not.toContain('Private moderation details');
    expect(line(ownerItem)).toBe(line(publicItem));
  });

  it('does not borrow a neighbouring draw’s name for an IRL verdict', () => {
    const item = one([drawn(), reviewed({ approved: false })]);

    expect(line(item)).toMatch(/IRL-пруф.*отклон[её]н/i);
    expect(line(item)).not.toMatch(/Караоке|Чужая карточка/);
    expect(item.facts).toContain(ru.feed.facts.eventDrawn('Караоке'));
  });

  it('retains a review even if reference enrichment is unavailable', () => {
    const item = one([reviewed({ approved: true })], {
      ...page,
      players: [],
      objects: [],
    });

    expect(line(item)).toMatch(/IRL-пруф.*одобрен/i);
    expect(visible(item)).not.toContain(playerId);
    expect(visible(item)).not.toContain(effectId);
  });
});

describe('stage 5 feed: aggregate run condition checks', () => {
  it.each([
    {
      met: true,
      waived: false,
      expected: /условие.*выполнено/i,
      forbidden: /не выполнено|неприменимо/i,
    },
    { met: false, waived: false, expected: /условие.*не выполнено/i, forbidden: /неприменимо/i },
    { met: true, waived: true, expected: /условие.*неприменимо/i, forbidden: /выполнено/i },
  ])(
    'keeps the $met / waived $waived result inside a completion',
    ({ met, waived, expected, forbidden }) => {
      const item = one([
        entry('run-condition-checked', { runId, playerId, met, waived, difficulty: 'hard' }),
        completed(),
      ]);

      expect(line(item)).toBe('Вася проходит Hollow Knight');
      const resultFacts = item.facts.filter((fact) => /условие/i.test(fact));
      expect(resultFacts).toHaveLength(1);
      expect(resultFacts[0]).toMatch(expected);
      expect(resultFacts[0]).not.toMatch(forbidden);
      expect(visible(item)).not.toMatch(/куб|монет/i);
    },
  );

  it.each([
    { met: true, waived: false, expected: /условие.*выполнено/i },
    { met: false, waived: false, expected: /условие.*не выполнено/i },
    { met: true, waived: true, expected: /условие.*неприменимо/i },
  ])(
    'retains the check when difficulty is corrected: $met / waived $waived',
    ({ met, waived, expected }) => {
      const item = one([
        entry('run-condition-checked', { runId, playerId, met, waived, difficulty: 'normal' }),
        entry('run-difficulty-changed', {
          runId,
          playerId,
          oldDifficulty: 'hard',
          newDifficulty: 'normal',
          dice: [],
          challengeDice: [],
          comment: '',
          changedAt: '2026-11-17T12:00:00Z',
        }),
      ]);

      expect(line(item)).toContain('Hollow Knight');
      const resultFacts = item.facts.filter((fact) => /условие/i.test(fact));
      expect(resultFacts).toHaveLength(1);
      expect(resultFacts[0]).toMatch(expected);
      expect(visible(item)).not.toMatch(/куб|монет/i);
    },
  );

  it.each([{ met: true }, { met: false }, { met: true, waived: true }])(
    'does not infer dice or coin payouts from a standalone check $met/$waived',
    (result) => {
      const item = one([
        entry('run-condition-checked', { runId, playerId, difficulty: 'hard', ...result }),
      ]);

      expect(line(item)).toMatch(/условие прохождения/i);
      expect(visible(item)).not.toMatch(/куб|монет/i);
      expect(item.facts.filter((fact) => /условие/i.test(fact))).toHaveLength(0);
    },
  );
});

describe('stage 5 feed: object manual tasks versus legacy event instructions', () => {
  it.each(['drop', 'paidReroll', 'difficulty'])(
    'names an object task with null drawEvent from %s without inventing a good event',
    (source) => {
      const item = one([
        entry('manual-effect-created', {
          effectId,
          playerId,
          objectId: 'karaoke',
          drawEvent: null,
          source,
          runId: null,
        }),
      ]);

      expect(visible(item)).toContain('Караоке');
      expect(visible(item)).not.toMatch(/хороший|плохой|Чужая карточка/i);
    },
  );

  it('does not make a legacy good-event chip from the object task next to an actual bad draw', () => {
    const item = one([
      entry('manual-effect-created', {
        effectId,
        playerId,
        objectId: 'karaoke',
        drawEvent: null,
        source: 'drop',
        runId,
      }),
      drawn(),
      completed(),
    ]);

    expect(line(item)).toBe('Вася проходит Hollow Knight');
    expect(item.facts).toContain(ru.feed.facts.eventDrawn('Караоке'));
    expect(visible(item)).not.toMatch(/хороший ивент/i);
  });

  it('keeps an unknown object task legible without a good-event default or GUID', () => {
    const item = one([
      entry('manual-effect-created', {
        effectId,
        playerId,
        objectId: 'unknown-event',
        drawEvent: null,
        source: 'drop',
        runId: null,
      }),
    ]);

    expect(line(item)).toContain('Вася');
    expect(visible(item)).not.toMatch(/хороший|Чужая карточка/i);
    expect(visible(item)).not.toContain(effectId);
  });

  it.each(['good', 'bad'] as const)(
    'preserves the legacy %s draw instruction when there is no objectId',
    (drawEvent) => {
      const item = one([
        entry('manual-effect-created', {
          effectId,
          playerId,
          drawEvent,
          source: 'drop',
          runId,
        }),
        entry('run-dropped', { runId, playerId, penaltyDice: [] }),
      ]);

      expect(line(item)).toBe('Вася дропает Hollow Knight');
      expect(item.facts).toContain(ru.effects.drawEvent(drawEvent, 'drop'));
    },
  );
});

describe('stage 5 feed: undo preserves history rather than reinterpreting it', () => {
  it.each(['submission', 'review', 'continuation'] as const)(
    'keeps an undone %s row visibly marked without identifiers or private proof content',
    (kind) => {
      const source =
        kind === 'submission'
          ? submitted({ files: ['secret-file'], link: 'https://private.example/video' })
          : kind === 'review'
            ? reviewed({ approved: false })
            : resumed('secret-option');
      const item = one([{ ...source, undone: true }]);

      expect(item.undone).toBe(true);
      expect(line(item)).toContain('Вася');
      expect(visible(item)).not.toMatch(/secret-file|private\.example|secret-option/);
      expect(visible(item)).not.toContain(effectId);
      expect(visible(item)).not.toContain(choiceId);
    },
  );
});
