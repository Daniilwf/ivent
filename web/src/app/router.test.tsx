import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link } from './Link';
import { adminPlayersSeason, navigate, paths, routeOf, usePageHeading, usePath } from './router';

// H5: the site's pages by address (D-150) — links open them without a reload, back and forward work.

const id = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

function Where() {
  return <p data-testid="where">{usePath()}</p>;
}

function Page() {
  const heading = usePageHeading();
  return (
    <>
      <h1 ref={heading} tabIndex={-1}>
        Страница
      </h1>
      <input aria-label="Поле" />
    </>
  );
}

/** One frame of the page: usePageHeading moves the focus on the next one */
const nextFrame = () =>
  act(
    () =>
      new Promise<void>((done) => {
        requestAnimationFrame(() => {
          done();
        });
      }),
  );

/** The browser's Back: resolves once the entry before is the current one */
const goBack = () =>
  new Promise<void>((resolve) => {
    globalThis.addEventListener(
      'popstate',
      () => {
        resolve();
      },
      { once: true },
    );
    globalThis.history.back();
  });

describe('router', () => {
  it('carries only an explicit valid season in the admin players return address', () => {
    const href = paths.adminPlayers(id);
    const url = new URL(href, globalThis.location.origin);
    expect(url.pathname).toBe('/admin/players');
    expect(routeOf(url.pathname)).toEqual({ kind: 'admin', section: 'players' });
    expect(adminPlayersSeason(url.search)).toBe(id);
    expect(adminPlayersSeason(`?season=${id.toUpperCase()}`)).toBe(id);
    for (const search of [
      '',
      '?season=',
      '?season=not-a-guid',
      `?season=${id}%2F`,
      `?season=${id}&season=${id}`,
    ]) {
      expect(adminPlayersSeason(search)).toBeNull();
    }
  });
  afterEach(() => {
    globalThis.history.replaceState(null, '', '/');
  });

  it('knows the pages of the site by their addresses', () => {
    expect(routeOf('/')).toEqual({ kind: 'season' });
    expect(routeOf(`/seasons/${id}/`)).toEqual({ kind: 'season', seasonId: id });
    expect(routeOf('/feed')).toEqual({ kind: 'feed', seasonId: null });
    expect(routeOf('/feed/')).toEqual({ kind: 'feed', seasonId: null });
    expect(routeOf(`/seasons/${id}/feed`)).toEqual({ kind: 'feed', seasonId: id });
    expect(routeOf('/interactions')).toEqual({ kind: 'interactions', seasonId: null });
    expect(routeOf(`/seasons/${id}/interactions`)).toEqual({ kind: 'interactions', seasonId: id });
    expect(routeOf(`/users/${id}`)).toEqual({ kind: 'profile', userId: id });
    expect(routeOf(`/games/${id}`)).toEqual({ kind: 'game', gameId: id });
    expect(routeOf('/games/not-an-id')).toEqual({ kind: 'notFound' });
    expect(routeOf('/pool')).toEqual({ kind: 'pool' });
    expect(routeOf('/pool/')).toEqual({ kind: 'pool' });
    expect(routeOf('/rules')).toEqual({ kind: 'rules' });
    expect(routeOf('/whats-new/')).toEqual({ kind: 'whatsNew' });
    expect(routeOf('/whats-new/latest')).toEqual({ kind: 'notFound' });
    expect(routeOf('/whatever')).toEqual({ kind: 'notFound' });
    expect(routeOf('/admin')).toEqual({ kind: 'admin', section: null });
    expect(routeOf('/admin/players/')).toEqual({ kind: 'admin', section: 'players' });
    expect(routeOf('/admin/players/1')).toEqual({ kind: 'notFound' });
  });

  it('builds the addresses the links point to', () => {
    expect(routeOf(paths.playerPreview(id, id))).toEqual({
      kind: 'playerPreview',
      seasonId: id,
      playerId: id,
    });
    expect(routeOf(paths.season(id))).toEqual({ kind: 'season', seasonId: id });
    expect(paths.feed()).toBe('/feed');
    expect(paths.feed(id)).toBe(`/seasons/${id}/feed`);
    expect(paths.interactions(id)).toBe(`/seasons/${id}/interactions`);
    expect(paths.profile(id)).toBe(`/users/${id}`);
    expect(paths.game(id)).toBe(`/games/${id}`);
    expect(paths.pool()).toBe('/pool');
    expect(paths.rules()).toBe('/rules');
    expect(paths.whatsNew()).toBe('/whats-new');
    expect(paths.admin()).toBe('/admin');
    expect(paths.admin('log')).toBe('/admin/log');
    expect(routeOf(paths.profile(id))).toEqual({ kind: 'profile', userId: id });
  });

  it('opens a page by a link without reloading and goes back with the browser', async () => {
    render(
      <>
        <Link to={paths.game(id)}>Игра</Link>
        <Where />
      </>,
    );

    await userEvent.click(screen.getByRole('link', { name: 'Игра' }));

    expect(screen.getByTestId('where')).toHaveTextContent(`/games/${id}`);
    expect(screen.getByRole('link', { name: 'Игра' })).toHaveAttribute('href', `/games/${id}`);

    act(() => {
      globalThis.history.replaceState(null, '', '/');
      globalThis.dispatchEvent(new PopStateEvent('popstate'));
    });
    expect(screen.getByTestId('where')).toHaveTextContent(/^\/$/);
  });

  it('leaves a click with a modifier to the browser (a new tab)', () => {
    render(
      <>
        <Link to="/feed">Лента</Link>
        <Where />
      </>,
    );
    const link = screen.getByRole('link', { name: 'Лента' });
    const click = new MouseEvent('click', { bubbles: true, cancelable: true, ctrlKey: true });

    act(() => {
      link.dispatchEvent(click);
    });

    expect(screen.getByTestId('where')).toHaveTextContent(/^\/$/);
  });

  // FL-1: a link followed in one test left the next tests' first page taking the focus to its heading a frame late,
  // in the middle of their typing. The two tests run in this order: the second is a new visit
  it('a page opened by a link takes the focus to its heading', async () => {
    act(() => {
      navigate('/pool');
    });
    render(<Page />);
    await nextFrame();

    expect(screen.getByRole('heading', { name: 'Страница' })).toHaveFocus();
  });

  it('the first page of a test is the first of a visit: the focus stays where the reader put it', async () => {
    render(<Page />);
    screen.getByRole('textbox', { name: 'Поле' }).focus();
    await nextFrame();

    expect(screen.getByRole('textbox', { name: 'Поле' })).toHaveFocus();
  });

  // Review of K1A-21: the site took the scroll restoration from the browser for the pool and the feed, and every other
  // page opened by Back was left where the page before it had been scrolled
  it('Back to a page that does not restore its own scroll puts it where the reader left it, not where the last page was', async () => {
    const scrolled = vi.spyOn(window, 'scrollTo');
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 400 });
    navigate(`/users/${id}`);
    // A page opened by a link starts at the top
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 });
    navigate('/pool');
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 15000 });
    navigate('/rules');
    scrolled.mockClear();

    await goBack();
    await nextFrame();
    expect(scrolled).toHaveBeenLastCalledWith(0, 15000);

    // The profile was opened by a link and left at the top
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 15000 });
    await goBack();
    await nextFrame();
    expect(scrolled).toHaveBeenLastCalledWith(0, 0);

    // The first page was left 400 px down
    await goBack();
    await nextFrame();
    expect(scrolled).toHaveBeenLastCalledWith(0, 400);
    scrolled.mockRestore();
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 });
  });

  it('opens an unvisited fragment without jumping to the top and restores a saved position', async () => {
    const scrolled = vi.spyOn(window, 'scrollTo');
    try {
      act(() => {
        globalThis.history.replaceState(null, '', '/rules#rules-roll');
        globalThis.dispatchEvent(new PopStateEvent('popstate'));
      });
      await nextFrame();
      expect(scrolled).not.toHaveBeenCalled();

      act(() => {
        globalThis.history.replaceState({ scrollY: 640 }, '', '/rules#rules-roll');
        globalThis.dispatchEvent(new PopStateEvent('popstate'));
      });
      await nextFrame();
      expect(scrolled).toHaveBeenLastCalledWith(0, 640);
    } finally {
      scrolled.mockRestore();
    }
  });

  it('a reload keeps where the page was scrolled in its entry', () => {
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 2300 });
    globalThis.dispatchEvent(new Event('pagehide'));

    expect((globalThis.history.state as { scrollY?: number }).scrollY).toBe(2300);
    Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 });
  });

  it('does not stack the same page twice', () => {
    const before = globalThis.history.length;

    act(() => {
      navigate('/');
    });

    expect(globalThis.history.length).toBe(before);
  });
});
