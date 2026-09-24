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
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
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
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
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
        dropHintMinutes: null,
        dropPenalty: null,
        techRerollOpen: false,
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
      players: [{ id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'playing' }],
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

    const text = ru.turn.dropConfirm(defaultPenalty);
    expect(await screen.findByTestId('drop-confirm')).toHaveTextContent(text);
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
      [/клетки/, /плох\S* ивент/i, /штрафа кубами нет/],
    ],
    [
      'no dice penalty but a bad event',
      { count: 2, sides: 4, affectsPoints: false, affectsPosition: false, badEvent: true },
      [/штрафа кубами нет/, /плох\S* ивент/i],
      [/2d4/],
    ],
    [
      'nothing at all',
      { count: 2, sides: 4, affectsPoints: false, affectsPosition: false, badEvent: false },
      [/штрафа кубами нет/],
      [/2d4/, /плох\S* ивент/i],
    ],
  ])('names the penalty the server gives: %s', async (_case, penalty, present, absent) => {
    servePlaying(playing({ dropPenalty: penalty }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    const text = ru.turn.dropConfirm(penalty);
    expect(text).not.toBe(ru.turn.dropConfirm(defaultPenalty));
    expect(await screen.findByTestId('drop-confirm')).toHaveTextContent(text);
    for (const pattern of present) expect(text).toMatch(pattern);
    for (const pattern of absent) expect(text).not.toMatch(pattern);
  });

  it('asks plainly when the server gives no penalty', async () => {
    servePlaying(playing({ dropPenalty: null }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('drop'));

    expect(ru.turn.dropConfirm(null)).toBe('Дропнуть игру?');
    expect(await screen.findByTestId('drop-confirm')).toHaveTextContent(ru.turn.dropConfirm(null));
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
      players: [{ id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'rolling' }],
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
  });
});
