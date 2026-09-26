import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';

// The hub is replaced: tests trigger "another player acted" by calling the captured callback.
let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: () => void) => {
    hubChange = onChange;
    return () => {
      hubChange = null;
    };
  },
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';

// Cells to the finish on the fixture map start → c1 → finish (the server computes them, D-100)
const cellsToFinish: Record<string, number> = { start: 2, c1: 1, finish: 0 };

/** The server's leaderboard for fixture players already listed in place order (one player per place). */
function leaderboardOf(players: Schemas['PlayerView'][]): Schemas['LeaderboardRowView'][] {
  return players.map((p, i) => ({
    playerId: p.id,
    place: i + 1,
    points: p.points,
    cellsToFinish: cellsToFinish[p.cellId] ?? null,
    isFirst: false,
    provisional: false,
  }));
}

function season(overrides: Partial<Schemas['SeasonView']> = {}): Schemas['SeasonView'] {
  const players = overrides.players ?? [
    {
      id: me,
      name: 'Вася',
      cellId: 'start',
      points: 0,
      phase: 'idle',
      finishOrder: null,
      avatar: null,
    },
  ];
  return {
    id: seasonId,
    // D-101: the season's status and deadline (none here)
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    players,
    me: {
      playerId: me,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      roll: null,
      unchecked: null,
      finish: null,
    },
    lastSequence: 3,
    name: 'Тестовый сезон',
    ...overrides,
    leaderboard: overrides.leaderboard ?? leaderboardOf(players),
  };
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

type Handler = (request: Request) => Response | Promise<Response>;

// The avatar section (D-117) asks for the account itself; the cases here are about the season, so it has none
const isAccountGet = (r: Request) => r.method === 'GET' && r.url.endsWith('/api/auth/me');
const account = {
  id: 'u',
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};

function serve(handler: Handler) {
  const fetch = vi.fn((request: Request) =>
    Promise.resolve(isAccountGet(request) ? json(200, account) : handler(request)),
  );
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

const isSeasonGet = (r: Request) =>
  r.method === 'GET' && r.url.endsWith(`/api/seasons/${seasonId}`);

describe('SeasonScreen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows a rule rejection in Russian', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, season())
        : json(409, {
            title: 'rejected',
            status: 409,
            detail: null,
            code: 'roll.noAvailableGames',
          }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('roll'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.rejection['roll.noAvailableGames'],
    );
  });

  it('disables the action while its command runs and sends a new command id each time', async () => {
    let release: (() => void) | null = null;
    const bodies: { commandId: string }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, season());
      bodies.push((await r.json()) as { commandId: string });
      await new Promise<void>((resolve) => (release = resolve));
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const roll = await screen.findByTestId('roll');
    await userEvent.click(roll);
    expect(roll).toBeDisabled();

    await act(async () => {
      release?.();
      await Promise.resolve();
    });
    await vi.waitFor(() => {
      expect(screen.getByTestId('roll')).toBeEnabled();
    });
    await userEvent.click(screen.getByTestId('roll'));
    await act(async () => {
      release?.();
      await Promise.resolve();
    });

    expect(bodies).toHaveLength(2);
    expect(bodies[0]?.commandId).not.toBe(bodies[1]?.commandId);
  });

  it('refetches when another player acts', async () => {
    let current = season();
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');

    current = season({
      lastSequence: 7,
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'c1',
          points: 4,
          phase: 'idle',
          finishOrder: null,
          avatar: null,
        },
      ],
    });
    act(() => {
      hubChange?.();
    });

    expect(await screen.findByTestId(`leader-${me}`)).toHaveTextContent(
      ru.leaderboard.row(1, 'Вася', 4, 1),
    );
    expect(screen.getByTestId('cell-c1')).toHaveTextContent('Вася');
  });

  it('ignores an answer older than what it already shows', async () => {
    let current = season({
      lastSequence: 9,
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'c1',
          points: 4,
          phase: 'idle',
          finishOrder: null,
          avatar: null,
        },
      ],
    });
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');
    expect(screen.getByTestId('cell-c1')).toHaveTextContent('Вася');

    current = season({ lastSequence: 5 }); // a late answer from before the move
    act(() => {
      hubChange?.();
    });
    await act(() => Promise.resolve());

    expect(screen.getByTestId('cell-c1')).toHaveTextContent('Вася');
  });

  it('shows the pending choice and sends the picked option', async () => {
    const choiceId = 'c0000000-0000-0000-0000-000000000001';
    const game = (id: string, title: string) => ({ id, title, hours: 12, marks: [] });
    const options = [
      { id: 'a1', game: game('a1000000-0000-0000-0000-000000000001', 'Silent Hill') },
      { id: 'b2', game: game('b2000000-0000-0000-0000-000000000002', 'Outlast') },
    ];
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) {
        return json(
          200,
          season({
            me: {
              playerId: me,
              phase: 'rolling',
              offer: null,
              choice: { id: choiceId, kind: 'game', options },
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
              dropHintMinutes: null,
              dropPenalty: null,
              techRerollOpen: false,
              challengesEnabled: false,
              roll: null,
              unchecked: null,
              finish: null,
            },
          }),
        );
      }
      bodies.push({ url: r.url, body: (await r.json()) as unknown });
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('choice')).toHaveTextContent(ru.turn.choose);
    expect(screen.queryByTestId('start')).not.toBeInTheDocument();
    // H3: the option card names the game and its hours the way the run card does
    const option = screen.getByTestId('option-a1');
    expect(option).toHaveTextContent('Silent Hill');
    expect(option).toHaveTextContent(ru.board.hours(12));
    // The card's name is its content: the action, the game and its hours
    expect(option).toHaveAccessibleName(`${ru.turn.pick} Silent Hill. ${ru.board.hours(12)}`);

    await userEvent.click(screen.getByTestId('option-b2'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({
      url: expect.stringMatching(/\/choose$/) as unknown,
      body: { choiceId, optionId: 'b2', commandId: expect.any(String) as unknown },
    });
  });

  it('explains a choice already made in another tab and refetches the season', async () => {
    const choiceId = 'c0000000-0000-0000-0000-000000000001';
    const picked = {
      id: 'a1000000-0000-0000-0000-000000000001',
      title: 'Silent Hill',
      hours: 12,
      marks: [],
    };
    const choosing = season({
      me: {
        playerId: me,
        phase: 'rolling',
        offer: null,
        choice: {
          id: choiceId,
          kind: 'game',
          options: [
            { id: 'a1', game: picked },
            {
              id: 'b2',
              game: {
                id: 'b2000000-0000-0000-0000-000000000002',
                title: 'Outlast',
                hours: 9,
                marks: [],
              },
            },
          ],
        },
        activeRun: null,
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
    // The other tab already chose: the server now has Вася playing, and answers this tab's choice with 409
    const playing = season({
      lastSequence: 5,
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'start',
          points: 0,
          phase: 'playing',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'playing',
        offer: null,
        choice: null,
        activeRun: {
          id: 'e0000000-0000-0000-0000-000000000001',
          game: picked,
          startedAt: '2026-09-24T10:00:00Z',
        },
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
    let current = choosing;
    let seasonGets = 0;
    serve((r) => {
      if (isSeasonGet(r)) {
        seasonGets++;
        return json(200, current);
      }
      current = playing;
      return json(409, {
        title: 'rejected',
        status: 409,
        detail: null,
        code: 'turn.noPendingChoice',
      });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('choice');
    const getsBefore = seasonGets;

    await userEvent.click(screen.getByTestId('option-b2'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.rejection['turn.noPendingChoice'],
    );
    await vi.waitFor(() => {
      expect(seasonGets).toBeGreaterThan(getsBefore);
    });
    await vi.waitFor(() => {
      expect(screen.queryByTestId('choice')).not.toBeInTheDocument();
    });
  });

  it('declares an offered game already played', async () => {
    const gameId = 'a1000000-0000-0000-0000-000000000001';
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) {
        return json(
          200,
          season({
            me: {
              playerId: me,
              phase: 'rolling',
              offer: { id: gameId, title: 'Silent Hill', hours: 12, marks: [] },
              choice: null,
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
              dropHintMinutes: null,
              dropPenalty: null,
              techRerollOpen: false,
              challengesEnabled: false,
              roll: null,
              unchecked: null,
              finish: null,
            },
          }),
        );
      }
      bodies.push({ url: r.url, body: (await r.json()) as unknown });
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('already-played'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({
      url: expect.stringMatching(/\/already-played$/) as unknown,
      body: { gameId, commandId: expect.any(String) as unknown },
    });
  });

  it('declares an option of the pending choice already played by its game id, not the option id', async () => {
    const choiceId = 'c0000000-0000-0000-0000-000000000001';
    // Option ids and game ids differ on purpose: the command takes the game (D-92)
    const options = [
      {
        id: 'opt-1',
        game: {
          id: 'a1000000-0000-0000-0000-000000000001',
          title: 'Silent Hill',
          hours: 12,
          marks: [],
        },
      },
      {
        id: 'opt-2',
        game: {
          id: 'b2000000-0000-0000-0000-000000000002',
          title: 'Outlast',
          hours: 9,
          marks: [],
        },
      },
    ];
    const bodies: { url: string; body: unknown }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) {
        return json(
          200,
          season({
            me: {
              playerId: me,
              phase: 'rolling',
              offer: null,
              choice: { id: choiceId, kind: 'game', options },
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
              dropHintMinutes: null,
              dropPenalty: null,
              techRerollOpen: false,
              challengesEnabled: false,
              roll: null,
              unchecked: null,
              finish: null,
            },
          }),
        );
      }
      bodies.push({ url: r.url, body: (await r.json()) as unknown });
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('already-played-opt-2'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({
      url: expect.stringMatching(new RegExp(`/api/seasons/${seasonId}/already-played$`)) as unknown,
      body: {
        gameId: 'b2000000-0000-0000-0000-000000000002',
        commandId: expect.any(String) as unknown,
      },
    });
    expect(bodies[0]?.body).not.toMatchObject({ gameId: 'opt-2' });
  });

  it('rerolls an offered game with a new command id', async () => {
    const bodies: { url: string; body: { commandId?: string } }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) {
        return json(
          200,
          season({
            me: {
              playerId: me,
              phase: 'rolling',
              offer: {
                id: 'a1000000-0000-0000-0000-000000000001',
                title: 'Silent Hill',
                hours: 12,
                marks: [],
              },
              choice: null,
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
              dropHintMinutes: null,
              dropPenalty: null,
              techRerollOpen: false,
              challengesEnabled: false,
              roll: null,
              unchecked: null,
              finish: null,
            },
          }),
        );
      }
      bodies.push({ url: r.url, body: (await r.json()) as { commandId?: string } });
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));
    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    await vi.waitFor(() => {
      expect(screen.getByTestId('reroll')).toBeEnabled();
    });
    await userEvent.click(screen.getByTestId('reroll'));
    await vi.waitFor(() => {
      expect(bodies).toHaveLength(2);
    });

    // The whole offer is given up: the body names no game, only the command
    expect(bodies[0]).toMatchObject({
      url: expect.stringMatching(new RegExp(`/api/seasons/${seasonId}/reroll$`)) as unknown,
      body: { commandId: expect.any(String) as unknown },
    });
    expect(bodies[0]?.body.commandId).not.toBe(bodies[1]?.body.commandId);
  });

  it('rerolls a pending choice as a whole', async () => {
    const choiceId = 'c0000000-0000-0000-0000-000000000001';
    const options = [
      {
        id: 'opt-1',
        game: {
          id: 'a1000000-0000-0000-0000-000000000001',
          title: 'Silent Hill',
          hours: 12,
          marks: [],
        },
      },
      {
        id: 'opt-2',
        game: {
          id: 'b2000000-0000-0000-0000-000000000002',
          title: 'Outlast',
          hours: 9,
          marks: [],
        },
      },
    ];
    const bodies: { url: string; body: unknown }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) {
        return json(
          200,
          season({
            me: {
              playerId: me,
              phase: 'rolling',
              offer: null,
              choice: { id: choiceId, kind: 'game', options },
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
              dropHintMinutes: null,
              dropPenalty: null,
              techRerollOpen: false,
              challengesEnabled: false,
              roll: null,
              unchecked: null,
              finish: null,
            },
          }),
        );
      }
      bodies.push({ url: r.url, body: (await r.json()) as unknown });
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    // One reroll button for the whole choice (D-93), next to the options
    await screen.findByTestId('choice');
    expect(screen.getAllByTestId('reroll')).toHaveLength(1);
    await userEvent.click(screen.getByTestId('reroll'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({
      url: expect.stringMatching(new RegExp(`/api/seasons/${seasonId}/reroll$`)) as unknown,
      body: { commandId: expect.any(String) as unknown },
    });
  });

  it('offers no reroll while idle or playing', async () => {
    let current = season();
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');
    expect(screen.queryByTestId('reroll')).not.toBeInTheDocument();

    current = season({
      lastSequence: 5,
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'start',
          points: 0,
          phase: 'playing',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'playing',
        offer: null,
        choice: null,
        activeRun: {
          id: 'e0000000-0000-0000-0000-000000000001',
          game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
          startedAt: '2026-09-24T10:00:00Z',
        },
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
    act(() => {
      hubChange?.();
    });

    expect(await screen.findAllByText(/Silent Hill/)).not.toHaveLength(0);
    expect(screen.queryByTestId('reroll')).not.toBeInTheDocument();
  });

  it('explains a reroll refused for lack of coins in Russian', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(
            200,
            season({
              me: {
                playerId: me,
                phase: 'rolling',
                offer: {
                  id: 'a1000000-0000-0000-0000-000000000001',
                  title: 'Silent Hill',
                  hours: 12,
                  marks: [],
                },
                choice: null,
                activeRun: null,
                lastCompleted: null,
                nextReroll: null,
                manualEffects: [],
                dropHintMinutes: null,
                dropPenalty: null,
                techRerollOpen: false,
                challengesEnabled: false,
                roll: null,
                unchecked: null,
                finish: null,
              },
            }),
          )
        : json(409, {
            title: 'rejected',
            status: 409,
            detail: null,
            code: 'roll.notEnoughCoins',
          }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));

    // The dictionary has its own text for the code, not the generic «unknown» one
    const text = ru.rejection['roll.notEnoughCoins'];
    expect(text).toEqual(expect.any(String));
    expect(text).not.toBe(ru.rejection.unknown);
    expect(await screen.findByRole('alert')).toHaveTextContent(text);
  });

  it('shows a spectator no actions', async () => {
    serve(() => json(200, season({ me: null })));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByText(ru.turn.spectator)).toBeInTheDocument();
    expect(screen.queryByTestId('roll')).not.toBeInTheDocument();
  });

  it.each<[string, Partial<Schemas['SeasonView']>, string]>([
    ['a spectator', { me: null }, ru.turn.spectatorTitle],
    ['a finished season', { status: 'finished' }, ru.turn.finishedTitle],
    ['a closing season', { status: 'closing' }, ru.turn.closingTitle],
    ['an open turn', {}, ru.turn.title],
  ])('names the turn card by the state: %s (H2)', async (_, overrides, title) => {
    serve(() => json(200, season(overrides)));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const turn = await screen.findByTestId('turn');
    expect(within(turn).getByRole('heading', { level: 2 })).toHaveTextContent(title);
  });

  it('returns to sign-in when the session is gone', async () => {
    serve(() => json(401, {}));
    const onSignedOut = vi.fn();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={onSignedOut} />);

    await vi.waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });

  it('says so when the network fails instead of hanging', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.reject(new TypeError('network down'))),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.shell.loadErrorTitle);
  });
});

describe('SeasonScreen reroll price and manual effects (D-93)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  type Price = Schemas['RerollPriceView'];

  function rolling(nextReroll: Price | null, manualEffects: Schemas['ManualEffectView'][] = []) {
    return season({
      me: {
        playerId: me,
        phase: 'rolling',
        offer: {
          id: 'a1000000-0000-0000-0000-000000000001',
          title: 'Silent Hill',
          hours: 12,
          marks: [],
        },
        choice: null,
        activeRun: null,
        lastCompleted: null,
        nextReroll,
        manualEffects,
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
  }

  /** Serves the season and records every command request. */
  function serveRolling(view: Schemas['SeasonView']) {
    const commands: string[] = [];
    serve((r) => {
      if (isSeasonGet(r)) return json(200, view);
      // Reads (the account for the avatar block, D-117) are not commands
      if (r.method === 'GET') return json(404, {});
      commands.push(r.url);
      return json(200, { duplicate: false, events: [] });
    });
    return commands;
  }

  const rerollUrl = new RegExp(`/api/seasons/${seasonId}/reroll$`);

  it.each<Price>([
    { payment: 'freeThisRoll', coins: 0 },
    { payment: 'freeRerollResource', coins: 0 },
    { payment: 'coins', coins: 5 },
    { payment: 'coins', coins: 0 },
    { payment: 'badEvent', coins: 0 },
  ])('shows the price of the next reroll on the button: %o', async (price) => {
    serveRolling(rolling(price));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('reroll')).toHaveTextContent(
      ru.turn.rerollFor(price.payment, price.coins),
    );
  });

  it.each<Price>([
    { payment: 'freeThisRoll', coins: 0 },
    { payment: 'freeRerollResource', coins: 0 },
    { payment: 'coins', coins: 0 },
  ])('sends a free reroll at once without asking: %o', async (price) => {
    const commands = serveRolling(rolling(price));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]).toMatch(rerollUrl);
    expect(screen.queryByTestId('reroll-confirm')).not.toBeInTheDocument();
  });

  it.each<Price>([
    { payment: 'coins', coins: 5 },
    { payment: 'badEvent', coins: 0 },
  ])('asks before a paid reroll and sends it only when confirmed: %o', async (price) => {
    const commands = serveRolling(rolling(price));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));

    // The confirmation names the price; nothing is sent yet
    expect(await screen.findByTestId('reroll-confirm')).toHaveTextContent(
      ru.turn.rerollConfirm(price.payment, price.coins),
    );
    expect(commands).toHaveLength(0);

    await userEvent.click(screen.getByTestId('reroll-confirm-yes'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]).toMatch(rerollUrl);
  });

  it.each<Price>([
    { payment: 'coins', coins: 5 },
    { payment: 'badEvent', coins: 0 },
  ])('cancels a paid reroll without a request: %o', async (price) => {
    const commands = serveRolling(rolling(price));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));
    await userEvent.click(await screen.findByTestId('reroll-confirm-no'));

    expect(screen.queryByTestId('reroll-confirm')).not.toBeInTheDocument();
    expect(await screen.findByTestId('reroll')).toBeEnabled();
    await act(async () => {
      await Promise.resolve();
    });
    expect(commands).toHaveLength(0);
  });

  it('moves the focus into the reroll confirmation and back to the reroll on cancel (H3)', async () => {
    serveRolling(rolling({ payment: 'coins', coins: 5 }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));
    await vi.waitFor(() => {
      expect(screen.getByText(ru.turn.rerollConfirm('coins', 5))).toHaveFocus();
    });
    await userEvent.click(screen.getByTestId('reroll-confirm-no'));

    await vi.waitFor(() => {
      expect(screen.getByTestId('reroll')).toHaveFocus();
    });

    // Escape cancels it too
    await userEvent.click(screen.getByTestId('reroll'));
    await vi.waitFor(() => {
      expect(screen.getByText(ru.turn.rerollConfirm('coins', 5))).toHaveFocus();
    });
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByTestId('reroll-confirm')).not.toBeInTheDocument();
    await vi.waitFor(() => {
      expect(screen.getByTestId('reroll')).toHaveFocus();
    });
  });

  it('lists pending manual effects of the player', async () => {
    const effects: Schemas['ManualEffectView'][] = [
      { id: 'e1000000-0000-0000-0000-000000000001', drawEvent: 'bad', source: 'paidReroll' },
      { id: 'e1000000-0000-0000-0000-000000000002', drawEvent: 'bad', source: 'paidReroll' },
    ];
    serveRolling(rolling({ payment: 'badEvent', coins: 0 }, effects));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const list = await screen.findByTestId('manual-effects');
    expect(list).toHaveTextContent(ru.effects.title);
    for (const effect of effects) {
      expect(screen.getByTestId(`manual-effect-${effect.id}`)).toHaveTextContent(
        ru.effects.drawEvent('bad', 'paidReroll'),
      );
    }
  });

  it('puts pending effects in the turn card above the roll button, with their count (H2)', async () => {
    const effects: Schemas['ManualEffectView'][] = [
      { id: 'e1000000-0000-0000-0000-000000000001', drawEvent: 'bad', source: 'drop' },
    ];
    const idle = season();
    if (!idle.me) throw new Error('the fixture has a player');
    serve((r) =>
      isSeasonGet(r)
        ? json(200, { ...idle, me: { ...idle.me, manualEffects: effects } })
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const turn = await screen.findByTestId('turn');
    const todo = within(turn).getByTestId('after');
    expect(within(todo).getByRole('heading', { name: ru.turn.todo(1) })).toBeInTheDocument();
    expect(within(todo).getByTestId('manual-effects')).toBeInTheDocument();
    // Before a new roll, the to-do comes first
    const roll = within(turn).getByTestId('roll');
    expect(todo.compareDocumentPosition(roll) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('puts the rolled game above the pending effects while the roll waits for an answer (H3, D-137)', async () => {
    const effects: Schemas['ManualEffectView'][] = [
      { id: 'e1000000-0000-0000-0000-000000000001', drawEvent: 'bad', source: 'paidReroll' },
    ];
    serveRolling(rolling({ payment: 'badEvent', coins: 0 }, effects));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const turn = await screen.findByTestId('turn');
    const todo = within(turn).getByTestId('after');
    expect(within(todo).getByRole('heading', { name: ru.turn.todo(1) })).toBeInTheDocument();
    // The answer to the roll is the one main action: it stands first, the tails of the last run below it
    const reroll = within(turn).getByTestId('reroll');
    expect(todo.compareDocumentPosition(reroll) & Node.DOCUMENT_POSITION_PRECEDING).toBeTruthy();
  });

  it('shows no manual effects section when there are none', async () => {
    serveRolling(rolling({ payment: 'freeThisRoll', coins: 0 }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('reroll');
    expect(screen.queryByTestId('manual-effects')).not.toBeInTheDocument();
  });
});

describe('SeasonScreen drop and tech reroll (RR2, RR4, RR5, D-94)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const runId = 'e0000000-0000-0000-0000-000000000001';
  const dropUrl = new RegExp(`/api/seasons/${seasonId}/drop$`);
  const techRerollUrl = new RegExp(`/api/seasons/${seasonId}/tech-reroll$`);
  const reasons = [
    'weakPc',
    'paidUnavailable',
    'doesNotLaunch',
    'emulatorTooSlow',
    'other',
  ] as const;
  type Reason = (typeof reasons)[number];

  type Penalty = Schemas['DropPenaltyView'];
  /** The default ruleset's penalty: 2d4 on points and position and a bad event. */
  const defaultPenalty: Penalty = {
    count: 2,
    sides: 4,
    affectsPoints: true,
    affectsPosition: true,
    badEvent: true,
  };

  /**
   * Вася playing Silent Hill. The server decides everything the drop and tech reroll buttons need (D-94 (5)):
   * `dropHintMinutes` (non-null only while less than the minimum is played), `dropPenalty`, `techRerollOpen`.
   * The start time is far in the past on purpose: the screen must not compute the hint from it.
   */
  function playing(
    turn: {
      dropHintMinutes?: number | null;
      dropPenalty?: Penalty | null;
      techRerollOpen?: boolean;
      startedAt?: string;
    } = {},
  ): Schemas['SeasonView'] {
    return season({
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'start',
          points: 0,
          phase: 'playing',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'playing',
        offer: null,
        choice: null,
        activeRun: {
          id: runId,
          game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
          startedAt: turn.startedAt ?? '2026-09-24T10:00:00Z',
        },
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
        dropHintMinutes: turn.dropHintMinutes === undefined ? null : turn.dropHintMinutes,
        dropPenalty: turn.dropPenalty === undefined ? defaultPenalty : turn.dropPenalty,
        techRerollOpen: turn.techRerollOpen ?? true,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
  }

  /** Serves the season and records every command request with its body. */
  function servePlaying(view: Schemas['SeasonView']) {
    const commands: { url: string; body: Record<string, unknown> }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, view);
      commands.push({ url: r.url, body: (await r.json()) as Record<string, unknown> });
      return json(200, { duplicate: false, events: [] });
    });
    return commands;
  }

  it('offers drop and tech reroll only while playing', async () => {
    let current = season();
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');
    expect(screen.queryByTestId('drop')).not.toBeInTheDocument();
    expect(screen.queryByTestId('tech-reroll')).not.toBeInTheDocument();

    current = { ...playing(), lastSequence: 9 };
    act(() => {
      hubChange?.();
    });

    expect(await screen.findByTestId('drop')).toHaveTextContent(ru.turn.drop);
    expect(screen.getByTestId('tech-reroll')).toHaveTextContent(ru.turn.techReroll);
    // Completing stays available next to them
    expect(screen.getByTestId('complete-form')).toBeInTheDocument();
  });

  it('offers no drop or tech reroll for an offered game', async () => {
    servePlaying(
      season({
        me: {
          playerId: me,
          phase: 'rolling',
          offer: {
            id: 'a1000000-0000-0000-0000-000000000001',
            title: 'Silent Hill',
            hours: 12,
            marks: [],
          },
          choice: null,
          activeRun: null,
          lastCompleted: null,
          nextReroll: { payment: 'freeThisRoll', coins: 0 },
          manualEffects: [],
          dropHintMinutes: null,
          dropPenalty: null,
          techRerollOpen: false,
          challengesEnabled: false,
          roll: null,
          unchecked: null,
          finish: null,
        },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('start');
    expect(screen.queryByTestId('drop')).not.toBeInTheDocument();
    expect(screen.queryByTestId('tech-reroll')).not.toBeInTheDocument();
  });

  it('asks before a drop, naming the penalty and the bad event, and sends it when confirmed', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    // H4: a window names the game and lists every consequence, one per line
    const dialog = await screen.findByTestId('drop-confirm');
    expect(dialog).toHaveTextContent(ru.turn.dropTitle('Silent Hill'));
    const consequences = ru.turn.dropConsequences(defaultPenalty);
    expect(
      within(dialog)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(consequences);
    const text = consequences.join(' ');
    expect(text).toMatch(/штраф/i);
    expect(text).toContain('2d4');
    expect(text).toMatch(/очки/);
    expect(text).toMatch(/клетки/);
    expect(text).toMatch(/плох\S* ивент/i);
    expect(commands).toHaveLength(0);

    await userEvent.click(screen.getByTestId('drop-confirm-yes'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]?.url).toMatch(dropUrl);
    expect(commands[0]?.body).toMatchObject({ commandId: expect.any(String) as unknown });
  });

  it('cancels a drop without a request', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));
    await userEvent.click(await screen.findByTestId('drop-confirm-no'));

    expect(screen.queryByTestId('drop-confirm')).not.toBeInTheDocument();
    expect(await screen.findByTestId('drop')).toBeEnabled();
    await act(async () => {
      await Promise.resolve();
    });
    expect(commands).toHaveLength(0);
  });

  it.each<[string, Penalty, RegExp[], RegExp[]]>([
    [
      '1d6 on points only, no bad event',
      { count: 1, sides: 6, affectsPoints: true, affectsPosition: false, badEvent: false },
      [/1d6/, /очки/],
      [/клетки/, /плох\S* ивент/i, /штрафа кубами нет/i],
    ],
    [
      'no dice penalty but a bad event',
      { count: 2, sides: 4, affectsPoints: false, affectsPosition: false, badEvent: true },
      [/штрафа кубами нет/i, /плох\S* ивент/i],
      [/2d4/],
    ],
    [
      'nothing at all',
      { count: 2, sides: 4, affectsPoints: false, affectsPosition: false, badEvent: false },
      [/штрафа кубами нет/i],
      [/2d4/, /плох\S* ивент/i],
    ],
  ])('names the penalty the server gives: %s', async (_case, penalty, present, absent) => {
    servePlaying(playing({ dropPenalty: penalty }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    const consequences = ru.turn.dropConsequences(penalty);
    expect(consequences).not.toEqual(ru.turn.dropConsequences(defaultPenalty));
    const dialog = await screen.findByTestId('drop-confirm');
    expect(
      within(dialog)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(consequences);
    const text = consequences.join(' ');
    for (const pattern of present) expect(text).toMatch(pattern);
    for (const pattern of absent) expect(text).not.toMatch(pattern);
  });

  it('asks plainly when the server gives no penalty', async () => {
    servePlaying(playing({ dropPenalty: null }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    // No penalty to name: only what a drop always does
    expect(ru.turn.dropConsequences(null)).toEqual([
      'Игра больше не выпадет тебе в этом сезоне, дальше — новый ролл',
    ]);
    const dialog = await screen.findByTestId('drop-confirm');
    expect(dialog).toHaveTextContent(ru.turn.dropTitle('Silent Hill'));
    expect(
      within(dialog)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(ru.turn.dropConsequences(null));
    expect(dialog).not.toHaveTextContent(/штраф|кубы/i);
  });

  it('hints before an hour of play but still lets the player drop (RR4)', async () => {
    // Started long ago by the client's clock: the hint follows the server's minutes, not the start time
    const commands = servePlaying(playing({ dropHintMinutes: 60 }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    expect(await screen.findByTestId('drop-hint')).toHaveTextContent(ru.turn.dropHint(60));
    await userEvent.click(screen.getByTestId('drop-confirm-yes'));
    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]?.url).toMatch(dropUrl);
  });

  it.each([
    ['after the hour by the server clock', '2026-09-24T10:00:00Z'],
    ['even right after the start by the client clock', new Date().toISOString()],
  ] as const)('shows no hint without hint minutes: %s', async (_case, startedAt) => {
    servePlaying(playing({ dropHintMinutes: null, startedAt }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    expect(await screen.findByTestId('drop-confirm')).toBeInTheDocument();
    expect(screen.queryByTestId('drop-hint')).not.toBeInTheDocument();
  });

  it('replaces the tech reroll with a note once the window is closed, keeping the drop', async () => {
    servePlaying(playing({ techRerollOpen: false }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('tech-reroll-closed')).toHaveTextContent(
      ru.turn.techRerollClosed,
    );
    expect(screen.queryByTestId('tech-reroll')).not.toBeInTheDocument();
    expect(screen.getByTestId('drop')).toBeInTheDocument();
    expect(screen.getByTestId('complete-form')).toBeInTheDocument();
  });

  it('shows the tech reroll and no note while the window is open', async () => {
    servePlaying(playing({ techRerollOpen: true }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('tech-reroll')).toBeInTheDocument();
    expect(screen.queryByTestId('tech-reroll-closed')).not.toBeInTheDocument();
  });

  it('asks for one of the five reasons and sends the tech reroll', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('tech-reroll'));

    const select = await screen.findByTestId<HTMLSelectElement>('tech-reroll-reason');
    const options = Array.from(select.options).filter((o) => o.value !== '');
    expect(options.map((o) => o.value)).toEqual([...reasons]);
    for (const option of options) {
      expect(option).toHaveTextContent(ru.turn.techRerollReasons[option.value as Reason]);
    }
    expect(commands).toHaveLength(0);

    await userEvent.selectOptions(select, 'paidUnavailable');
    await userEvent.click(screen.getByTestId('tech-reroll-submit'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]?.url).toMatch(techRerollUrl);
    expect(commands[0]?.body).toMatchObject({
      commandId: expect.any(String) as unknown,
      reason: 'paidUnavailable',
    });
  });

  it('opens the tech reroll in a window that says what it does before anything is sent (H4)', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('tech-reroll'));

    const dialog = await screen.findByRole('dialog', {
      name: ru.turn.techRerollTitle('Silent Hill'),
    });
    expect(
      within(dialog)
        .getAllByRole('listitem')
        .map((li) => li.textContent),
    ).toEqual(ru.turn.techRerollConsequences);
    // Nothing to send before a reason is picked
    expect(within(dialog).getByTestId('tech-reroll-submit')).toBeDisabled();
    expect(commands).toHaveLength(0);
  });

  it('keeps the drop apart from completing: a quiet red link, not a main button (H4)', async () => {
    servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('drop')).toHaveAttribute('data-variant', 'dangerLink');
    expect(screen.getByTestId('complete-submit')).toHaveAttribute('data-variant', 'main');
    // One main action on the screen
    expect(
      within(screen.getByTestId('turn'))
        .getAllByRole('button')
        .filter((b) => b.getAttribute('data-variant') === 'main'),
    ).toHaveLength(1);
  });

  it('requires a comment for the reason «other» before sending', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await userEvent.click(await screen.findByTestId('tech-reroll'));

    await userEvent.selectOptions(await screen.findByTestId('tech-reroll-reason'), 'other');
    await userEvent.click(screen.getByTestId('tech-reroll-submit'));

    // Nothing is sent without the comment; the form explains why
    expect(await screen.findByText(ru.turn.techRerollCommentRequired)).toBeInTheDocument();
    expect(commands).toHaveLength(0);

    await userEvent.type(screen.getByTestId('tech-reroll-comment'), 'Нужен руль');
    await userEvent.click(screen.getByTestId('tech-reroll-submit'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]?.url).toMatch(techRerollUrl);
    expect(commands[0]?.body).toMatchObject({ reason: 'other', comment: 'Нужен руль' });
  });

  it('cancels a tech reroll without a request', async () => {
    const commands = servePlaying(playing());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('tech-reroll'));
    await userEvent.click(await screen.findByTestId('tech-reroll-cancel'));

    expect(screen.queryByTestId('tech-reroll-reason')).not.toBeInTheDocument();
    await act(async () => {
      await Promise.resolve();
    });
    expect(commands).toHaveLength(0);
  });

  it.each(['run.techRerollWindowClosed', 'run.reasonCommentRequired'])(
    'explains the rejection %s in Russian',
    async (code) => {
      serve((r) =>
        isSeasonGet(r)
          ? json(200, playing())
          : json(409, { title: 'rejected', status: 409, detail: null, code }),
      );
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      await userEvent.click(await screen.findByTestId('tech-reroll'));
      await userEvent.selectOptions(await screen.findByTestId('tech-reroll-reason'), 'weakPc');
      await userEvent.click(screen.getByTestId('tech-reroll-submit'));

      const text = ru.rejection[code];
      expect(text).toEqual(expect.any(String));
      expect(text).not.toBe(ru.rejection.unknown);
      expect(await screen.findByRole('alert')).toHaveTextContent(text ?? '');
    },
  );

  it('lists the bad event of a drop among manual effects', async () => {
    const effect: Schemas['ManualEffectView'] = {
      id: 'e1000000-0000-0000-0000-000000000003',
      drawEvent: 'bad',
      source: 'drop',
    };
    servePlaying(
      season({
        me: {
          playerId: me,
          phase: 'idle',
          offer: null,
          choice: null,
          activeRun: null,
          lastCompleted: null,
          nextReroll: null,
          manualEffects: [effect],
          dropHintMinutes: null,
          dropPenalty: null,
          techRerollOpen: false,
          challengesEnabled: false,
          roll: null,
          unchecked: null,
          finish: null,
        },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    // The source has its own words: not the paid reroll's, not a missing entry
    const text = ru.effects.drawEvent('bad', 'drop');
    expect(text).not.toMatch(/undefined/);
    expect(text).not.toBe(ru.effects.drawEvent('bad', 'paidReroll'));
    expect(text).toMatch(/дроп/i);
    expect(await screen.findByTestId(`manual-effect-${effect.id}`)).toHaveTextContent(text);
  });
});

describe('SeasonScreen marks on offered games (G8, D-94 (6))', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  type Mark = Schemas['GameMarkView'];

  function rolling(turn: Partial<Schemas['MyTurnView']>): Schemas['SeasonView'] {
    return season({
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'start',
          points: 0,
          phase: 'rolling',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'rolling',
        offer: null,
        choice: null,
        activeRun: null,
        lastCompleted: null,
        nextReroll: { payment: 'freeThisRoll', coins: 0 },
        manualEffects: [],
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
        ...turn,
      },
    });
  }

  function serveView(view: Schemas['SeasonView']) {
    serve((r) => (isSeasonGet(r) ? json(200, view) : json(404, {})));
  }

  it('names who dropped and who tech-rerolled the offered game', async () => {
    const marks: Mark[] = [
      { playerName: 'Петя', kind: 'dropped' },
      { playerName: 'Маша', kind: 'techRerolled' },
    ];
    serveView(
      rolling({
        offer: {
          id: 'a1000000-0000-0000-0000-000000000001',
          title: 'Silent Hill',
          hours: 12,
          marks,
        },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const shown = await screen.findByTestId('game-marks');
    expect(shown).toHaveTextContent(ru.turn.gameMark('Петя', 'dropped'));
    expect(shown).toHaveTextContent(ru.turn.gameMark('Маша', 'techRerolled'));
    expect(ru.turn.gameMark('Петя', 'dropped')).not.toBe(ru.turn.gameMark('Петя', 'techRerolled'));
    expect(ru.turn.gameMark('Петя', 'dropped')).toContain('Петя');
  });

  it('shows no marks for an offered game nobody gave up', async () => {
    serveView(
      rolling({
        offer: {
          id: 'a1000000-0000-0000-0000-000000000001',
          title: 'Silent Hill',
          hours: 12,
          marks: [],
        },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('start');
    expect(screen.queryByTestId('game-marks')).not.toBeInTheDocument();
  });

  it('shows the marks of each choice option on that option only', async () => {
    serveView(
      rolling({
        choice: {
          id: 'c0000000-0000-0000-0000-000000000001',
          kind: 'game',
          options: [
            {
              id: 'a1',
              game: {
                id: 'a1000000-0000-0000-0000-000000000001',
                title: 'Silent Hill',
                hours: 12,
                marks: [{ playerName: 'Петя', kind: 'techRerolled' }],
              },
            },
            {
              id: 'b2',
              game: {
                id: 'b2000000-0000-0000-0000-000000000002',
                title: 'Outlast',
                hours: 9,
                marks: [],
              },
            },
          ],
        },
      }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const marked = await screen.findByTestId('option-a1');
    expect(within(marked).getByTestId('game-marks')).toHaveTextContent(
      ru.turn.gameMark('Петя', 'techRerolled'),
    );
    expect(
      within(screen.getByTestId('option-b2')).queryByTestId('game-marks'),
    ).not.toBeInTheDocument();
    expect(screen.getAllByTestId('game-marks')).toHaveLength(1);
    // A screen reader hears the mark in the card's name too (H3 review)
    expect(marked).toHaveAccessibleName(
      expect.stringContaining(ru.turn.gameMark('Петя', 'techRerolled')) as string,
    );
  });
});

describe('SeasonScreen completion reward (C7a, D-96)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  type CompletedWithReward = Schemas['CompletedRunView'];

  const runId = 'b1000000-0000-0000-0000-000000000009';

  function completed(last: CompletedWithReward, effects: Schemas['ManualEffectView'][] = []) {
    return season({
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'c1',
          points: last.total,
          phase: 'idle',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'idle',
        offer: null,
        choice: null,
        activeRun: null,
        lastCompleted: last,
        nextReroll: null,
        manualEffects: effects,
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
        challengesEnabled: false,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
  }

  function last(overrides: Partial<CompletedWithReward> = {}): CompletedWithReward {
    return {
      id: runId,
      game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 6 },
      difficulty: 'normal',
      dice: [
        { sides: 4, value: 3 },
        { sides: 4, value: 1 },
      ],
      challengeDice: [{ sides: 4, value: 4 }],
      total: 8,
      review: { rating: 9, text: 'Туман и радио' },
      proof: null,
      status: 'completed',
      ...overrides,
    };
  }

  function playingWithoutHours(challengesEnabled = true) {
    return season({
      players: [
        {
          id: me,
          name: 'Вася',
          cellId: 'start',
          points: 0,
          phase: 'playing',
          finishOrder: null,
          avatar: null,
        },
      ],
      me: {
        playerId: me,
        phase: 'playing',
        offer: null,
        choice: null,
        activeRun: {
          id: runId,
          game: { id: 'a1000000-0000-0000-0000-000000000002', title: 'Pathologic', hours: null },
          startedAt: '2026-09-24T10:00:00Z',
        },
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: true,
        challengesEnabled,
        roll: null,
        unchecked: null,
        finish: null,
      },
    });
  }

  /** Serves the playing view and records every command sent. */
  function recordCommands(view: Schemas['SeasonView']) {
    const commands: { url: string; body: Record<string, unknown> }[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, view);
      commands.push({ url: r.url, body: (await r.json()) as Record<string, unknown> });
      return json(200, { duplicate: false, events: [] });
    });
    return commands;
  }

  it('shows the challenge dice and the review of the last completed run', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, completed(last())) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByText(ru.turn.lastChallengeDice([4]), { exact: false }),
    ).toBeInTheDocument();
    expect(
      screen.getByText(ru.turn.lastReview(9, 'Туман и радио'), { exact: false }),
    ).toBeInTheDocument();
  });

  it('sums the dice by hours and the challenge dice in one line, listing the challenge dice under it', async () => {
    // D-96: 3 + 1 by hours and 4 for the challenge make 8
    serve((r) => (isSeasonGet(r) ? json(200, completed(last())) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('last-dice')).toHaveTextContent(
      ru.turn.lastDice('Silent Hill', [3, 1, 4], 8),
    );
    expect(screen.getByTestId('last-challenge-dice')).toHaveTextContent(
      ru.turn.lastChallengeDice([4]),
    );
  });

  it('shows the reject instead of the dice line for a rejected last run', async () => {
    // D-98: the run's points and cells were taken back, so its dice no longer count
    serve((r) =>
      isSeasonGet(r)
        ? json(
            200,
            completed(
              last({
                status: 'rejected',
                proof: {
                  status: 'rejected',
                  files: [],
                  links: [],
                  note: null,
                  comment: 'Не та игра',
                },
              }),
            ),
          )
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const line = await screen.findByTestId('last-dice');
    expect(line).toHaveTextContent(ru.turn.lastRejected('Silent Hill'));
    expect(line).not.toHaveTextContent(ru.turn.lastDice('Silent Hill', [3, 1, 4], 8));
  });

  it('shows the dice line for a completed last run', async () => {
    serve((r) =>
      isSeasonGet(r) ? json(200, completed(last({ status: 'completed' }))) : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const line = await screen.findByTestId('last-dice');
    expect(line).toHaveTextContent(ru.turn.lastDice('Silent Hill', [3, 1, 4], 8));
    expect(line).not.toHaveTextContent(ru.turn.lastRejected('Silent Hill'));
  });

  it('shows neither challenge dice nor a review when there are none', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, completed(last({ challengeDice: [], total: 4, review: null })))
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');

    const challengePrefix = ru.turn.lastChallengeDice([]).split(':')[0] ?? '';
    expect(screen.queryByText(challengePrefix, { exact: false })).not.toBeInTheDocument();
    expect(
      screen.queryByText(ru.turn.lastReview(9, null).split(':')[0] ?? '', { exact: false }),
    ).not.toBeInTheDocument();
  });

  it('shows a rating without text as a rating', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, completed(last({ review: { rating: 4, text: null } })))
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(
      await screen.findByText(ru.turn.lastReview(4, null), { exact: false }),
    ).toBeInTheDocument();
  });

  it('lists the good event of «выше сложной» among manual effects with its own words', async () => {
    const effect: Schemas['ManualEffectView'] = {
      id: 'e1000000-0000-0000-0000-000000000009',
      drawEvent: 'good',
      source: 'difficulty',
    };
    serve((r) =>
      isSeasonGet(r)
        ? json(200, completed(last({ difficulty: 'extreme' }), [effect]))
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const text = ru.effects.drawEvent('good', 'difficulty');
    expect(text).not.toMatch(/undefined/);
    expect(text).toMatch(/сложн/i);
    expect(await screen.findByTestId(`manual-effect-${effect.id}`)).toHaveTextContent(text);
  });

  it('sends the estimate with its source, the challenge and the review with the completion', async () => {
    const commands = recordCommands(playingWithoutHours());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('complete-hours'), '6');
    await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
    await userEvent.click(screen.getByLabelText(ru.turn.challengeDone));
    await userEvent.selectOptions(screen.getByTestId('complete-review-rating'), '7');
    await userEvent.type(screen.getByTestId('complete-review-text'), 'Хорошо');
    await userEvent.click(screen.getByTestId('complete-submit'));

    await vi.waitFor(() => {
      expect(commands).toHaveLength(1);
    });
    expect(commands[0]?.url).toMatch(new RegExp(`/api/seasons/${seasonId}/complete$`));
    expect(commands[0]?.body).toMatchObject({
      difficulty: 'normal',
      estimatedHours: 6,
      hoursSource: 'HLTB',
      challengeDone: true,
      review: { rating: 7, text: 'Хорошо' },
    });
  });

  it('offers no challenge when the season has challenges off', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, playingWithoutHours(false)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('complete-submit');
    expect(screen.queryByLabelText(ru.turn.challengeDone)).not.toBeInTheDocument();
  });

  it('offers the challenge when the season has challenges on', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, playingWithoutHours(true)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByLabelText(ru.turn.challengeDone)).not.toBeChecked();
  });

  it.each([
    ['challenges are off', false],
    ['the box is unchecked', true],
  ])(
    'always sends challengeDone false with the completion when %s (db9dc14)',
    async (_case, challengesEnabled) => {
      const commands = recordCommands(playingWithoutHours(challengesEnabled));
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      await userEvent.type(await screen.findByTestId('complete-hours'), '6');
      await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
      await userEvent.click(screen.getByTestId('complete-submit'));

      await vi.waitFor(() => {
        expect(commands).toHaveLength(1);
      });
      expect(commands[0]?.url).toMatch(new RegExp(`/api/seasons/${seasonId}/complete$`));
      expect(commands[0]?.body).toHaveProperty('challengeDone', false);
    },
  );

  it.each(['run.hoursSourceTooLong', 'feature.disabled', 'season.closed'] as const)(
    'shows the refusal %s in Russian',
    async (code) => {
      serve((r) =>
        isSeasonGet(r)
          ? json(200, playingWithoutHours())
          : json(409, { title: 'rejected', status: 409, detail: null, code }),
      );
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      await userEvent.type(await screen.findByTestId('complete-hours'), '6');
      await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
      await userEvent.click(screen.getByTestId('complete-submit'));

      const text = ru.rejection[code];
      expect(text).toBeTruthy();
      expect(await screen.findByRole('alert')).toHaveTextContent(text);
    },
  );

  it('shows the engine refusal of a missing source in Russian', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, playingWithoutHours())
        : json(409, {
            title: 'rejected',
            status: 409,
            detail: null,
            code: 'run.hoursSourceRequired',
          }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('complete-hours'), '6');
    await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.rejection['run.hoursSourceRequired'],
    );
  });
});

describe('SeasonScreen: runs waiting for the admin (D-134)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const waiting = (count: number, limit: number) => {
    const base = season();
    return season({ me: base.me && { ...base.me, unchecked: { count, limit } } });
  };

  it('closes the roll at the limit and says why, offering to send the proofs meanwhile', async () => {
    const fetch = serve((r) => (isSeasonGet(r) ? json(200, waiting(2, 2)) : json(200, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const roll = await screen.findByTestId('roll');
    expect(roll).toBeDisabled();
    expect(screen.getByText(ru.turn.uncheckedBlocked(2, 2))).toBeInTheDocument();
    await userEvent.click(roll);
    expect(fetch.mock.calls.some(([r]) => r.method === 'POST')).toBe(false);
  });

  it('keeps the roll open below the limit with a quiet count', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, waiting(1, 2)) : json(200, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('roll')).toBeEnabled();
    expect(screen.getByTestId('unchecked')).toHaveTextContent(ru.turn.uncheckedWaiting(1, 2));
    expect(screen.queryByText(ru.turn.uncheckedBlocked(1, 2))).toBeNull();
  });

  it('says nothing when the season has no limit or nothing waits', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, waiting(0, 2)) : json(200, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('roll')).toBeEnabled();
    expect(screen.queryByTestId('unchecked')).toBeNull();
  });

  it('shows the refusal in Russian if the server refuses the roll anyway', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, waiting(1, 2))
        : json(409, {
            title: 'rejected',
            status: 409,
            detail: null,
            code: 'roll.tooManyUnchecked',
          }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('roll'));
    expect(await screen.findByText(ru.rejection['roll.tooManyUnchecked'])).toBeInTheDocument();
  });
});

describe('SeasonScreen wheel (H3, D-136)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const offered = {
    id: 'a1000000-0000-0000-0000-000000000001',
    title: 'Silent Hill',
    hours: 12,
    marks: [],
  };
  const roll: Schemas['WheelRollView'] = {
    sequence: 7,
    category: 'Horror',
    sectors: ['Action', 'Horror', 'RPG'],
    misses: [
      { game: 'Outlast', reason: 'completedInSeason', player: 'Петя', at: '2026-10-12T09:00:00Z' },
      { game: 'Alan Wake', reason: 'beingPlayed', player: 'Маша', at: null },
    ],
  };
  const rolled = (sequence = 7): Schemas['SeasonView'] => {
    const base = season({ lastSequence: sequence });
    if (!base.me) throw new Error('the fixture has a player');
    return {
      ...base,
      me: {
        ...base.me,
        phase: 'rolling',
        offer: offered,
        nextReroll: { payment: 'freeThisRoll', coins: 0 },
        roll: { ...roll, sequence },
      },
    };
  };

  it('shows a roll the page opened with at once, its misses in words', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, rolled()) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const offer = await screen.findByTestId('offer');
    expect(screen.queryByTestId('wheel')).not.toBeInTheDocument();
    expect(offer).toHaveTextContent('Silent Hill');
    expect(offer).toHaveTextContent('Horror');
    // Told as done, with the day of the completion (SPEC «Уже прошёл Вася, 12.10»)
    const misses = within(offer).getByTestId('roll-misses');
    expect(misses).toHaveTextContent(ru.moments.wheel.missed(2));
    expect(misses).toHaveTextContent(ru.moments.wheel.missedCompleted('Outlast', 'Петя', '12.10'));
    expect(misses).toHaveTextContent(ru.moments.wheel.missedPlaying('Alan Wake', 'Маша'));
  });

  it('spins the wheel for a new roll, then offers the game; the moment can be skipped', async () => {
    let current = season();
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      await r.text();
      current = rolled();
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('roll'));

    const wheel = await screen.findByTestId('wheel');
    expect(screen.queryByTestId('start')).not.toBeInTheDocument();
    await userEvent.click(within(wheel).getByRole('button', { name: ru.moments.skip }));
    expect(await screen.findByTestId('start')).toBeInTheDocument();
    expect(screen.queryByTestId('wheel')).not.toBeInTheDocument();
    // The result is said in a live region that outlives the wheel, and the focus goes to the rolled game
    expect(screen.getByTestId('roll-announce')).toHaveTextContent(
      ru.moments.wheel.announce('Horror', ru.moments.wheel.result('Silent Hill')),
    );
    await vi.waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Silent Hill' })).toHaveFocus();
    });
  });

  it('spins for the new roll «Уже проходил» brings, on the wheel the server sent', async () => {
    let current = rolled(7);
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      await r.text();
      current = rolled(8);
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('already-played'));

    const wheel = await screen.findByTestId('wheel');
    for (const sector of roll.sectors) {
      expect(within(wheel).getByText(sector)).toBeInTheDocument();
    }
  });

  it('does not spin again for an older roll the admin brought back by undoing a reroll', async () => {
    let current = rolled(9);
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('offer');

    const older = rolled(7);
    if (!older.me) throw new Error('the fixture has a player');
    current = {
      ...older,
      lastSequence: 12,
      me: { ...older.me, offer: { ...offered, title: 'Outlast' } },
    };
    act(() => {
      hubChange?.();
    });
    expect(await screen.findByText('Outlast', { selector: 'h3' })).toBeInTheDocument();

    expect(screen.queryByTestId('wheel')).not.toBeInTheDocument();
  });

  it('plays on the stage of the map on a desktop and keeps the result there until closed', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('min-width'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
    let current = season();
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      await r.text();
      current = rolled();
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('roll'));

    const wheel = await screen.findByTestId('wheel');
    expect(within(screen.getByTestId('turn')).queryByTestId('wheel')).not.toBeInTheDocument();
    await userEvent.click(within(wheel).getByRole('button', { name: ru.moments.skip }));
    // The answer is in the turn card at once; the landed wheel stays on the stage
    expect(await screen.findByTestId('start')).toBeInTheDocument();
    const landed = screen.getByTestId('wheel');
    await userEvent.click(within(landed).getByRole('button', { name: ru.moments.wheel.toMap }));
    expect(screen.queryByTestId('wheel')).not.toBeInTheDocument();
    // The button went with the stage: the focus is on the rolled game
    await vi.waitFor(() => {
      expect(screen.getByRole('heading', { name: 'Silent Hill' })).toHaveFocus();
    });
  });

  it('spins again for a reroll', async () => {
    let current = rolled(7);
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      await r.text();
      current = rolled(9);
      return json(200, { duplicate: false, events: [] });
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('reroll'));

    expect(await screen.findByTestId('wheel')).toBeInTheDocument();
  });
});

describe('SeasonScreen completion moment (H4)', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const runId = 'b1000000-0000-0000-0000-000000000020';
  const done: Schemas['CompletedRunView'] = {
    id: runId,
    game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 6 },
    difficulty: 'normal',
    dice: [{ sides: 4, value: 1 }],
    challengeDice: [{ sides: 4, value: 2 }],
    total: 3,
    review: null,
    proof: null,
    status: 'completed',
  };
  const announce = ru.moments.dice.announce(
    'Silent Hill',
    ru.moments.dice.result([1], [2], 3),
    ru.moments.dice.at(2, false),
  );

  /** Вася plays Silent Hill at the start, or has completed it (standing on c1) */
  function view(
    completed: Schemas['CompletedRunView'] | null,
    sequence = 5,
    cellId = completed ? 'c1' : 'start',
  ): Schemas['SeasonView'] {
    const base = season({
      lastSequence: sequence,
      players: [
        {
          id: me,
          name: 'Вася',
          cellId,
          points: completed?.total ?? 0,
          phase: completed ? 'idle' : 'playing',
          finishOrder: null,
          avatar: null,
        },
      ],
    });
    if (!base.me) throw new Error('the fixture has a player');
    return {
      ...base,
      me: {
        ...base.me,
        phase: completed ? 'idle' : 'playing',
        activeRun: completed
          ? null
          : {
              id: runId,
              game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 6 },
              startedAt: '2026-09-24T10:00:00Z',
            },
        lastCompleted: completed,
        dropPenalty: completed
          ? null
          : { count: 2, sides: 4, affectsPoints: true, affectsPosition: true, badEvent: true },
      },
    };
  }

  /** The page opens with the run in play; completing it brings the dice */
  function serveCompletion(after: Schemas['SeasonView'] = view(done, 6)) {
    let current = view(null);
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      await r.text();
      current = after;
      return json(200, { duplicate: false, events: [] });
    });
    return {
      set: (next: Schemas['SeasonView']) => {
        current = next;
      },
    };
  }

  async function complete() {
    await userEvent.click(await screen.findByTestId('complete-submit'));
  }

  it('shows the last completion the page opened with at once, without the dice', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, view(done)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('last-dice')).toHaveTextContent(
      ru.turn.lastDice('Silent Hill', [1, 2], 3),
    );
    expect(screen.getByTestId('roll')).toBeInTheDocument();
    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
  });

  it('throws the dice in the turn card for a completion made while the page is open; the rest waits', async () => {
    serveCompletion();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await complete();

    const dice = await screen.findByTestId('dice');
    expect(within(screen.getByTestId('turn')).getByTestId('dice')).toBe(dice);
    // Every die of the run is on the table, the challenge die among them
    expect(dice.querySelectorAll('[data-die]')).toHaveLength(2);
    expect(within(dice).getByText(ru.moments.dice.challengeDie)).toBeInTheDocument();
    // The result is not told before the dice land, and the next roll waits
    expect(screen.queryByTestId('last-dice')).not.toBeInTheDocument();
    expect(screen.queryByTestId('roll')).not.toBeInTheDocument();
    expect(screen.getByTestId('roll-announce')).toBeEmptyDOMElement();
  });

  it('shows the result at once on «Показать результат»: said in words, the focus on the dice line', async () => {
    serveCompletion();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await complete();

    const dice = await screen.findByTestId('dice');
    await userEvent.click(within(dice).getByRole('button', { name: ru.moments.skip }));

    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
    expect(screen.getByTestId('roll-announce')).toHaveTextContent(announce);
    expect(screen.getByTestId('roll')).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(screen.getByTestId('last-dice')).toHaveFocus();
    });
    expect(screen.getByTestId('last-dice')).toHaveTextContent(
      ru.turn.lastDice('Silent Hill', [1, 2], 3),
    );
  });

  it('does not restart the dice when the season refreshes in the middle, nor throw them again after', async () => {
    const server = serveCompletion();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await complete();
    const dice = await screen.findByTestId('dice');
    const firstDie = dice.querySelector('[data-die]');

    // Another player acts: the same completion comes again in a newer view
    server.set(view(done, 9));
    act(() => {
      hubChange?.();
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(screen.getByTestId('dice')).toBe(dice);
    expect(dice.querySelector('[data-die]')).toBe(firstDie);

    await userEvent.click(within(dice).getByRole('button', { name: ru.moments.skip }));
    server.set(view(done, 11));
    act(() => {
      hubChange?.();
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
  });

  it.each([
    ['the admin rejects it', { status: 'rejected' as const }],
    [
      'the admin adds a die after a change of hours',
      { dice: [...done.dice, { sides: 4, value: 4 }], total: 7 },
    ],
  ])('does not throw the dice again for the same run when %s', async (_case, change) => {
    let current = view(done);
    serve((r) => (isSeasonGet(r) ? json(200, current) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('last-dice');

    current = view({ ...done, ...change }, 12);
    act(() => {
      hubChange?.();
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 20));
    });

    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
    expect(screen.getByTestId('last-dice')).toBeInTheDocument();
  });

  it('plays on the stage of the map on a desktop, not in the turn card', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({
      matches: query.includes('min-width'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }));
    serveCompletion();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await complete();

    const dice = await screen.findByTestId('dice');
    expect(within(screen.getByTestId('turn')).queryByTestId('dice')).not.toBeInTheDocument();
    // The turn card says where to look meanwhile
    expect(screen.getByTestId('throwing')).toHaveTextContent(ru.turn.throwing('Silent Hill'));
    await userEvent.click(within(dice).getByRole('button', { name: ru.moments.skip }));
    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
    expect(screen.queryByTestId('throwing')).not.toBeInTheDocument();
    expect(screen.getByTestId('roll-announce')).toHaveTextContent(announce);
  });

  it('ends by itself: the dice land, the token walks, the result is said', async () => {
    serveCompletion();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await complete();
    await screen.findByTestId('dice');

    // Within the moments' budget: the dice, the rest on the total and the walk
    await vi.waitFor(
      () => {
        expect(screen.getByTestId('roll-announce')).toHaveTextContent(announce);
      },
      { timeout: 6000 },
    );
    expect(screen.queryByTestId('dice')).not.toBeInTheDocument();
  });

  // Reduced motion: CompletionMoment.test.tsx (motion reads the preference once per module)

  it('says the token reached the finish when the dice take it there', async () => {
    serveCompletion(view(done, 6, 'finish'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await complete();

    const dice = await screen.findByTestId('dice');
    await userEvent.click(within(dice).getByRole('button', { name: ru.moments.skip }));

    expect(screen.getByTestId('roll-announce')).toHaveTextContent(ru.moments.dice.at(3, true));
  });
});
