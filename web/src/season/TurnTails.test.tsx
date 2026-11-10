import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { json } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

// Stage 1–2 tails of the turn card: the finished player's state (D-260) and the wish reroll (D-325, D-261)

let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    hubChange = () => {
      onChange([]);
    };
    onChange([]);
    return () => {
      hubChange = null;
    };
  },
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';
const runId = 'e0000000-0000-0000-0000-000000000001';

type Me = Schemas['MyTurnView'];

function season(
  turn: Partial<Me> = {},
  cellId = 'start',
  row: { place?: number; isFirst?: boolean } = {},
): Schemas['SeasonView'] {
  const players: Schemas['PlayerView'][] = [
    {
      id: me,
      name: 'Вася',
      cellId,
      points: 12,
      phase: turn.phase ?? 'idle',
      finishOrder: turn.finish?.order ?? null,
      avatar: null,
      token: 0,
    },
  ];
  return {
    id: seasonId,
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
      ...turn,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    leaderboard: [
      {
        playerId: me,
        place: row.place ?? 1,
        points: 12,
        cellsToFinish: cellId === 'finish' ? 0 : 2,
        isFirst: row.isFirst ?? turn.finish?.order === 1,
        provisional: (row.isFirst ?? turn.finish?.order === 1) && turn.finish?.frozen !== true,
      },
    ],
  };
}

const isSeasonGet = (r: Request) =>
  r.method === 'GET' && r.url.endsWith(`/api/seasons/${seasonId}`);
const isAccountGet = (r: Request) => r.method === 'GET' && r.url.endsWith('/api/auth/me');
const isFeedGet = (r: Request) => r.method === 'GET' && new URL(r.url).pathname.endsWith('/feed');
const account = {
  id: 'u',
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};
const emptyFeed = { entries: [], nextBefore: null, players: [], games: [], runs: [] };

/** Serves the season (the latest `view()`); a command answers with `command` and is recorded */
function serve(
  view: () => Schemas['SeasonView'],
  command: () => Response = () => json(200, { duplicate: false, events: [] }),
) {
  const commands: { url: string; body: Record<string, unknown> }[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (r: Request) => {
      if (isAccountGet(r)) return json(200, account);
      if (isFeedGet(r)) return json(200, emptyFeed);
      if (isSeasonGet(r)) return json(200, view());
      commands.push({ url: r.url, body: (await r.json()) as Record<string, unknown> });
      return command();
    }),
  );
  return commands;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the finished player’s turn card (D-260)', () => {
  it('says the first finish is provisional until the proofs are checked', async () => {
    serve(() => season({ finish: { order: 1, frozen: false } }, 'finish'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const finish = await screen.findByTestId('my-finish');
    expect(finish).toHaveTextContent(ru.turn.myFinish.title);
    expect(finish).toHaveTextContent(ru.turn.myFinish.firstProvisional);
    expect(finish).toHaveTextContent(ru.turn.myFinish.firstProvisionalNext);
    expect(finish).not.toHaveTextContent(ru.turn.myFinish.firstFrozen);
  });

  it('says the frozen first plays on without counting', async () => {
    serve(() => season({ finish: { order: 1, frozen: true } }, 'finish'));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const finish = await screen.findByTestId('my-finish');
    expect(finish).toHaveTextContent(ru.turn.myFinish.firstFrozen);
    expect(finish).toHaveTextContent(ru.turn.myFinish.firstFrozenNext);
    expect(finish).not.toHaveTextContent(ru.turn.myFinish.firstProvisional);
    // SPEC «Первое место»: he plays on without counting — the roll stays, its meaning is said next to it (D-260)
    expect(screen.getByTestId('roll')).toBeEnabled();
  });

  it('says a later finisher’s place by points and that the points still grow', async () => {
    serve(() => season({ finish: { order: 3, frozen: false } }, 'finish', { place: 2 }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const finish = await screen.findByTestId('my-finish');
    expect(finish).toHaveTextContent(ru.turn.myFinish.later(2));
    expect(finish).toHaveTextContent(ru.turn.myFinish.laterNext);
    expect(finish).not.toHaveTextContent(ru.turn.myFinish.firstProvisional);
  });

  it('takes the first place from the leaderboard: the second to finish leads once the first’s finish is revoked', async () => {
    // D-99: the order of finishing is never reused — Петя of scenario 5 finished second and holds the first place
    serve(() => season({ finish: { order: 2, frozen: false } }, 'finish', { isFirst: true }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('my-finish')).toHaveTextContent(
      ru.turn.myFinish.firstProvisional,
    );
  });

  it('says the frozen one is the first whatever his order of finishing', async () => {
    serve(() => season({ finish: { order: 2, frozen: true } }, 'finish', { isFirst: true }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('my-finish')).toHaveTextContent(ru.turn.myFinish.firstFrozen);
  });

  it('shows no finish state to a player who has not finished', async () => {
    serve(() => season());
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('roll');
    expect(screen.queryByTestId('my-finish')).not.toBeInTheDocument();
  });

  it('shows no finish state once the season is over: the results say it', async () => {
    serve(() => ({
      ...season({ finish: { order: 1, frozen: true } }, 'finish'),
      status: 'finished',
    }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByText(ru.turn.finishedText)).toBeInTheDocument();
    expect(screen.queryByTestId('my-finish')).not.toBeInTheDocument();
  });

  it('turns from provisional to frozen when the hub says the proofs were approved', async () => {
    let frozen = false;
    serve(() => ({
      ...season({ finish: { order: 1, frozen } }, 'finish'),
      lastSequence: frozen ? 9 : 3,
    }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    expect(await screen.findByTestId('my-finish')).toHaveTextContent(
      ru.turn.myFinish.firstProvisional,
    );

    frozen = true;
    act(() => {
      hubChange?.();
    });

    expect(await screen.findByText(ru.turn.myFinish.firstFrozen)).toBeInTheDocument();
  });
});

describe('the wish reroll (D-325, D-261)', () => {
  function playing(open: boolean | undefined, techRerollOpen = true) {
    return season({
      phase: 'playing',
      activeRun: {
        id: runId,
        game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Persona 5', hours: 90 },
        startedAt: '2026-09-24T10:00:00Z',
      },
      techRerollOpen,
      techRerollUntil: '2026-09-25T10:00:00Z',
      ...(open === undefined ? {} : { wishRerollOpen: open }),
    });
  }

  it('is offered while the server says it is open and goes through the tech reroll with the reason «wish»', async () => {
    const commands = serve(() => playing(true));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('wish-reroll'));
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveTextContent(ru.turn.wishRerollTitle('Persona 5'));
    for (const line of ru.turn.wishRerollConsequences) expect(dialog).toHaveTextContent(line);
    await userEvent.click(within(dialog).getByTestId('wish-reroll-confirm'));

    expect(commands).toHaveLength(1);
    expect(commands[0]?.url).toMatch(new RegExp(`/api/seasons/${seasonId}/tech-reroll$`));
    expect(commands[0]?.body).toMatchObject({ reason: 'wish', comment: null });
  });

  it('cancels without a command', async () => {
    const commands = serve(() => playing(true));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('wish-reroll'));
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByTestId('wish-reroll-cancel'),
    );

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(commands).toHaveLength(0);
  });

  it.each([
    ['closed by the server', false],
    ['not said by an older server', undefined],
  ])('is not offered when %s', async (_case, open) => {
    serve(() => playing(open));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('active-run');
    expect(screen.queryByTestId('wish-reroll')).not.toBeInTheDocument();
  });

  it.each([
    'run.wishRerollNotListed',
    'run.wishRerollImposed',
    'run.techRerollWindowClosed',
  ] as const)('says the refusal %s in Russian', async (code) => {
    serve(
      () => playing(true),
      () => json(409, { title: 'rejected', status: 409, detail: null, code }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('wish-reroll'));
    await userEvent.click(
      within(await screen.findByRole('dialog')).getByTestId('wish-reroll-confirm'),
    );

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection[code]);
  });
});
