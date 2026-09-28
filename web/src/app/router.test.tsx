import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Link } from './Link';
import { navigate, paths, routeOf, usePath } from './router';

// H5: the site's pages by address (D-150) — links open them without a reload, back and forward work.

const id = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

function Where() {
  return <p data-testid="where">{usePath()}</p>;
}

describe('router', () => {
  afterEach(() => {
    globalThis.history.replaceState(null, '', '/');
  });

  it('knows the pages of the site by their addresses', () => {
    expect(routeOf('/')).toEqual({ kind: 'season' });
    expect(routeOf('/feed')).toEqual({ kind: 'feed', seasonId: null });
    expect(routeOf('/feed/')).toEqual({ kind: 'feed', seasonId: null });
    expect(routeOf(`/seasons/${id}/feed`)).toEqual({ kind: 'feed', seasonId: id });
    expect(routeOf(`/users/${id}`)).toEqual({ kind: 'profile', userId: id });
    expect(routeOf(`/games/${id}`)).toEqual({ kind: 'game', gameId: id });
    expect(routeOf('/games/not-an-id')).toEqual({ kind: 'notFound' });
    expect(routeOf('/pool')).toEqual({ kind: 'pool' });
    expect(routeOf('/pool/')).toEqual({ kind: 'pool' });
    expect(routeOf('/rules')).toEqual({ kind: 'rules' });
    expect(routeOf('/whatever')).toEqual({ kind: 'notFound' });
    expect(routeOf('/admin')).toEqual({ kind: 'admin', section: null });
    expect(routeOf('/admin/players/')).toEqual({ kind: 'admin', section: 'players' });
    expect(routeOf('/admin/players/1')).toEqual({ kind: 'notFound' });
  });

  it('builds the addresses the links point to', () => {
    expect(paths.feed()).toBe('/feed');
    expect(paths.feed(id)).toBe(`/seasons/${id}/feed`);
    expect(paths.profile(id)).toBe(`/users/${id}`);
    expect(paths.game(id)).toBe(`/games/${id}`);
    expect(paths.pool()).toBe('/pool');
    expect(paths.rules()).toBe('/rules');
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

  it('does not stack the same page twice', () => {
    const before = globalThis.history.length;

    act(() => {
      navigate('/');
    });

    expect(globalThis.history.length).toBe(before);
  });
});
