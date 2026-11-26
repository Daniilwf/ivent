import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, json, seasonId } from '../test/fakeServer';
import { StatisticsSection } from './StatisticsSection';

const t = ru.admin.statistics;
const path = (id: string) => `/api/admin/seasons/${id}/statistics`;
const archive = '5ea50000-0000-0000-0000-000000000002';
const rows = [
  {
    playerId: '1',
    name: 'Барсук',
    attacksMade: 0,
    attacksReceived: 2,
    lastActionAt: '2026-10-12T08:23:00Z',
  },
  {
    playerId: '2',
    name: 'Волк',
    attacksMade: 2,
    attacksReceived: 0,
    lastActionAt: '2026-10-13T06:00:00Z',
  },
  {
    playerId: '3',
    name: 'Ёж',
    attacksMade: 0,
    attacksReceived: 0,
    lastActionAt: null,
  },
] satisfies Schemas['AdminSeasonStatisticsView']['players'];

function displayedNames() {
  return screen.getAllByRole('rowheader').map((header) => header.textContent);
}

describe('Statistics', () => {
  it('shows the server order and distinct zero attacks and unknown activity, then sorts in place', async () => {
    const server = fakeServer({ [`GET ${path(seasonId)}`]: { players: rows } });
    render(<StatisticsSection seasonId={seasonId} version={0} />);

    const table = await screen.findByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers.map((header) => header.textContent)).toEqual([
      t.player,
      t.attacksMade,
      t.attacksReceived,
      t.lastAction,
    ]);
    expect(displayedNames()).toEqual(['Барсук', 'Волк', 'Ёж']);
    const quiet = within(table).getByRole('row', { name: /Ёж/ });
    expect(within(quiet).getAllByRole('cell', { name: '0' })).toHaveLength(2);
    expect(within(quiet).getByText(t.noAction)).toBeVisible();
    const activity = within(table).getByText(/12 октября.*11:23 МСК/);
    expect(activity).toHaveAttribute('datetime', rows[0]?.lastActionAt);

    const order = screen.getByRole('combobox', { name: t.order });
    await userEvent.selectOptions(order, 'attacksMade');
    expect(displayedNames()).toEqual(['Волк', 'Барсук', 'Ёж']);
    expect(order).toHaveFocus();
    await userEvent.selectOptions(order, 'attacksReceived');
    expect(displayedNames()).toEqual(['Барсук', 'Волк', 'Ёж']);
    await userEvent.selectOptions(order, 'lastActionAt');
    expect(displayedNames()).toEqual(['Волк', 'Барсук', 'Ёж']);
    expect(within(table).getByRole('columnheader', { name: t.lastAction })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    expect(server.sent('GET', path(seasonId))).toHaveLength(1);
  });

  it('shows an error and can retry to an empty report', async () => {
    let attempts = 0;
    fakeServer({
      [`GET ${path(seasonId)}`]: () =>
        ++attempts === 1 ? { status: 503 } : { body: { players: [] } },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);

    expect(await screen.findByRole('heading', { name: ru.admin.loadErrorTitle })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    expect(await screen.findByRole('heading', { name: t.emptyTitle })).toBeVisible();
    expect(screen.queryByRole('combobox', { name: t.order })).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('drops a pending refresh of the previous season and clears its visible rows', async () => {
    let attempts = 0;
    let finishRefresh: (() => void) | undefined;
    let finishArchive: (() => void) | undefined;
    const refresh = new Promise<Response>((resolve) => {
      finishRefresh = () => {
        resolve(json(200, { players: rows }));
      };
    });
    const archived = new Promise<Response>((resolve) => {
      finishArchive = () => {
        resolve(json(200, { players: [{ ...rows[0], name: 'Лиса' }] }));
      };
    });
    const server = fakeServer({
      [`GET ${path(seasonId)}`]: () => (++attempts === 1 ? { body: { players: rows } } : refresh),
      [`GET ${path(archive)}`]: () => archived,
    });
    const { rerender } = render(<StatisticsSection seasonId={seasonId} version={0} />);
    expect(await screen.findByRole('rowheader', { name: 'Барсук' })).toBeVisible();

    rerender(<StatisticsSection seasonId={seasonId} version={1} />);
    await waitFor(() => {
      expect(server.sent('GET', path(seasonId))).toHaveLength(2);
    });
    rerender(<StatisticsSection seasonId={archive} version={0} />);
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.getByTestId('loading')).toBeVisible();
    await act(async () => {
      finishArchive?.();
      await archived;
    });
    expect(await screen.findByRole('rowheader', { name: 'Лиса' })).toBeVisible();
    await act(async () => {
      finishRefresh?.();
      await refresh;
    });
    expect(displayedNames()).toEqual(['Лиса']);
  });
});
