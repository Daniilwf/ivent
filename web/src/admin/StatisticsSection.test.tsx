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
    completedRunPoints: 0,
    completedRunHours: 0,
    pointsPerHour: null,
  },
  {
    playerId: '2',
    name: 'Волк',
    attacksMade: 2,
    attacksReceived: 0,
    lastActionAt: '2026-10-13T06:00:00Z',
    completedRunPoints: 0,
    completedRunHours: 0,
    pointsPerHour: null,
  },
  {
    playerId: '3',
    name: 'Ёж',
    attacksMade: 0,
    attacksReceived: 0,
    lastActionAt: null,
    completedRunPoints: 0,
    completedRunHours: 0,
    pointsPerHour: null,
  },
] satisfies Schemas['AdminSeasonStatisticsView']['players'];

function displayedNames() {
  return screen.getAllByRole('rowheader').map((header) => header.textContent);
}

describe('Statistics', () => {
  it('shows earned completion points per saved hour and distinguishes no sample from a measured zero', async () => {
    fakeServer({
      [`GET ${path(seasonId)}`]: {
        players: [
          { ...rows[0], completedRunPoints: 25, completedRunHours: 2, pointsPerHour: 12.5 },
          { ...rows[1], completedRunPoints: 0, completedRunHours: 5, pointsPerHour: 0 },
          { ...rows[2], completedRunPoints: 0, completedRunHours: 0, pointsPerHour: null },
        ],
      },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);

    const table = await screen.findByRole('table');
    expect(within(table).getByRole('columnheader', { name: 'Очки в час' })).toBeVisible();
    expect(screen.getByText(/очки за зачтённые прохождения/i)).toBeVisible();
    const measured = within(table).getByRole('row', { name: /Барсук/ });
    expect(within(measured).getByText('12,5', { exact: true })).toBeVisible();
    expect(within(measured).getByText('25 очков за 2 часа', { exact: true })).toBeVisible();
    const zero = within(table).getByRole('row', { name: /Волк/ });
    expect(within(zero).getByText('0 очков за 5 часов', { exact: true })).toBeVisible();
    expect(within(zero).queryByText('Нет зачтённых часов')).toBeNull();
    const absent = within(table).getByRole('row', { name: /Ёж/ });
    expect(within(absent).getByText('Нет зачтённых часов', { exact: true })).toBeVisible();
    expect(within(absent).queryByText(/очков за/)).toBeNull();
  });

  it('sorts the unrounded server rate with measured zero before absent samples and stable equal rates', async () => {
    fakeServer({
      [`GET ${path(seasonId)}`]: {
        players: [
          { ...rows[0], completedRunPoints: 1, completedRunHours: 3, pointsPerHour: 0.3333 },
          { ...rows[1], completedRunPoints: 334, completedRunHours: 1000, pointsPerHour: 0.334 },
          { ...rows[2], completedRunPoints: 0, completedRunHours: 0, pointsPerHour: null },
          {
            ...rows[0],
            playerId: '4',
            name: 'Лиса',
            completedRunPoints: 0,
            completedRunHours: 5,
            pointsPerHour: 0,
          },
          {
            ...rows[0],
            playerId: '5',
            name: 'Рысь',
            completedRunPoints: 1,
            completedRunHours: 3,
            pointsPerHour: 0.3333,
          },
          {
            ...rows[0],
            playerId: '6',
            name: 'Сова',
            completedRunPoints: 0,
            completedRunHours: 0,
            pointsPerHour: null,
          },
        ],
      },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);
    await screen.findByRole('table');

    const order = screen.getByRole('combobox', { name: t.order });
    await userEvent.selectOptions(order, 'pointsPerHour');
    expect(displayedNames()).toEqual(['Волк', 'Барсук', 'Рысь', 'Лиса', 'Ёж', 'Сова']);
    expect(screen.getAllByText('0,33', { exact: true })).toHaveLength(3);
    expect(screen.getByRole('columnheader', { name: 'Очки в час' })).toHaveAttribute(
      'aria-sort',
      'descending',
    );
    expect(order).toHaveFocus();
  });

  it('keeps a tiny positive rate distinct from measured zero and an absent sample', async () => {
    fakeServer({
      [`GET ${path(seasonId)}`]: {
        players: [
          { ...rows[0], completedRunPoints: 1, completedRunHours: 1000, pointsPerHour: 0.001 },
          { ...rows[1], completedRunPoints: 0, completedRunHours: 5, pointsPerHour: 0 },
          { ...rows[2], completedRunPoints: 0, completedRunHours: 0, pointsPerHour: null },
          {
            ...rows[0],
            playerId: '4',
            name: 'Лиса',
            completedRunPoints: 1,
            completedRunHours: 0.001,
            pointsPerHour: 1000,
          },
        ],
      },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);
    const table = await screen.findByRole('table');
    const tiny = within(table).getByRole('row', { name: /Барсук/ });
    expect(within(tiny).getByText('<0,01', { exact: true })).toBeVisible();
    expect(within(tiny).getByText('1 очко за 1 000 часов', { exact: true })).toBeVisible();
    const zero = within(table).getByRole('row', { name: /Волк/ });
    const zeroMetric = within(zero).getAllByRole('cell')[2];
    if (!zeroMetric) throw new Error('The third metric cell is missing.');
    expect(within(zeroMetric).getByText('0', { exact: true })).toBeVisible();
    expect(within(zeroMetric).queryByText('<0,01', { exact: true })).toBeNull();
    const absent = within(table).getByRole('row', { name: /Ёж/ });
    expect(within(absent).getByText('Нет зачтённых часов', { exact: true })).toBeVisible();
    expect(within(absent).queryByText('<0,01', { exact: true })).toBeNull();
    const short = within(table).getByRole('row', { name: /Лиса/ });
    expect(within(short).getByText('1 очко за 0,001 часа', { exact: true })).toBeVisible();
  });

  it('shows the server order and distinct zero attacks and unknown activity, then sorts in place', async () => {
    const server = fakeServer({ [`GET ${path(seasonId)}`]: { players: rows } });
    render(<StatisticsSection seasonId={seasonId} version={0} />);

    const table = await screen.findByRole('table');
    const headers = within(table).getAllByRole('columnheader');
    expect(headers.map((header) => header.textContent)).toEqual([
      t.player,
      t.attacksMade,
      t.attacksReceived,
      t.pointsPerHour,
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

  it('shows legal decimal-minimum saved hours without a false zero and a finite large rate', async () => {
    fakeServer({
      [`GET ${path(seasonId)}`]: {
        players: [
          { ...rows[0], completedRunPoints: 8, completedRunHours: 1e-28, pointsPerHour: 8e28 },
        ],
      },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);
    const table = await screen.findByRole('table');
    const player = within(table).getByRole('row', { name: /Барсук/ });
    expect(within(player).getByText('8 очков за 1E-28 часа', { exact: true })).toBeVisible();
    expect(within(player).getByText('8E28', { exact: true })).toBeVisible();
    expect(within(player).queryByText('8 очков за 0 часа', { exact: true })).toBeNull();
  });

  it('keeps an overflow-promoted hours total compact without turning its positive rate into zero', async () => {
    fakeServer({
      [`GET ${path(seasonId)}`]: {
        players: [
          { ...rows[0], completedRunPoints: 4, completedRunHours: 1e29, pointsPerHour: 4e-29 },
        ],
      },
    });
    render(<StatisticsSection seasonId={seasonId} version={0} />);
    const table = await screen.findByRole('table');
    const player = within(table).getByRole('row', { name: /Барсук/ });
    expect(within(player).getByText('4 очка за 1E29 часов', { exact: true })).toBeVisible();
    expect(within(player).getByText('<0,01', { exact: true })).toBeVisible();
    const metric = within(player).getAllByRole('cell')[2];
    if (!metric) throw new Error('The third metric cell is missing.');
    expect(within(metric).queryByText('0', { exact: true })).toBeNull();
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
