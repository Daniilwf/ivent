import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import {
  emptyRefs,
  feedDays,
  mergeEntries,
  withRefs,
  type FeedItem,
  type FeedRef,
} from './feedModel';

// H5: the feed's lines from the log (D-150) — one command, one line; who did what with which game; the facts and the
// quote; undone commands kept and marked; days by Moscow time, newest first.

type Entry = Schemas['FeedEntryView'];

const vasya = '11111111-1111-1111-1111-111111111111';
const petya = '22222222-2222-2222-2222-222222222222';
const hollow = '33333333-3333-3333-3333-333333333333';
const celeste = '44444444-4444-4444-4444-444444444444';
const run1 = '55555555-5555-5555-5555-555555555555';

const page = {
  players: [
    { id: petya, userId: 'u-petya', name: 'Петя', avatar: null, hasProfile: true, token: 0 },
    {
      id: vasya,
      userId: 'u-vasya',
      name: 'Вася',
      avatar: { id: 'f', url: '/a', thumbnailUrl: '/t' },
      hasProfile: true,
      token: 1,
    },
  ],
  games: [
    { id: hollow, title: 'Hollow Knight', hasPage: true },
    { id: celeste, title: 'Celeste', hasPage: false },
  ],
  runs: [{ id: run1, gameId: hollow }],
};
const refs = withRefs(emptyRefs(), page);

let sequence = 100;
function entry(
  command: string,
  type: string,
  data: Record<string, unknown>,
  { at = '2026-09-26T12:00:00Z', undone = false }: { at?: string; undone?: boolean } = {},
): Entry {
  sequence -= 1;
  return { sequence, commandId: command, occurredAt: at, type, data, author: 'vasya', undone };
}

/** A line as plain text: the names in it as they are shown */
function text(item: FeedItem | undefined) {
  return (item?.line ?? [])
    .map((part) =>
      typeof part === 'string' ? part : part.kind === 'player' ? part.player.name : part.game.title,
    )
    .join('');
}

const refsOf = (item: FeedItem | undefined) =>
  (item?.line ?? []).filter((p): p is FeedRef => typeof p !== 'string');

const now = new Date('2026-09-26T18:00:00Z');
const one = (entries: Entry[]) => feedDays(entries, refs, now)[0]?.items[0];

describe('feed lines', () => {
  it('makes one line of a completion: the game by its run, the dice, points and cells as one fact, the finish', () => {
    // Newest first, as the feed gives them
    const item = one([
      entry('c1', 'player-finished', { playerId: vasya, runId: run1, order: 1, surplus: 0 }),
      entry('c1', 'player-moved', {
        playerId: vasya,
        from: 'a',
        to: 'f',
        steps: 7,
        path: ['b', 'c', 'd', 'e', 'f', 'g', 'h'],
        reason: 'completionRoll',
        runId: run1,
      }),
      entry('c1', 'points-changed', {
        playerId: vasya,
        delta: 7,
        reason: 'completionRoll',
        runId: run1,
      }),
      entry('c1', 'completion-rolled', {
        runId: run1,
        playerId: vasya,
        dice: [
          { sides: 6, value: 4 },
          { sides: 6, value: 3 },
        ],
        challengeDice: [],
      }),
      entry('c1', 'run-completed', {
        runId: run1,
        playerId: vasya,
        difficulty: 'hard',
        hours: 27,
        completedAt: '2026-09-26T12:00:00Z',
      }),
    ]);

    expect(text(item)).toBe('Вася проходит Hollow Knight');
    expect(item?.icon).toBe('complete');
    expect(item?.actor?.name).toBe('Вася');
    expect(item?.facts).toEqual([
      ru.difficulty.hard,
      '27 ч',
      'Кубы: 4 + 3 = 7',
      'Финиш: 1-е место',
      '+7 очков и клеток',
    ]);
    expect(refsOf(item).map((r) => (r.kind === 'game' ? r.game.id : r.player.userId))).toEqual([
      'u-vasya',
      hollow,
    ]);
  });

  it('says a drop with its penalty dice and what it took', () => {
    const item = one([
      entry('c2', 'manual-effect-created', {
        effectId: 'e',
        playerId: petya,
        drawEvent: 'bad',
        source: 'drop',
        runId: run1,
      }),
      entry('c2', 'player-moved', {
        playerId: petya,
        from: 'f',
        to: 'c',
        steps: -5,
        path: ['e', 'd', 'c'],
        reason: 'dropPenalty',
        runId: run1,
      }),
      entry('c2', 'points-changed', {
        playerId: petya,
        delta: -5,
        reason: 'dropPenalty',
        runId: run1,
      }),
      entry('c2', 'game-excluded', { playerId: petya, gameId: hollow, reason: 'dropped' }),
      entry('c2', 'run-dropped', {
        runId: run1,
        playerId: petya,
        penaltyDice: [
          { sides: 4, value: 2 },
          { sides: 4, value: 3 },
        ],
      }),
    ]);

    expect(text(item)).toBe('Петя дропает Hollow Knight');
    expect(item?.icon).toBe('drop');
    // A checkpoint stopped the token after 3 cells: points and cells differ, so they are two facts
    expect(item?.facts).toEqual([
      'Штраф: 2 + 3 = 5',
      ru.effects.drawEvent('bad', 'drop'),
      '−5 очков',
      '−3 клетки',
    ]);
  });

  it('leads a reroll with the new game and adds how it was paid', () => {
    const item = one([
      entry('c3', 'game-rolled', {
        playerId: vasya,
        category: 'Метроидвании',
        misses: [{ gameId: celeste, reason: 'beingPlayed', byPlayerId: petya }],
        gameId: hollow,
        sectors: [],
      }),
      entry('c3', 'coins-changed', { playerId: vasya, delta: -2, reason: 'reroll', runId: null }),
      entry('c3', 'game-rerolled', { playerId: vasya, gameIds: [celeste], payment: 'coins' }),
    ]);

    expect(text(item)).toBe('Вася выкручивает Hollow Knight');
    expect(item?.facts).toEqual([
      'Категория: Метроидвании',
      '1 промах колеса',
      'Реролл за монетки',
      '−2 монетки',
    ]);
  });

  it('quotes a review with its rating', () => {
    const item = one([
      entry('c4', 'run-reviewed', {
        runId: run1,
        playerId: vasya,
        rating: 9,
        text: 'Лучшая метроидвания',
      }),
    ]);

    expect(text(item)).toBe('Вася оценивает Hollow Knight');
    expect(item?.quote).toEqual({ rating: 9, text: 'Лучшая метроидвания' });
  });

  it('shows a proof without its hidden details and a reject with the admin comment when it is there', () => {
    const sent = one([entry('c5', 'proof-submitted', { runId: run1, playerId: vasya })]);
    const rejected = one([
      entry('c6', 'points-changed', {
        playerId: vasya,
        delta: -7,
        reason: 'proofRejected',
        runId: run1,
      }),
      entry('c6', 'proof-rejected', { runId: run1, playerId: vasya, comment: 'Скрин не тот' }),
    ]);

    expect(text(sent)).toBe('Вася отправляет пруф по Hollow Knight');
    expect(sent?.quote).toBeNull();
    expect(text(rejected)).toBe('Вася: прохождение Hollow Knight отклонено');
    expect(rejected?.quote).toEqual({ rating: null, text: 'Скрин не тот' });
    expect(rejected?.facts).toEqual(['−7 очков']);
  });

  it('says a reject with the drop penalty with its dice (D-327)', () => {
    const item = one([
      entry('c7', 'proof-rejected', { runId: run1, playerId: vasya, comment: 'Очевидный обман' }),
      entry('c7', 'proof-reject-penalized', {
        runId: run1,
        playerId: vasya,
        penaltyDice: [
          { sides: 4, value: 1 },
          { sides: 4, value: 3 },
        ],
      }),
    ]);

    expect(text(item)).toBe('Вася: прохождение Hollow Knight отклонено со штрафом дропа');
    expect(item?.facts).toContain('Штраф: 1 + 3 = 4');
  });

  it('keeps an undone command, marked, and says the undo with its reason', () => {
    const days = feedDays(
      [
        entry('undo', 'command-undone', { commandId: 'c7', comment: 'Ошибся кнопкой' }),
        entry(
          'c7',
          'game-rolled',
          { playerId: petya, category: 'Инди', misses: [], gameId: celeste },
          { undone: true },
        ),
      ],
      refs,
      now,
    );

    const [undo, rolled] = days[0]?.items ?? [];
    expect(text(undo)).toBe(ru.feed.lines.undone().join(''));
    expect(undo?.quote?.text).toBe('Ошибся кнопкой');
    expect(undo?.undone).toBe(false);
    expect(rolled?.undone).toBe(true);
    // A deleted game keeps its title without a link
    expect(refsOf(rolled).find((r) => r.kind === 'game')).toEqual({
      kind: 'game',
      game: { id: celeste, title: 'Celeste', hasPage: false },
    });
  });

  it('names a player or a game the page does not know in neutral words instead of an id', () => {
    const item = one([
      entry('c8', 'run-started', { runId: 'r', playerId: 'nobody', gameId: 'unknown' }),
    ]);

    expect(text(item)).toBe(`${ru.feed.someone} начинает ${ru.feed.someGame}`);
    expect(
      refsOf(item).every((r) => (r.kind === 'player' ? !r.player.hasProfile : !r.game.hasPage)),
    ).toBe(true);
  });

  it('says the season’s own events without a player', () => {
    const lines = feedDays(
      [
        entry('s3', 'season-deadline-set', { deadline: null }),
        entry('s2', 'season-status-changed', { from: 'draft', to: 'active' }),
        entry('s1', 'season-created', { seasonId: 's', name: 'Осень' }),
      ],
      refs,
      now,
    )[0]?.items.map((item) => [text(item), item.actor]);

    expect(lines).toEqual([
      ['Дедлайн сезона снят', null],
      ['Сезон начался! Крути колесо', null],
      ['Сезон «Осень» создан', null],
    ]);
  });

  it('turns the admin’s change of a player into a line with the reason and what changed', () => {
    const item = one([
      entry('c9', 'coins-changed', { playerId: petya, delta: 3, reason: 'adminAdjustment' }),
      entry('c9', 'player-moved', {
        playerId: petya,
        from: 'a',
        to: 'x',
        steps: 0,
        path: ['x'],
        reason: 'adminAdjustment',
      }),
      entry('c9', 'player-adjusted', { playerId: petya, comment: 'Компенсация за баг' }),
    ]);

    expect(text(item)).toBe('Петя: правка админа');
    expect(item?.icon).toBe('admin');
    expect(item?.quote?.text).toBe('Компенсация за баг');
    expect(item?.facts).toEqual([ru.feed.facts.moved, '+3 монетки']);
  });

  it('says why points or coins changed when that is the whole line', () => {
    const reward = one([
      entry('c11', 'coins-changed', {
        playerId: vasya,
        delta: 6,
        reason: 'completionReward',
        runId: run1,
      }),
    ]);
    const bonus = one([
      entry('c12', 'points-changed', {
        playerId: petya,
        delta: 5,
        reason: 'finishBonus',
        runId: null,
      }),
    ]);

    expect(text(reward)).toBe('Вася: +6 монеток за прохождение');
    expect(text(bonus)).toBe('Петя: +5 очков — бонус за финиш');
  });

  it('leaves out a command with nothing a line would say', () => {
    expect(
      feedDays([entry('c10', 'finish-surplus-changed', { playerId: vasya, delta: 1 })], refs, now),
    ).toEqual([]);
  });

  it('groups the lines by the Moscow day: today, yesterday, then the date', () => {
    const days = feedDays(
      [
        entry(
          'd1',
          'run-started',
          { runId: run1, playerId: vasya, gameId: hollow },
          { at: '2026-09-26T10:00:00Z' },
        ),
        // 23:30 in Moscow on the 25th is still yesterday there
        entry(
          'd2',
          'run-started',
          { runId: run1, playerId: vasya, gameId: hollow },
          { at: '2026-09-25T20:30:00Z' },
        ),
        entry(
          'd3',
          'run-started',
          { runId: run1, playerId: vasya, gameId: hollow },
          { at: '2026-09-25T06:00:00Z' },
        ),
        entry(
          'd4',
          'run-started',
          { runId: run1, playerId: vasya, gameId: hollow },
          { at: '2026-09-20T12:00:00Z' },
        ),
        entry(
          'd5',
          'run-started',
          { runId: run1, playerId: vasya, gameId: hollow },
          { at: '2025-12-31T12:00:00Z' },
        ),
      ],
      refs,
      now,
    );

    expect(days.map((d) => [d.label, d.items.map((i) => i.id)])).toEqual([
      [ru.feed.today, ['d1']],
      [ru.feed.yesterday, ['d2', 'd3']],
      ['20 сентября', ['d4']],
      ['31 декабря 2025 г.', ['d5']],
    ]);
  });
});

describe('feed lines of the graph map (D-319)', () => {
  it('says a branch was chosen, with the teleport the move ended on', () => {
    const item = one([
      entry('m1', 'player-moved', {
        playerId: vasya,
        from: 't',
        to: 'a2',
        steps: 0,
        path: ['a2'],
        reason: 'teleport',
        runId: null,
      }),
      entry('m1', 'player-moved', {
        playerId: vasya,
        from: 'f',
        to: 't',
        steps: 2,
        path: ['b1', 't'],
        reason: 'completionRoll',
        runId: run1,
      }),
      entry('m1', 'choice-made', { playerId: vasya, choiceId: 'c', optionId: 'b1' }),
    ]);
    expect(text(item)).toBe(ru.feed.lines.choseBranch<string>('Вася').join(''));
    expect(item?.facts).toContain(ru.feed.facts.teleport);
    expect(item?.facts).toContain(ru.feed.facts.cells(2));
  });

  it('keeps «выбирает игру» for a choice of games', () => {
    const item = one([
      entry('g1', 'choice-made', { playerId: vasya, choiceId: 'c', optionId: 'x' }),
    ]);
    expect(text(item)).toBe(ru.feed.lines.chose<string>('Вася').join(''));
  });

  it("says the map was published, with the admin's comment", () => {
    const item = one([entry('p1', 'map-published', { map: {}, comment: 'Добавили болото' })]);
    expect(text(item)).toBe(ru.feed.lines.mapPublished<string>().join(''));
    expect(item?.quote?.text).toBe('Добавили болото');
  });

  it('says a completion stopped at a fork to wait for the branch', () => {
    const item = one([
      entry('w1', 'branch-choice-requested', {
        playerId: vasya,
        choiceId: 'c',
        cellId: 'f',
        options: ['a1', 'b1'],
        steps: 2,
        reason: 'completionRoll',
        runId: run1,
      }),
      entry('w1', 'player-moved', {
        playerId: vasya,
        from: 'start',
        to: 'f',
        steps: 3,
        path: ['f'],
        reason: 'completionRoll',
        runId: run1,
        paused: true,
      }),
      entry('w1', 'run-completed', {
        runId: run1,
        playerId: vasya,
        difficulty: 'normal',
        hours: 5,
      }),
    ]);
    expect(item?.facts).toContain(ru.feed.facts.branchWaiting);
  });
});

describe('feed pages', () => {
  it('gives each player the token of their place in the season’s list, as on the map', () => {
    expect([...refs.players.values()].map((p) => [p.name, p.token, p.avatar])).toEqual([
      ['Петя', 0, undefined],
      ['Вася', 1, '/t'],
    ]);
  });

  it('adds the games and runs of a later page to those already known', () => {
    const later = withRefs(refs, {
      players: [],
      games: [{ id: 'g2', title: 'Hades', hasPage: true }],
      runs: [{ id: 'r2', gameId: 'g2' }],
    });

    expect(later.players).toBe(refs.players);
    expect([...later.games.keys()]).toEqual([hollow, celeste, 'g2']);
    expect(later.runs.get(run1)).toBe(hollow);
    expect(later.runs.get('r2')).toBe('g2');
  });

  it('merges pages newest first, a fresh copy of an entry replacing the old one', () => {
    const a = { ...entry('x', 'run-started', {}), sequence: 3 };
    const b = { ...entry('y', 'run-started', {}), sequence: 2 };
    const c = { ...entry('z', 'run-started', {}), sequence: 1 };
    const undone = { ...b, undone: true };

    expect(mergeEntries([b, c], [a, undone]).map((e) => [e.sequence, e.undone])).toEqual([
      [3, false],
      [2, true],
      [1, false],
    ]);
  });

  it('puts the cover of a completed game beside its line, and none without a cover or on other lines (D-222)', () => {
    const covered = withRefs(refs, {
      players: [],
      games: [
        {
          id: hollow,
          title: 'Hollow Knight',
          hasPage: true,
          cover: { id: 'f1', url: '/api/files/f1', thumbnailUrl: '/api/files/f1/thumbnail' },
        },
      ],
      runs: [],
    });
    const completed = (game: string) =>
      feedDays(
        [
          entry('c9', 'run-completed', {
            runId: run1,
            gameId: game,
            playerId: vasya,
            difficulty: 'hard',
            hours: 3,
          }),
        ],
        covered,
        now,
      )[0]?.items[0];

    expect(completed(hollow)?.cover).toEqual({
      id: hollow,
      title: 'Hollow Knight',
      hasPage: true,
      cover: '/api/files/f1/thumbnail',
    });
    expect(completed(celeste)?.cover).toBeNull();
    const started = feedDays(
      [entry('c10', 'run-started', { runId: run1, gameId: hollow, playerId: vasya })],
      covered,
      now,
    )[0]?.items[0];
    expect(started?.cover).toBeNull();
  });
});
