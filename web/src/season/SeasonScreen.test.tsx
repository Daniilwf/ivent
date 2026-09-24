import { act, render, screen } from '@testing-library/react';
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

function season(overrides: Partial<Schemas['SeasonView']> = {}): Schemas['SeasonView'] {
  return {
    id: seasonId,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    players: [{ id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'idle' }],
    me: {
      playerId: me,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [],
    },
    lastSequence: 3,
    ...overrides,
  };
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

type Handler = (request: Request) => Response | Promise<Response>;

function serve(handler: Handler) {
  const fetch = vi.fn((request: Request) => Promise.resolve(handler(request)));
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
      players: [{ id: me, name: 'Вася', cellId: 'c1', points: 4, phase: 'idle' }],
    });
    act(() => {
      hubChange?.();
    });

    expect(await screen.findByTestId(`leader-${me}`)).toHaveTextContent(
      ru.leaderboard.row('Вася', 4),
    );
    expect(screen.getByTestId('cell-c1')).toHaveTextContent('Вася');
  });

  it('ignores an answer older than what it already shows', async () => {
    let current = season({
      lastSequence: 9,
      players: [{ id: me, name: 'Вася', cellId: 'c1', points: 4, phase: 'idle' }],
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
    const game = (id: string, title: string) => ({ id, title, hours: 12 });
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
    expect(screen.getByTestId('option-a1')).toHaveTextContent(ru.turn.option('Silent Hill', 12));

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
    const picked = { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 };
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
              game: { id: 'b2000000-0000-0000-0000-000000000002', title: 'Outlast', hours: 9 },
            },
          ],
        },
        activeRun: null,
        lastCompleted: null,
        nextReroll: null,
        manualEffects: [],
      },
    });
    // The other tab already chose: the server now has Вася playing, and answers this tab's choice with 409
    const playing = season({
      lastSequence: 5,
      players: [{ id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'playing' }],
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
              offer: { id: gameId, title: 'Silent Hill', hours: 12 },
              choice: null,
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
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
        game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
      },
      {
        id: 'opt-2',
        game: { id: 'b2000000-0000-0000-0000-000000000002', title: 'Outlast', hours: 9 },
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
              },
              choice: null,
              activeRun: null,
              lastCompleted: null,
              nextReroll: null,
              manualEffects: [],
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
        game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
      },
      {
        id: 'opt-2',
        game: { id: 'b2000000-0000-0000-0000-000000000002', title: 'Outlast', hours: 9 },
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
      players: [{ id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'playing' }],
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
                },
                choice: null,
                activeRun: null,
                lastCompleted: null,
                nextReroll: null,
                manualEffects: [],
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

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.app.loadError);
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
        offer: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 12 },
        choice: null,
        activeRun: null,
        lastCompleted: null,
        nextReroll,
        manualEffects,
      },
    });
  }

  /** Serves the season and records every command request. */
  function serveRolling(view: Schemas['SeasonView']) {
    const commands: string[] = [];
    serve((r) => {
      if (isSeasonGet(r)) return json(200, view);
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

  it('shows no manual effects section when there are none', async () => {
    serveRolling(rolling({ payment: 'freeThisRoll', coins: 0 }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('reroll');
    expect(screen.queryByTestId('manual-effects')).not.toBeInTheDocument();
  });
});
