import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { graphBoard, legsPath } from '../board/graphBoard';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

// The season screen on the graph map (2.10): the real cells, teleports and zones, the branch choice at a fork on a
// phone and on a desktop, the walk along the chosen branch. The linear screen stays as it was (SeasonScreen.test.tsx).

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

const me = '10000000-0000-0000-0000-000000000001';
const choiceId = 'c0000000-0000-0000-0000-000000000001';

/** start → f (fork) → a1 → a2 → finish through the swamp, or f → b1 (+3) → t (teleport to a2) → b2 → finish */
const map: Pick<Schemas['SeasonView'], 'cells' | 'edges' | 'zones'> = {
  cells: [
    { id: 'start', type: 'start', x: 20, y: 40 },
    { id: 'f', type: 'fork', x: 120, y: 40 },
    { id: 'a1', type: 'empty', x: 220, y: 20, zone: 'swamp' },
    { id: 'a2', type: 'empty', x: 320, y: 20, zone: 'swamp' },
    { id: 'b1', type: 'pointsBonus', x: 220, y: 80, amount: 3 },
    { id: 't', type: 'teleport', x: 320, y: 80, to: 'a2' },
    { id: 'b2', type: 'checkpoint', x: 420, y: 80 },
    { id: 'finish', type: 'finish', x: 520, y: 40 },
  ],
  edges: [
    { from: 'start', to: 'f', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'f', to: 'a1', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'f', to: 'b1', isDefaultForward: false, isPrimaryBackward: true },
    { from: 'a1', to: 'a2', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'b1', to: 't', isDefaultForward: true, isPrimaryBackward: true },
    { from: 't', to: 'b2', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'a2', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'b2', to: 'finish', isDefaultForward: true, isPrimaryBackward: false },
  ],
  zones: [
    {
      id: 'swamp',
      name: 'Болото ужаса',
      rollFilter: { tags: ['Horror'] },
      diceModifier: { stage: 'add', value: 1 },
      dropPenaltyMultiplier: 1.5,
    },
  ],
};

const branchChoice: Schemas['ChoiceView'] = {
  id: choiceId,
  kind: 'branch',
  options: [
    { id: 'a1', game: null },
    { id: 'b1', game: null },
  ],
  steps: 2,
};

function season(cell: string, overrides: Partial<Schemas['MyTurnView']> = {}, sequence = 3) {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    ...map,
    mapMode: 'graph',
    players: [
      {
        id: me,
        name: 'Вася',
        cellId: cell,
        points: 0,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      {
        playerId: me,
        place: 1,
        points: 0,
        cellsToFinish: 3,
        isFirst: false,
        provisional: false,
      },
    ],
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
      lastMove: null,
      ...overrides,
    },
    lastSequence: sequence,
    name: 'Сезон на карте',
  } satisfies Schemas['SeasonView'];
}

const common = {
  'GET /api/auth/me': {
    id: 'u',
    login: 'vasya',
    name: 'Вася',
    role: 'player',
    mustChangePassword: false,
    avatar: null,
  },
  'GET /api/seasons/*/feed': { entries: [], nextBefore: null, players: [], games: [], runs: [] },
};

function desktop() {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the graph board', () => {
  it('numbers the cells from the start along the arrows, the default branch first', () => {
    const { board, cellNumber } = graphBoard(map);
    expect(cellNumber.get('start')).toBe(1);
    expect(cellNumber.get('f')).toBe(2);
    expect(cellNumber.get('a1')).toBe(3);
    expect(cellNumber.get('b1')).toBe(4);
    const teleport = board.cells.find((c) => c.kind === 'teleport');
    expect(teleport?.to).toBe(cellNumber.get('a2'));
    expect(board.zones.map((z) => z.name)).toEqual(['Болото ужаса']);
    expect(board.edges).toHaveLength(8);
  });

  it('lays out a map without places in columns from the start', () => {
    const { board } = graphBoard({
      ...map,
      cells: map.cells.map((c) => ({ ...c, x: null, y: null })),
    });
    const x = (id: number) => board.cells.find((c) => c.id === id)?.x ?? 0;
    expect(x(1)).toBeLessThan(x(2));
    expect(x(2)).toBeLessThan(x(3));
  });

  it('walks the legs of a move with a teleport as a flight', () => {
    const { cellNumber } = graphBoard(map);
    const walked = legsPath(
      [
        { from: 'f', path: ['b1', 't'], reason: 'completionRoll' },
        { from: 't', path: ['a2'], reason: 'teleport' },
      ],
      cellNumber,
    );
    expect(walked?.path).toEqual(['f', 'b1', 't', 'a2'].map((id) => cellNumber.get(id)));
    expect(walked?.jumps).toEqual([3]);
  });
});

describe('the season screen on the graph map', () => {
  it('lists the cells with what they do and explains the zones', async () => {
    fakeServer({ ...common, 'GET /api/seasons/*': season('a1') });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const cells = await screen.findByTestId('cells');
    expect(within(cells).getByTestId('cell-t')).toHaveTextContent(
      `${ru.map.cellNumber(6)}, ${ru.map.cellKinds['teleport'] ?? ''}, ${ru.map.teleportTo(5)}`,
    );
    expect(within(cells).getByTestId('cell-b1')).toHaveTextContent(ru.map.bonusAmount(3));
    expect(within(cells).getByTestId('cell-a1')).toHaveTextContent(ru.map.inZone('Болото ужаса'));
    const zone = screen.getByTestId('zone-swamp');
    expect(zone).toHaveTextContent(ru.map.zones.here);
    expect(zone).toHaveTextContent(ru.map.zones.filter(ru.map.zones.tags(['Horror'])));
    expect(zone).toHaveTextContent(ru.map.zones.diceAdd(1));
    expect(zone).toHaveTextContent(ru.map.zones.drop(1.5));
    expect(screen.getByTestId('map-symbols')).toHaveTextContent(ru.map.legend.teleport);
    expect(screen.getByTestId('map-symbols')).toHaveTextContent(ru.map.legend.checkpoint);
    // The map marks the teleport's jump
    expect(document.querySelectorAll('[data-teleport]')).toHaveLength(1);
  });

  it('asks for the branch in the turn card on a phone, with where each leads, and sends the pick', async () => {
    const server = fakeServer({
      ...common,
      'GET /api/seasons/*': season('f', { choice: branchChoice }),
      'POST /api/seasons/*/choose': { duplicate: false, events: [] },
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const branch = await within(await screen.findByTestId('turn')).findByTestId('branch');
    expect(branch).toHaveTextContent(ru.map.branch.lead(2));
    // Two steps: into a1, then a2; into b1, then the teleport t that leads to a2
    expect(within(branch).getByTestId('branch-a1')).toHaveTextContent(ru.map.branch.endCell(5));
    expect(within(branch).getByTestId('branch-a1')).toHaveTextContent('Болото ужаса');
    expect(within(branch).getByTestId('branch-b1')).toHaveTextContent(
      ru.map.branch.endTeleport(6, 5),
    );
    // No roll while the throw waits for its branch
    expect(screen.queryByTestId('roll')).not.toBeInTheDocument();
    // The map rings the options, numbered as the buttons
    expect(document.querySelectorAll('[data-option]')).toHaveLength(2);

    await userEvent.click(within(branch).getByTestId('branch-b1'));

    expect(server.sent('POST', '/choose').map((c) => c.body)).toEqual([
      expect.objectContaining({ choiceId, optionId: 'b1' }),
    ]);
  });

  it('picks a branch with the keyboard', async () => {
    const server = fakeServer({
      ...common,
      'GET /api/seasons/*': season('f', { choice: branchChoice }),
      'POST /api/seasons/*/choose': { duplicate: false, events: [] },
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    const option = await screen.findByRole('button', { name: new RegExp(ru.map.branch.go(1)) });

    option.focus();
    await userEvent.keyboard('{Enter}');

    expect(server.sent('POST', '/choose')[0]?.body).toEqual(
      expect.objectContaining({ optionId: 'a1' }),
    );
  });

  it('asks for the branch on the map on a desktop and points there from the turn card', async () => {
    desktop();
    fakeServer({ ...common, 'GET /api/seasons/*': season('f', { choice: branchChoice }) });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const branch = await screen.findByTestId('branch');
    expect(within(screen.getByTestId('turn')).queryByTestId('branch')).not.toBeInTheDocument();
    expect(screen.getByTestId('branch-hint')).toHaveTextContent(ru.map.branch.onMap);
    expect(within(branch).getAllByRole('button')).toHaveLength(2);
  });

  it('a branch choice that comes while the page is open takes the focus', async () => {
    let current = season('start');
    fakeServer({ ...common, 'GET /api/seasons/*': () => ({ body: current }) });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');

    current = season('f', { choice: branchChoice }, 5);
    hubChange?.();

    const title = await screen.findByText(ru.map.branch.title);
    await vi.waitFor(() => {
      expect(title.closest('[tabindex]')).toHaveFocus();
    });
  });

  it('walks the chosen branch, the teleport included, and says where the token stands', async () => {
    let current = season('f', {
      choice: branchChoice,
      lastMove: { sequence: 3, legs: [{ from: 'start', path: ['f'], reason: 'completionRoll' }] },
    });
    fakeServer({
      ...common,
      'GET /api/seasons/*': () => ({ body: current }),
      'POST /api/seasons/*/choose': () => {
        current = season(
          'a2',
          {
            lastMove: {
              sequence: 7,
              legs: [
                { from: 'f', path: ['b1', 't'], reason: 'completionRoll' },
                { from: 't', path: ['a2'], reason: 'teleport' },
              ],
            },
          },
          7,
        );
        return { body: { duplicate: false, events: [] } };
      },
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('branch-b1'));

    const walk = await screen.findByTestId('walk');
    // The roll waits for the token
    expect(screen.queryByTestId('roll')).not.toBeInTheDocument();
    await userEvent.click(within(walk).getByRole('button', { name: ru.moments.skip }));
    expect(screen.queryByTestId('walk')).not.toBeInTheDocument();
    expect(screen.getByTestId('roll-announce')).toHaveTextContent(ru.map.branch.announce(5));
    expect(screen.getByTestId('roll')).toBeInTheDocument();
  });

  it('draws the linear season as before: no legend, no branch', async () => {
    fakeServer({
      ...common,
      'GET /api/seasons/*': {
        ...season('start'),
        cells: [
          { id: 'start', type: 'start' },
          { id: 'c1', type: 'empty' },
          { id: 'finish', type: 'finish' },
        ],
        edges: [],
        zones: [],
        mapMode: 'linear',
      },
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await screen.findByTestId('roll');
    expect(screen.queryByTestId('map-legend')).not.toBeInTheDocument();
    expect(screen.getByTestId('cell-c1')).toHaveTextContent(ru.map.cellNumber(2));
  });
});
