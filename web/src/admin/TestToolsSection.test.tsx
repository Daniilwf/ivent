import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { ru } from '../i18n/ru';
import { adminUser, answer, fakeServer, seasonId, type Handler } from '../test/fakeServer';
import { AdminScreen } from './AdminScreen';
import { forgetSiteEnvironment } from '../app/siteStatus';

// H9, D-221: the test tools — scenarios, the site's clock and its randomness — for the admin, only where the test
// endpoints exist (Development and Test); on the live site the section is neither listed nor opened.

vi.mock('../api/realtime', () => ({ watchSeason: () => () => undefined }));

const t = ru.admin.test;
const vasya = '10000000-0000-0000-0000-000000000001';
const petya = '10000000-0000-0000-0000-000000000002';

const player = (id: string, name: string) => ({
  id,
  userId: `u-${name}`,
  name,
  cellId: 'start',
  points: 0,
  coins: 0,
  resources: {},
  phase: 'idle',
  playing: false,
  isInactive: false,
  lastActionAt: null,
  inactiveHint: false,
});

const tools = (shiftMinutes = 0, seed: number | null = null) => ({
  clock: { now: '2026-10-01T09:00:00Z', adjustable: true, shiftMinutes },
  random: { seed, seedable: true },
  scenarios: ['finish-soon', 'deadline-in-hour', 'five-manual-effects'],
});

const status = (testTools: boolean, environment = testTools ? 'development' : 'production') => ({
  maintenance: false,
  version: '1',
  environment,
  testTools,
});

function site(routes: Record<string, Handler | object> = {}, testTools = true) {
  return fakeServer({
    'GET /api/status': status(testTools),
    'GET /api/test': tools(),
    'GET /api/admin/seasons/*/proofs': [],
    'GET /api/admin/seasons/*/players': [player(vasya, 'Вася'), player(petya, 'Петя')],
    ...routes,
  });
}

function open(seasonOf: string | null = seasonId) {
  render(<AdminScreen section="test" currentSeasonId={seasonOf} user={adminUser} />);
}

describe('The test tools', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.pushState(null, '', '/');
  });

  it('show a skeleton, then the copy of the site, the clock, the randomness and the scenarios', async () => {
    site();
    open();

    expect(screen.getByTestId('loading')).toHaveAttribute('aria-busy', 'true');
    expect(await screen.findByTestId('test-clock-now')).toHaveTextContent('1 октября, 12:00 МСК');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(ru.admin.sections.test);
    expect(screen.getByTestId('test-clock-shift')).toHaveTextContent(t.realTime);
    expect(screen.queryByTestId('clock-reset')).toBeNull();
    expect(screen.getByTestId('test-random-state')).toHaveTextContent(t.unseeded);
    expect(screen.queryByTestId('random-unseed')).toBeNull();
    const choice = await screen.findByTestId('scenario-choice');
    for (const name of ['finish-soon', 'deadline-in-hour', 'five-manual-effects'])
      expect(
        within(choice).getByRole('radio', { name: t.scenarioNames[name] ?? name }),
      ).toBeInTheDocument();
    expect(screen.getByTestId('scenario-about')).toHaveTextContent(
      t.scenarioAbout['finish-soon'] ?? '',
    );
    // The one main action of the page
    expect(screen.getByTestId('scenario-load')).toHaveAttribute('data-variant', 'main');
  });

  it('load a scenario onto the picked player and say what it did', async () => {
    const server = site({
      'POST /api/test/seasons/*/scenarios/*': {
        scenario: 'finish-soon',
        commands: ['AdjustPlayer'],
      },
    });
    open();

    await userEvent.selectOptions(await screen.findByTestId('scenario-player'), petya);
    await userEvent.click(screen.getByTestId('scenario-load'));

    expect(
      await screen.findByText(t.loaded(ru.admin.log.commands['AdjustPlayer'] ?? '')),
    ).toBeInTheDocument();
    const sent = server.sent('POST', `/api/test/seasons/${seasonId}/scenarios/finish-soon`);
    expect(sent.map((c) => c.body)).toEqual([{ playerId: petya }]);
  });

  it('load the deadline without a player', async () => {
    const server = site({
      'POST /api/test/seasons/*/scenarios/*': {
        scenario: 'deadline-in-hour',
        commands: ['SetSeasonDeadline'],
      },
    });
    open();

    await userEvent.click(
      await screen.findByRole('radio', { name: t.scenarioNames['deadline-in-hour'] ?? '' }),
    );
    expect(screen.queryByTestId('scenario-player')).toBeNull();
    expect(screen.getByTestId('scenario-about')).toHaveTextContent(
      t.scenarioAbout['deadline-in-hour'] ?? '',
    );
    await userEvent.click(screen.getByTestId('scenario-load'));

    await waitFor(() => {
      expect(server.sent('POST', '/scenarios/deadline-in-hour').map((c) => c.body)).toEqual([{}]);
    });
  });

  it('say in Russian why a scenario was refused', async () => {
    site({
      'POST /api/test/seasons/*/scenarios/*': answer(409, {
        title: 'The test action was refused.',
        status: 409,
        code: 'test.playerUnknown',
      }),
    });
    open();

    await userEvent.click(await screen.findByTestId('scenario-load'));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      ru.admin.rejection['test.playerUnknown'] ?? '',
    );
  });

  it('offer no scenario without players, and send the admin to the season without a season', async () => {
    site({ 'GET /api/admin/seasons/*/players': [] });
    const { unmount } = render(
      <AdminScreen section="test" currentSeasonId={seasonId} user={adminUser} />,
    );
    expect(await screen.findByText(t.noPlayers)).toBeInTheDocument();
    expect(screen.getByTestId('scenario-load')).toBeDisabled();
    unmount();
    // Another page: the site is asked again
    forgetSiteEnvironment();

    site();
    open(null);
    expect(await screen.findByText(t.noSeason)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: ru.admin.toSeason }));
    expect(globalThis.location.pathname).toBe('/admin/season');
  });

  it('move the clock an hour on and show the shift', async () => {
    let shift = 0;
    const server = site({
      'GET /api/test': () => ({ body: tools(shift) }),
      'POST /api/test/clock': () => {
        shift = 60;
        return { body: tools(shift).clock };
      },
    });
    open();

    await userEvent.click(await screen.findByTestId('clock-plus-hour'));

    expect(await screen.findByText(t.shift('+1 ч'))).toBeInTheDocument();
    expect(screen.getByText(t.moved)).toBeInTheDocument();
    expect(server.sent('POST', '/api/test/clock').map((c) => c.body)).toEqual([
      { advanceMinutes: 60, reset: false },
    ]);
  });

  it('move the clock to a Moscow moment, asking for one first', async () => {
    const server = site({ 'POST /api/test/clock': tools().clock });
    open();

    await userEvent.click(await screen.findByTestId('clock-move'));
    expect(await screen.findByText(t.moveToRequired)).toBeInTheDocument();
    expect(server.sent('POST', '/api/test/clock')).toHaveLength(0);

    await userEvent.type(screen.getByTestId('clock-moment'), '2026-12-31T23:00');
    await userEvent.click(screen.getByTestId('clock-move'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/test/clock').map((c) => c.body)).toEqual([
        { moveTo: '2026-12-31T23:00:00+03:00', reset: false },
      ]);
    });
  });

  it('bring a moved clock back to the real time', async () => {
    const server = site({
      'GET /api/test': tools(-(2 * 1440 + 90)),
      'POST /api/test/clock': tools().clock,
    });
    open();

    expect(await screen.findByTestId('test-clock-shift')).toHaveTextContent(
      t.shift('−2 дн 1 ч 30 мин'),
    );
    await userEvent.click(screen.getByTestId('clock-reset'));

    expect(await screen.findByText(t.wasReset)).toBeInTheDocument();
    expect(server.sent('POST', '/api/test/clock').map((c) => c.body)).toEqual([{ reset: true }]);
  });

  it('seed the randomness with a whole number only, and make it random again', async () => {
    let seed: number | null = null;
    const server = site({
      'GET /api/test': () => ({ body: tools(0, seed) }),
      'POST /api/test/random': (call) => {
        seed = (call.body as { seed: number | null }).seed;
        return { status: 204 };
      },
    });
    open();

    await userEvent.type(await screen.findByTestId('random-seed'), '4.2');
    await userEvent.click(screen.getByTestId('random-set'));
    expect(await screen.findByText(t.seedInvalid)).toBeInTheDocument();
    expect(server.sent('POST', '/api/test/random')).toHaveLength(0);

    await userEvent.clear(screen.getByTestId('random-seed'));
    await userEvent.type(screen.getByTestId('random-seed'), '42');
    await userEvent.click(screen.getByTestId('random-set'));
    expect(await screen.findByText(t.seeded(42))).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('random-unseed'));
    expect(await screen.findByText(t.unseeded)).toBeInTheDocument();
    expect(server.sent('POST', '/api/test/random').map((c) => c.body)).toEqual([
      { seed: 42 },
      { seed: null },
    ]);
  });

  it('offer no moves for a clock or randomness the site cannot change', async () => {
    site({
      'GET /api/test': {
        ...tools(),
        clock: { ...tools().clock, adjustable: false },
        random: { seed: null, seedable: false },
      },
    });
    open();

    expect(
      await screen.findByText(ru.admin.rejection['test.clockFixed'] ?? ''),
    ).toBeInTheDocument();
    expect(screen.getByText(ru.admin.rejection['test.randomFixed'] ?? '')).toBeInTheDocument();
    expect(screen.queryByTestId('clock-plus-hour')).toBeNull();
    expect(screen.queryByTestId('random-set')).toBeNull();
  });

  it('say so when the tools do not load, with a retry', async () => {
    site({ 'GET /api/test': answer(500) });
    open();

    expect(await screen.findByText(ru.admin.loadErrorTitle)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: ru.ui.retry })).toBeInTheDocument();
  });
});

describe('The test tools on the live site', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    globalThis.history.pushState(null, '', '/');
  });

  function app(testTools: boolean) {
    return site(
      {
        'GET /api/auth/me': adminUser,
        'GET /api/seasons/current': { id: seasonId },
      },
      testTools,
    );
  }

  it('are listed among the admin sections only where they exist', async () => {
    globalThis.history.pushState(null, '', '/admin/site');
    app(true);
    const { unmount } = render(<App />);
    // On a phone the sections are behind «Разделы»
    await userEvent.click(await screen.findByTestId('admin-sections'));
    expect(await screen.findByTestId('admin-nav-test')).toHaveTextContent(ru.admin.sections.test);
    unmount();
    // Another page: the site is asked again
    forgetSiteEnvironment();

    const live = app(false);
    render(<App />);
    await waitFor(() => {
      expect(live.sent('GET', '/api/status').length).toBeGreaterThan(0);
    });
    await userEvent.click(await screen.findByTestId('admin-sections'));
    expect(await screen.findByTestId('admin-nav-site')).toBeInTheDocument();
    expect(screen.queryByTestId('admin-nav-test')).toBeNull();
    expect(live.calls.some((c) => c.path.startsWith('/api/test'))).toBe(false);
  });

  it('are an address the live site does not have, and nothing of them is asked for', async () => {
    globalThis.history.pushState(null, '', '/admin/test');
    const server = app(false);

    render(<App />);

    expect(await screen.findByTestId('page-not-found')).toBeInTheDocument();
    expect(screen.queryByText(ru.admin.sections.test)).toBeNull();
    expect(server.calls.some((c) => c.path.startsWith('/api/test'))).toBe(false);
  });
});
