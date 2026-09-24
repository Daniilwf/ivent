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
    me: { playerId: me, phase: 'idle', offer: null, activeRun: null, lastCompleted: null },
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
