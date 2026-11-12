import { onDesktop } from '../test/desk';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId, type Call } from '../test/fakeServer';
import {
  addCell,
  addEdge,
  draftOf,
  loadDraft,
  normalized,
  removeCell,
  removeEdge,
  sameMap,
  saveDraft,
  setCell,
  setCellType,
  setDefaultBranch,
  type Draft,
} from './mapDraft';
import { checkDelayMs, MapSection } from './MapSection';
import { pastTheDelay } from '../test/waits';

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
  version: 0,
  events: false,
  shop: false,
  ...overrides,
});

const ok = { canPublish: true, problems: [], warnings: [] };
// The editor asks the server to check the draft once the edits rest (FL-2)
const afterEdit = pastTheDelay(checkDelayMs);

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

describe('the kept draft', () => {
  it("equals the server's map whatever order the edits added the fields in", () => {
    const { id, draft: added } = addCell(draftOf(published), { x: 420, y: 0 });
    const edited = setCell(setCellType(added, id, 'teleport'), id, { zone: 'swamp', to: 'c1' });
    // The server lists every field of a cell in its own order, the empty ones as null
    const fromServer: Draft = {
      ...edited,
      cells: edited.cells.map((c) => ({
        id: c.id,
        type: c.type,
        zone: c.zone ?? null,
        to: c.to ?? null,
        amount: null,
        deck: null,
        grants: null,
        x: c.x ?? null,
        y: c.y ?? null,
      })),
    };
    expect(sameMap(edited, fromServer)).toBe(true);
  });

  it('is dropped when the map was published again since it was made', () => {
    const base = draftOf(published);
    saveDraft(seasonId, { base, draft: addCell(base, { x: 0, y: 0 }).draft });
    const republished = addEdge(addCell(base, { x: 9, y: 9 }).draft, 'c1', 'c2');

    expect(loadDraft(seasonId, republished)).toEqual({ draft: null, stale: true });
    expect(loadDraft(seasonId, base)).toEqual({ draft: null, stale: false });
  });
});

describe('the map editor', () => {
  it('checks the draft as it changes and shows every problem in words, the occupied cell included', async () => {
    onDesktop();
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
    expect(await screen.findByText(t.check.unchanged, {}, afterEdit)).toBeInTheDocument();
    expect(screen.getByTestId('map-publish-button')).toBeDisabled();

    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    expect(screen.getByTestId('map-cell')).toHaveTextContent(t.cell.players('Вася'));
    await userEvent.click(screen.getByTestId('map-cell-remove'));

    const problems = await screen.findByTestId('map-problems', {}, afterEdit);
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

  it('names the bounds when a bonus is past them, not only a missing parameter (K1M-5)', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': {
        canPublish: false,
        problems: [{ code: 'map.cellParameter', subject: 'c1', message: '' }],
        warnings: [],
      },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    // The engine turns a bonus of 20 000 down with this code: the admin learns the ceiling from the words
    const problems = await screen.findByTestId('map-problems', {}, afterEdit);
    expect(problems).toHaveTextContent('c1');
    expect(problems).toHaveTextContent('10 000');
  });

  it('builds a fork with the keyboard panel and publishes it after the confirmation', async () => {
    onDesktop();
    const server = fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': (call) => ({
        body: sameMap(call.body as Draft, published as Draft) ? { ...ok, canPublish: false } : ok,
      }),
      'POST /api/admin/seasons/*/map/publish': { duplicate: false, events: [] },
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.unchanged, {}, afterEdit);

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

    expect(await screen.findByText(t.check.ok, {}, afterEdit)).toBeInTheDocument();
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
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
      'POST /api/admin/seasons/*/map/publish': () => ({
        status: 409,
        body: { title: 'x', status: 409, detail: null, code: 'map.branchChoicePending' },
      }),
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok, {}, afterEdit);

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
    onDesktop();
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

  it('warns about a zone with few games without blocking the publication', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        map: { ...published, zones: [{ id: 'swamp', name: 'Болото ужаса' }] },
      }),
      'POST /api/admin/seasons/*/map/check': {
        ...ok,
        warnings: [{ zoneId: 'swamp', available: 4, wanted: 15 }],
      },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(
      await screen.findByText(t.check.warning('Болото ужаса', 4, 15), {}, afterEdit),
    ).toBeInTheDocument();
    expect(screen.getByText(t.check.ok)).toBeInTheDocument();
    expect(screen.getByTestId('map-publish-button')).toBeEnabled();
  });

  it("keeps the map's settings in one column beside the cell panel", async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    const settings = await screen.findByTestId('map-settings');
    // The background and the zones stacked together: the two-column grid never wraps a third panel under the cell
    expect([...settings.children].map((c) => c.getAttribute('data-testid'))).toEqual([
      'map-background',
      'map-zones',
    ]);
    expect(settings.parentElement?.children).toHaveLength(2);
  });

  it('says when the kept draft was dropped because the map was published since', async () => {
    onDesktop();
    const older = { cells: [{ id: 'start', type: 'start' as const }], edges: [], zones: [] };
    saveDraft(seasonId, { base: older, draft: older });
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByText(t.draftStale)).toBeInTheDocument();
    expect(screen.queryByTestId('map-changed')).not.toBeInTheDocument();
  });

  it('offers no event or shop cell while those mechanics are off (K1A, D-302)', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view(),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok, {}, afterEdit);

    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    const options = within(screen.getByTestId('map-cell-type'))
      .getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value);
    expect(options).not.toContain('event');
    expect(options).not.toContain('shop');
    expect(options).toContain('teleport');
  });

  it('offers the event and shop cells once their mechanics are on', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({ events: true, shop: true }),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok, {}, afterEdit);

    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    const options = within(screen.getByTestId('map-cell-type'))
      .getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value);
    expect(options).toEqual(expect.arrayContaining(['event', 'shop']));
  });

  it('names the zone and the field of each zone error (K1A)', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        map: { ...published, zones: [{ id: 'zone-1', name: 'Зона 1' }] },
      }),
      'POST /api/admin/seasons/*/map/check': {
        canPublish: false,
        problems: [
          {
            code: 'map.zoneInvalid',
            subject: 'zone-1',
            message: '$.rollFilter.minHours: «minHours» is at most «maxHours».',
          },
          {
            code: 'map.zoneInvalid',
            subject: 'zone-1',
            message: '$.dropPenaltyMultiplier: A multiplier is above 0.',
          },
        ],
        warnings: [],
      },
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    const problems = await screen.findByTestId('map-problems', {}, afterEdit);
    expect(problems).toHaveTextContent(t.zoneProblem('Зона 1', '$.rollFilter.minHours'));
    expect(problems).toHaveTextContent(t.zoneProblem('Зона 1', '$.dropPenaltyMultiplier'));
    expect(t.zoneProblem('Зона 1', '$.rollFilter.minHours')).not.toBe(
      t.zoneProblem('Зона 1', '$.dropPenaltyMultiplier'),
    );
    expect(problems).not.toHaveTextContent('zone-1');
  });

  it('says at the field that a zone number is not a number', async () => {
    onDesktop();
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        map: { ...published, zones: [{ id: 'zone-1', name: 'Зона 1' }] },
      }),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    const zone = await screen.findByTestId('map-zone-zone-1');

    await userEvent.type(within(zone).getByLabelText(t.zone.drop, { exact: false }), 'abc');

    expect(within(zone).getByText(t.zone.notNumber)).toBeInTheDocument();
  });

  it('publishes naming the map version the draft was made from (K1A, D-616)', async () => {
    onDesktop();
    const server = fakeServer({
      'GET /api/admin/seasons/*/map': view({ version: 3 }),
      'POST /api/admin/seasons/*/map/check': (call) => ({
        body: sameMap(call.body as Draft, published as Draft) ? { ...ok, canPublish: false } : ok,
      }),
      'POST /api/admin/seasons/*/map/publish': { duplicate: false, events: [] },
    });
    render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.unchanged, {}, afterEdit);

    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    await userEvent.selectOptions(screen.getByTestId('map-cell-type'), 'checkpoint');
    await screen.findByText(t.check.ok, {}, afterEdit);
    await userEvent.type(screen.getByTestId('map-publish-comment'), 'Чекпоинт');
    await userEvent.click(screen.getByTestId('map-publish-button'));
    await userEvent.click(
      within(await screen.findByTestId('map-publish-confirm')).getByRole('button', {
        name: t.publish.confirm,
      }),
    );

    expect(await screen.findByText(t.publish.done)).toBeInTheDocument();
    expect(server.sent('POST', '/map/publish')[0]?.body).toMatchObject({ expectedVersion: 3 });
  });

  it('says a map published since in another tab made the open draft stale', async () => {
    onDesktop();
    let current = view({ version: 0 });
    fakeServer({
      'GET /api/admin/seasons/*/map': () => ({ body: current }),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    const { rerender } = render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok, {}, afterEdit);
    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    await userEvent.selectOptions(screen.getByTestId('map-cell-type'), 'checkpoint');

    // Another tab publishes a bonus on c1: the season's version moves on and the map is read again
    current = view({
      version: 1,
      map: {
        ...published,
        cells: (published.cells ?? []).map((c) =>
          c.id === 'c1' ? { ...c, type: 'pointsBonus', amount: 9 } : c,
        ),
      },
    });
    rerender(<MapSection seasonId={seasonId} version={1} />);

    expect(await screen.findByText(t.draftOutdated)).toBeInTheDocument();

    // Back to the published map: the newer one, and the warning goes
    await userEvent.click(screen.getByRole('button', { name: t.reset }));
    expect(screen.queryByText(t.draftOutdated)).toBeNull();
    await userEvent.selectOptions(screen.getByTestId('map-cell-pick'), 'c1');
    expect(screen.getByTestId('map-cell-type')).toHaveValue('pointsBonus');
  });

  it('takes a map published since when the draft has no edits of its own', async () => {
    onDesktop();
    let current = view({ version: 0 });
    fakeServer({
      'GET /api/admin/seasons/*/map': () => ({ body: current }),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    const { rerender } = render(<MapSection seasonId={seasonId} version={0} />);
    await screen.findByText(t.check.ok, {}, afterEdit);

    current = view({
      version: 1,
      map: {
        ...published,
        cells: (published.cells ?? []).map((c) =>
          c.id === 'c1' ? { ...c, type: 'checkpoint' } : c,
        ),
      },
    });
    rerender(<MapSection seasonId={seasonId} version={1} />);

    await userEvent.selectOptions(await screen.findByTestId('map-cell-pick'), 'c1');
    await vi.waitFor(() => {
      expect(screen.getByTestId('map-cell-type')).toHaveValue('checkpoint');
    });
    expect(screen.queryByText(t.draftOutdated)).toBeNull();
  });

  it('says a linear season needs the graph mode first', async () => {
    onDesktop();
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

  it('draws no token on the start for a player on a cell the map does not know', async () => {
    fakeServer({
      'GET /api/admin/seasons/*/map': view({
        players: [
          { playerId: 'p1', name: 'Вася', cellId: 'c1', finished: false, choosingBranch: false },
          { playerId: 'p2', name: 'Лиса', cellId: 'gone', finished: false, choosingBranch: false },
        ],
      }),
      'POST /api/admin/seasons/*/map/check': ok,
    });
    render(<MapSection seasonId={seasonId} version={0} />);

    await screen.findByTestId('map-phone');
    // Cell number 0 is the start since the numbering from 0 (D-622): an unknown cell is not drawn as the start
    expect(document.querySelector('[data-player="p1"]')).toBeInTheDocument();
    expect(document.querySelector('[data-player="p2"]')).not.toBeInTheDocument();
  });

  it('offers a retry when the page cannot load', async () => {
    fakeServer({ 'GET /api/admin/seasons/*/map': () => ({ status: 500 }) });
    render(<MapSection seasonId={seasonId} version={0} />);

    expect(await screen.findByText(ru.admin.loadErrorTitle)).toBeInTheDocument();
  });
});
