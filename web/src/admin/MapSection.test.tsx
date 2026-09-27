import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId, type Call } from '../test/fakeServer';
import {
  addCell,
  addEdge,
  normalized,
  removeCell,
  removeEdge,
  sameMap,
  setCellType,
  setDefaultBranch,
  type Draft,
} from './mapDraft';
import { MapSection } from './MapSection';

const t = ru.admin.map;

const published: Schemas['MapGraphView'] = {
  cells: [
    { id: 'start', type: 'start', x: 0, y: 0 },
    { id: 'c1', type: 'empty', x: 140, y: 0 },
    { id: 'finish', type: 'finish', x: 280, y: 0 },
  ],
  edges: [
    { from: 'start', to: 'c1', isDefaultForward: true, isPrimaryBackward: true },
    { from: 'c1', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
  ],
  zones: [],
};

const view = (overrides: Partial<Schemas['AdminMapView']> = {}): Schemas['AdminMapView'] => ({
  mode: 'graph',
  status: 'active',
  map: published,
  players: [{ playerId: 'p1', name: 'Вася', cellId: 'c1', finished: false, choosingBranch: false }],
  ...overrides,
});

const ok = { canPublish: true, problems: [], warnings: [] };

function desktop() {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query.includes('min-width'),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }));
}

beforeEach(() => {
  localStorage.clear();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

describe('the map draft', () => {
  const draft: Draft = { cells: published.cells ?? [], edges: published.edges ?? [], zones: [] };

  it('marks the first exit the default branch and the first entry the primary one', () => {
    const { id, draft: added } = addCell(draft, { x: 140, y: 100 });
    const forked = addEdge(addEdge(setCellType(added, 'c1', 'fork'), 'c1', id), id, 'finish');
    const exits = forked.edges.filter((e) => e.from === 'c1');
    expect(exits.map((e) => [e.to, e.isDefaultForward])).toEqual([
      ['finish', true],
      [id, false],
    ]);
    const entries = forked.edges.filter((e) => e.to === 'finish');
    expect(entries.map((e) => [e.from, e.isPrimaryBackward])).toEqual([
      ['c1', true],
      [id, false],
    ]);
    const switched = setDefaultBranch(forked, 'c1', id);
    expect(switched.edges.filter((e) => e.from === 'c1' && e.isDefaultForward)).toEqual([
      expect.objectContaining({ to: id }),
    ]);
  });

  it('never adds a loop or a repeated arrow, and hands the default on when it is removed', () => {
    expect(addEdge(draft, 'c1', 'c1')).toBe(draft);
    expect(addEdge(draft, 'start', 'c1')).toBe(draft);
    const { id, draft: added } = addCell(draft, { x: 0, y: 0 });
    const two = addEdge(added, 'c1', id);
    const left = removeEdge(two, 'c1', 'finish');
    expect(left.edges.filter((e) => e.from === 'c1')).toEqual([
      expect.objectContaining({ to: id, isDefaultForward: true }),
    ]);
  });

  it('removes a cell with its arrows and a teleport loses the way there', () => {
    const teleport = { ...setCellType(draft, 'start', 'teleport') };
    teleport.cells = teleport.cells.map((c) => (c.id === 'start' ? { ...c, to: 'c1' } : c));
    const gone = removeCell(teleport, 'c1');
    expect(gone.edges.some((e) => e.from === 'c1' || e.to === 'c1')).toBe(false);
    expect(gone.cells.find((c) => c.id === 'start')?.to).toBeNull();
  });

  it('a type change drops the old parameters and a points bonus starts at 1', () => {
    const bonus = setCellType(draft, 'c1', 'pointsBonus');
    expect(bonus.cells.find((c) => c.id === 'c1')?.amount).toBe(1);
    const plain = setCellType(bonus, 'c1', 'empty');
    expect(normalized(plain).cells.find((c) => c.id === 'c1')).toEqual({
      id: 'c1',
      type: 'empty',
      x: 140,
      y: 0,
    });
    expect(sameMap(plain, draft)).toBe(true);
  });
});

describe('the map editor', () => {
  it('checks the draft as it changes and shows every problem in words, the occupied cell included', async () => {
    desktop();
    const checks: Call[] = [];
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': (call) => {
        checks.push(call);
        const body = call.body as Schemas['MapGraphView'];
        return {
          body: body.cells?.some((c) => c.id === 'c1')
            ? { ...ok, canPublish: false }
            : {
                canPublish: false,
                problems: [
                  { code: 'map.occupiedCellRemoved', subject: 'c1', message: '' },
                  { code: 'map.deadEnd', subject: 'start', message: '' },
                ],
                warnings: [],
              },
        };
      },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    // The published map as it is: nothing to publish
    expect(await screen.findByText(t.check.unchanged)).toBeInTheDocument();
    expect(screen.getByTestId('map-publish-button')).toBeDisabled();

    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    expect(screen.getByTestId('map-cell')).toHaveTextContent(t.cell.players('Вася'));
    await userEvent.click(screen.getByTestId('map-cell-remove'));

    const problems = await screen.findByTestId('map-problems');
    expect(problems).toHaveTextContent(t.check.problems(2));
    expect(problems).toHaveTextContent(t.problem['map.occupiedCellRemoved']?.('c1') ?? '');
    expect(problems).toHaveTextContent(t.problem['map.deadEnd']?.('start') ?? '');
    expect(screen.getByTestId('map-changed')).toHaveTextContent(t.draftChanged);
    expect(checks.length).toBeGreaterThanOrEqual(2);
    // A problem about a cell selects it
    await userEvent.click(
      within(problems).getByRole('button', { name: t.problem['map.deadEnd']?.('start') ?? '' }),
    );
    expect(screen.getByTestId('map-cell')).toHaveTextContent(t.cell.title('start'));
  });

  it('builds a fork with the keyboard panel and publishes it after the confirmation', async () => {
    desktop();
    const server = fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': (call) => ({
        body: sameMap(call.body as Draft, published as Draft) ? { ...ok, canPublish: false } : ok,
      }),
      'POST /api/admin/seasons/*/map/publish': { duplicate: false, events: [] },
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.unchanged);

    // A new cell c2 between c1 and the finish, c1 a fork
    await userEvent.click(screen.getByTestId('map-add-cell'));
    expect(screen.getByTestId('map-cell')).toHaveTextContent(t.cell.title('c2'));
    await userEvent.selectOptions(screen.getByTestId('map-arrow-to'), 'finish');
    await userEvent.click(screen.getByTestId('map-arrow-add'));
    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    await userEvent.selectOptions(screen.getByTestId('map-cell-type'), 'fork');
    await userEvent.selectOptions(screen.getByTestId('map-arrow-to'), 'c2');
    await userEvent.click(screen.getByTestId('map-arrow-add'));
    expect(within(screen.getByTestId('map-exit-finish')).getByRole('checkbox')).toBeChecked();

    expect(await screen.findByText(t.check.ok)).toBeInTheDocument();
    // No comment, no window
    await userEvent.click(screen.getByTestId('map-publish-button'));
    expect(await screen.findByText(ru.admin.commentRequired)).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('map-publish-comment'), 'Развилка у c1');
    await userEvent.click(screen.getByTestId('map-publish-button'));
    const confirm = await screen.findByTestId('map-publish-confirm');
    expect(confirm).toHaveTextContent(t.publish.consequences[1]);
    await userEvent.click(within(confirm).getByRole('button', { name: t.publish.confirm }));

    const sent = server.sent('POST', '/map/publish')[0]?.body as {
      map: Draft;
      comment: string;
    };
    expect(sent.comment).toBe('Развилка у c1');
    expect(sent.map.cells.find((c) => c.id === 'c1')?.type).toBe('fork');
    expect(sent.map.edges).toContainEqual(
      expect.objectContaining({ from: 'c1', to: 'c2', isDefaultForward: false }),
    );
    expect(await screen.findByText(t.publish.done)).toBeInTheDocument();
  });

  it('says why the engine refused a publication', async () => {
    desktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
      'POST /api/admin/seasons/*/map/publish': () => ({
        status: 409,
        body: { title: 'x', status: 409, detail: null, code: 'map.branchChoicePending' },
      }),
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok);

    await userEvent.type(screen.getByTestId('map-publish-comment'), 'Новая');
    await userEvent.click(screen.getByTestId('map-publish-button'));
    await userEvent.click(
      within(await screen.findByTestId('map-publish-confirm')).getByRole('button', {
        name: t.publish.confirm,
      }),
    );

    expect(
      await screen.findByText(ru.admin.rejection['map.branchChoicePending'] ?? ''),
    ).toBeInTheDocument();
  });

  it('keeps the draft in this browser and brings back the published map', async () => {
    desktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    const first = render(<MapSection seasonId={seasonId} version={0} />);
    await userEvent.click(await screen.findByTestId('map-add-cell'));
    first.unmount();

    render(<MapSection seasonId={seasonId} version={0} />);
    expect(await screen.findByTestId('map-changed')).toBeInTheDocument();
    await userEvent.click(screen.getByTestId('map-reset'));
    expect(screen.queryByTestId('map-changed')).not.toBeInTheDocument();
  });

  it('says a linear season needs the graph mode first', async () => {
    desktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({ mode: 'linear' }),
      'POST /api/admin/seasons/*/map/check': { ...ok, canPublish: false },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByText(t.linear)).toBeInTheDocument();
  });

  it('shows the map read-only on a phone', async () => {
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByTestId('map-phone')).toHaveTextContent(t.phone);
    expect(screen.queryByTestId('map-editor')).not.toBeInTheDocument();
  });

  it('offers a retry when the page cannot load', async () => {
    fakeServer({ 'GET /api/admin/seasons/*/map': () => ({ status: 500 }) });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByText(ru.admin.loadErrorTitle)).toBeInTheDocument();
  });
});
