import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { SIGNED_OUT_EVENT, type Schemas } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { ru } from '../i18n/ru';
import { fakeServer, json, type Handler } from '../test/fakeServer';
import { PageHeader } from '../ui/PageHeader';
import { navigate, paths, routeOf } from './router';

type Navigation = Schemas['SeasonNavigationView'];
type Subscription = { id: string; notify: (updates: SeasonUpdate[]) => void; stopped: boolean };
type HomeProps = { seasonId: string; onNavigationLoaded?: (navigation: Navigation) => void };

const observed = vi.hoisted(() => ({
  deferJoin: false,
  subscriptions: [] as Subscription[],
  pollsRendered: [] as string[],
  // Preserve the first evaluation across cases: an eager import is detected even if another case later visits polls.
  pollsFirstLoadPath: null as string | null,
}));

vi.mock('../api/realtime', () => ({
  watchSeason: (id: string, notify: (updates: SeasonUpdate[]) => void) => {
    const subscription = { id, notify, stopped: false };
    observed.subscriptions.push(subscription);
    if (!observed.deferJoin) notify([]);
    return () => {
      subscription.stopped = true;
    };
  },
  watchPool: () => () => undefined,
}));

// These pages perform no requests or subscriptions themselves, so an App-level navigation read is observable.
vi.mock('../season/SeasonScreen', () => ({
  SeasonScreen: ({ seasonId, onNavigationLoaded }: HomeProps) => (
    <main data-testid="navigation-home" data-season-id={seasonId}>
      <PageHeader>Главная макета</PageHeader>
      <button
        onClick={() =>
          onNavigationLoaded?.({
            id: seasonId,
            pollsEnabled: true,
            galleryEnabled: true,
            interactionFeatures: { coop: false, duels: true, partnerBoard: false },
          })
        }
      >
        Главная загружена: включено
      </button>
      <button onClick={() => onNavigationLoaded?.({ id: seasonId, pollsEnabled: false })}>
        Главная загружена: выключено
      </button>
    </main>
  ),
}));

vi.mock('../feed/FeedScreen', () => ({
  FeedScreen: ({ seasonId }: { seasonId: string }) => (
    <main data-testid="navigation-feed" data-season-id={seasonId}>
      <PageHeader>Лента макета</PageHeader>
    </main>
  ),
}));

// Root supplies the new typed lazy-screen stub before this draft becomes runnable.
vi.mock('../polls/PollsScreen', () => {
  observed.pollsFirstLoadPath ??= globalThis.location.pathname;
  return {
    PollsScreen: ({ seasonId }: { seasonId: string }) => {
      observed.pollsRendered.push(seasonId);
      return (
        <main data-testid="navigation-polls" data-season-id={seasonId}>
          <PageHeader>Голосования макета</PageHeader>
        </main>
      );
    },
  };
});

const seasonA = 'aa110000-0000-4000-8000-000000000001';
const seasonB = 'bb220000-0000-4000-8000-000000000002';
const user = {
  id: 'u1',
  login: 'vasya',
  name: 'Вася',
  role: 'player',
  mustChangePassword: false,
  avatar: null,
};
const nav = (id: string, pollsEnabled: boolean): Navigation => ({ id, pollsEnabled });
const requestSeason = (path: string) => {
  const id = path.split('/')[3];
  if (!id) throw new Error('Expected a season-scoped navigation request');
  return id.toLowerCase();
};

function site(navigation: Handler = ({ path }) => ({ body: nav(requestSeason(path), false) })) {
  return fakeServer({
    'GET /api/auth/me': user,
    'GET /api/seasons/current': { id: seasonA },
    'GET /api/seasons/*/navigation': navigation,
  });
}

function deferredResponse() {
  let resolve: (response: Response) => void = () => {
    throw new Error('Response gate is not ready');
  };
  const promise = new Promise<Response>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

function at(path: string) {
  globalThis.history.replaceState(null, '', path);
}

function expectMenuHidden() {
  expect(screen.queryByTestId('nav-more')).not.toBeInTheDocument();
}

async function expectLinkTo(id: string) {
  const person = userEvent.setup();
  await person.click(await screen.findByTestId('nav-more'));
  const link = await screen.findByRole('menuitem', { name: ru.nav.polls });
  expect(link.getAttribute('href')?.toLowerCase()).toBe(paths.polls(id.toLowerCase()));
  await person.keyboard('{Escape}');
}

function activeSubscription(id: string) {
  const subscriptions = observed.subscriptions.filter(
    (subscription) => !subscription.stopped && subscription.id.toLowerCase() === id.toLowerCase(),
  );
  expect(subscriptions).toHaveLength(1);
  const subscription = subscriptions[0];
  if (!subscription) throw new Error('No active navigation subscription');
  return subscription;
}

async function publish(subscription: Subscription, type: string) {
  await act(async () => {
    subscription.notify([
      { seasonId: subscription.id, fromSequence: 2, toSequence: 2, types: [type] },
    ]);
    await Promise.resolve();
  });
}

beforeEach(() => {
  observed.deferJoin = false;
  observed.subscriptions.length = 0;
  observed.pollsRendered.length = 0;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  at('/');
});

describe('poll navigation uses actual flags of the selected season', () => {
  it('keeps the five primary links and the seasonal menu on one navigation row across pages', async () => {
    const answer = deferredResponse();
    const server = site(() => answer.promise);
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('navigation-home');
    const navigation = within(screen.getByRole('navigation', { name: ru.nav.label }));
    const expectPrimary = () => {
      expect(navigation.getAllByRole('link').map((link) => link.textContent)).toEqual([
        ru.nav.season,
        ru.nav.feed,
        ru.nav.pool,
        ru.nav.rules,
        ru.nav.hallOfFame,
      ]);
    };
    expectPrimary();
    expect(navigation.queryByRole('button', { name: ru.nav.more })).not.toBeInTheDocument();

    await person.click(screen.getByRole('button', { name: 'Главная загружена: включено' }));
    expectPrimary();
    const more = navigation.getByRole('button', { name: ru.nav.more });
    more.focus();
    await person.keyboard('{ArrowDown}');
    expect(await screen.findByRole('menuitem', { name: ru.nav.gallery })).toHaveFocus();
    expect(screen.getByRole('menuitem', { name: ru.nav.polls })).toHaveAttribute(
      'href',
      paths.polls(seasonA),
    );
    expect(screen.getByRole('menuitem', { name: ru.turn.interactions })).toHaveAttribute(
      'href',
      paths.interactions(seasonA),
    );
    await person.keyboard('{Escape}');
    expect(more).toHaveFocus();

    await person.click(navigation.getByRole('link', { name: ru.nav.feed }));
    await screen.findByTestId('navigation-feed');
    expectPrimary();
    expect(navigation.getByRole('button', { name: ru.nav.more })).toBe(more);
    expect(server.sent('GET', '/navigation')).toHaveLength(1);

    await act(async () => {
      answer.resolve(json(200, nav(seasonA, false)));
      await answer.promise;
    });
    await waitFor(() => expect(more).not.toBeInTheDocument());
  });

  it('parses poll aliases and explicit ids, rejects malformed paths and preserves legacy routes', () => {
    expect(paths.polls()).toBe('/polls');
    expect(paths.polls(seasonB)).toBe(`/seasons/${seasonB}/polls`);
    expect(routeOf('/polls')).toEqual({ kind: 'polls', seasonId: null });
    expect(routeOf('/polls/')).toEqual({ kind: 'polls', seasonId: null });
    expect(routeOf(`/seasons/${seasonB}/polls`)).toEqual({ kind: 'polls', seasonId: seasonB });
    const uppercase = routeOf(`/seasons/${seasonB.toUpperCase()}/polls`);
    expect(uppercase.kind).toBe('polls');
    if (uppercase.kind !== 'polls') throw new Error('A GUID is case-insensitive');
    expect(uppercase.seasonId?.toLowerCase()).toBe(seasonB);
    for (const path of [
      '/seasons/not-a-guid/polls',
      `/seasons/${seasonB}/polls/extra`,
      '/polls/extra',
    ])
      expect(routeOf(path)).toEqual({ kind: 'notFound' });
    expect(routeOf('/feed')).toEqual({ kind: 'feed', seasonId: null });
    expect(routeOf(`/seasons/${seasonA}/feed`)).toEqual({ kind: 'feed', seasonId: seasonA });
    expect(routeOf('/rules')).toEqual({ kind: 'rules' });
    expect(routeOf('/pool')).toEqual({ kind: 'pool' });
    expect(routeOf('/admin/log')).toEqual({ kind: 'admin', section: 'log' });
  });

  it('uses the main page callback without another navigation request, subscription or poll-screen import', async () => {
    at('/');
    const server = site();
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('navigation-home');
    expectMenuHidden();

    await person.click(screen.getByRole('button', { name: 'Главная загружена: включено' }));

    await expectLinkTo(seasonA);
    expect(server.sent('GET', '/navigation')).toHaveLength(0);
    expect(observed.subscriptions).toHaveLength(0);
    expect(observed.pollsRendered).toHaveLength(0);
    expect(observed.pollsFirstLoadPath).not.toBe('/');

    await person.click(screen.getByRole('button', { name: 'Главная загружена: выключено' }));

    expectMenuHidden();
    expect(server.sent('GET', '/navigation')).toHaveLength(0);
    expect(observed.subscriptions).toHaveLength(0);
  });

  it('keeps the latest rules flags when returning to the home view', async () => {
    at('/');
    const answer = deferredResponse();
    const server = site(() => answer.promise);
    const person = userEvent.setup();
    render(<App />);
    await screen.findByTestId('navigation-home');
    await person.click(screen.getByRole('button', { name: 'Главная загружена: включено' }));
    await expectLinkTo(seasonA);

    act(() => {
      navigate('/feed');
    });
    await screen.findByTestId('navigation-feed');
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(1);
    });
    await act(async () => {
      answer.resolve(json(200, nav(seasonA, false)));
      await answer.promise;
    });
    expectMenuHidden();

    act(() => {
      navigate('/');
    });
    await screen.findByTestId('navigation-home');
    expectMenuHidden();
    expect(server.sent('GET', '/navigation')).toHaveLength(1);
  });

  it('uses an explicit uppercase feed season rather than the default and accepts a canonical lowercase response id', async () => {
    at(`/seasons/${seasonB.toUpperCase()}/feed`);
    const answer = deferredResponse();
    const server = site(() => answer.promise);
    render(<App />);
    await screen.findByTestId('navigation-feed');
    expectMenuHidden();
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(1);
    });
    expect(server.sent('GET', '/navigation')[0]?.path.toLowerCase()).toBe(
      `/api/seasons/${seasonB}/navigation`,
    );

    await act(async () => {
      answer.resolve(json(200, nav(seasonB, true)));
      await answer.promise;
    });

    await expectLinkTo(seasonB);
    expect(
      screen.getByTestId('navigation-feed').getAttribute('data-season-id')?.toLowerCase(),
    ).toBe(seasonB);
    expect(server.sent('GET', '/navigation')).toHaveLength(1);
    expect(server.sent('GET', '/rules')).toHaveLength(0);
    activeSubscription(seasonB);
  });

  it('opens the current season from the poll alias and points its enabled link to the explicit address', async () => {
    at('/polls');
    const server = site(({ path }) => ({ body: nav(requestSeason(path), true) }));
    render(<App />);

    expect(await screen.findByTestId('navigation-polls')).toHaveAttribute(
      'data-season-id',
      seasonA,
    );
    await expectLinkTo(seasonA);
    expect(server.sent('GET', '/navigation')).toHaveLength(1);
    expect(server.sent('GET', '/rules')).toHaveLength(0);
  });

  it('refreshes off and on only for rule changes and undo, without an unrelated event causing another read', async () => {
    at('/feed');
    let enabled = false;
    const server = site(({ path }) => ({ body: nav(requestSeason(path), enabled) }));
    render(<App />);
    await screen.findByTestId('navigation-feed');
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(1);
    });
    const subscription = activeSubscription(seasonA);
    expectMenuHidden();

    await publish(subscription, 'game-rolled');
    expect(server.sent('GET', '/navigation')).toHaveLength(1);
    enabled = true;
    await publish(subscription, 'ruleset-changed');
    await expectLinkTo(seasonA);
    expect(server.sent('GET', '/navigation')).toHaveLength(2);

    enabled = false;
    await publish(subscription, 'command-undone');
    await waitFor(expectMenuHidden);
    expect(server.sent('GET', '/navigation')).toHaveLength(3);
  });

  it('refreshes once after a delayed initial join to catch a change missed by the navigation snapshot', async () => {
    observed.deferJoin = true;
    at('/feed');
    let enabled = true;
    const server = site(({ path }) => ({ body: nav(requestSeason(path), enabled) }));
    render(<App />);
    await screen.findByTestId('navigation-feed');
    await expectLinkTo(seasonA);
    expect(server.sent('GET', '/navigation')).toHaveLength(1);
    const subscription = activeSubscription(seasonA);

    enabled = false;
    await act(async () => {
      subscription.notify([]);
      await Promise.resolve();
    });

    await waitFor(expectMenuHidden);
    expect(server.sent('GET', '/navigation')).toHaveLength(2);
  });

  it('ignores a late enabled answer from A after navigation to disabled B and stops the old subscription', async () => {
    at(`/seasons/${seasonA}/feed`);
    const old = deferredResponse();
    const server = site(({ path }) =>
      requestSeason(path) === seasonA ? old.promise : { body: nav(seasonB, false) },
    );
    render(<App />);
    await screen.findByTestId('navigation-feed');
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(1);
    });
    const oldSubscription = activeSubscription(seasonA);

    act(() => {
      navigate(`/seasons/${seasonB}/feed`);
    });
    await waitFor(() =>
      expect(screen.getByTestId('navigation-feed')).toHaveAttribute('data-season-id', seasonB),
    );
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(2);
    });
    expect(oldSubscription.stopped).toBe(true);
    expectMenuHidden();

    await act(async () => {
      old.resolve(json(200, nav(seasonA, true)));
      await old.promise;
    });

    expectMenuHidden();
    expect(screen.getByTestId('navigation-feed')).toHaveAttribute('data-season-id', seasonB);
    activeSubscription(seasonB);
  });

  it('keeps another page usable on a temporary new-scope failure, hides the old flag and recovers on a rule update', async () => {
    at(`/seasons/${seasonA}/feed`);
    let broken = true;
    const server = site(({ path }) =>
      requestSeason(path) === seasonA
        ? { body: nav(seasonA, true) }
        : broken
          ? { status: 500 }
          : { body: nav(seasonB, true) },
    );
    render(<App />);
    await screen.findByTestId('navigation-feed');
    await expectLinkTo(seasonA);

    act(() => {
      navigate(`/seasons/${seasonB}/feed`);
    });
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(2);
    });
    expect(screen.getByTestId('navigation-feed')).toHaveAttribute('data-season-id', seasonB);
    expectMenuHidden();

    broken = false;
    await publish(activeSubscription(seasonB), 'ruleset-changed');
    await expectLinkTo(seasonB);
    expect(server.sent('GET', '/navigation')).toHaveLength(3);
    expect(screen.getByTestId('navigation-feed')).toBeVisible();
  });

  it('returns to sign-in and stops the scoped subscription when the navigation request returns 401', async () => {
    at('/feed');
    site(() => ({ status: 401 }));
    render(<App />);

    await screen.findByTestId('login-submit');

    expectMenuHidden();
    expect(screen.queryByTestId('navigation-feed')).not.toBeInTheDocument();
    expect(observed.subscriptions.every((subscription) => subscription.stopped)).toBe(true);
  });

  it('does not restore a poll link when a pending successful answer arrives after sign-out', async () => {
    at('/feed');
    const pending = deferredResponse();
    const server = site(() => pending.promise);
    render(<App />);
    await screen.findByTestId('navigation-feed');
    await waitFor(() => {
      expect(server.sent('GET', '/navigation')).toHaveLength(1);
    });
    const subscription = activeSubscription(seasonA);

    act(() => {
      globalThis.dispatchEvent(new Event(SIGNED_OUT_EVENT));
    });
    await screen.findByTestId('login-submit');
    expect(subscription.stopped).toBe(true);
    await act(async () => {
      pending.resolve(json(200, nav(seasonA, true)));
      await pending.promise;
    });

    expectMenuHidden();
    expect(screen.getByTestId('login-submit')).toBeVisible();
    expect(screen.queryByTestId('navigation-feed')).not.toBeInTheDocument();
  });
});
