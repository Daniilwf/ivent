import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, json } from '../test/fakeServer';
import { HallOfFameScreen } from './HallOfFameScreen';

const t = ru.hallOfFame;
const winnersPath = '/api/hall-of-fame';
const recordsPath = '/api/hall-of-fame/records';

const seasons: Schemas['HallOfFameSeasonView'][] = [
  {
    seasonId: 'spring',
    seasonName: 'Весна',
    winners: [
      { playerId: 'p1', name: 'Яна', points: 1024, profileUserId: 'u1' },
      { playerId: 'p2', name: 'Анна', points: 1024, profileUserId: null },
    ],
  },
  {
    seasonId: 'autumn',
    seasonName: 'Осень',
    winners: [{ playerId: 'p3', name: 'Лиса', points: 18, profileUserId: 'u3' }],
  },
];

const records: Schemas['HallOfFameRecordsView'] = {
  longestGames: [
    {
      seasonId: 'spring',
      seasonName: 'Весна',
      title: 'Zelda',
      gameId: 'g1',
      runId: 'r1',
      hours: 9.9,
    },
    {
      seasonId: 'autumn',
      seasonName: 'Осень',
      title: 'Abzû',
      gameId: 'g2',
      runId: 'r2',
      hours: 9.9,
    },
  ],
  fastestFinishes: [
    {
      seasonId: 'spring',
      seasonName: 'Весна',
      playerId: 'p1',
      name: 'Яна',
      elapsedSeconds: 93_784.125,
      profileUserId: 'u1',
    },
    {
      seasonId: 'autumn',
      seasonName: 'Осень',
      playerId: 'p2',
      name: 'Анна',
      elapsedSeconds: 93_784.125,
      profileUserId: null,
    },
  ],
  largestRolls: [
    {
      seasonId: 'spring',
      seasonName: 'Весна',
      runId: 'r3',
      playerId: 'p1',
      name: 'Яна',
      sum: 48,
      profileUserId: 'u1',
    },
    {
      seasonId: 'autumn',
      seasonName: 'Осень',
      runId: 'r4',
      playerId: 'p2',
      name: 'Анна',
      sum: 48,
      profileUserId: null,
    },
  ],
};

describe('HallOfFameScreen', () => {
  it('preserves the order of tied winners and records and links only available profiles', async () => {
    fakeServer({ [winnersPath]: seasons, [recordsPath]: records });
    render(<HallOfFameScreen onSignedOut={vi.fn()} />);

    const winners = within(screen.getByRole('region', { name: t.winners }));
    expect(await winners.findByText(t.tiedFirst)).toBeVisible();
    expect(
      winners.getAllByRole('heading', { level: 3 }).map((heading) => heading.textContent),
    ).toEqual(['Весна', 'Осень']);
    expect(winners.getAllByRole('link').map((link) => link.textContent)).toEqual(['Яна', 'Лиса']);
    const firstWinner = winners.getByRole('link', { name: 'Яна' });
    const deletedWinner = winners.getByText('Анна');
    expect(firstWinner).toHaveAttribute('href', '/users/u1');
    expect(deletedWinner).toBeVisible();
    expect(firstWinner.compareDocumentPosition(deletedWinner)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
    expect(winners.queryByRole('link', { name: 'Анна' })).not.toBeInTheDocument();
    expect(winners.getAllByText('1 024 очка')).toHaveLength(2);

    const longest = within(screen.getByRole('region', { name: t.record }));
    expect(await longest.findByText('9,9 ч')).toBeVisible();
    expect(longest.getAllByRole('link').map((link) => link.getAttribute('href'))).toEqual([
      '/games/g1',
      '/games/g2',
    ]);
    expect(longest.getByText('Весна')).toBeVisible();
    expect(longest.getByText('Осень')).toBeVisible();

    const fastest = within(screen.getByRole('region', { name: t.fastestFinish }));
    expect(fastest.getByText('1 д 2 ч 3 мин 4,125 с')).toBeVisible();
    const [firstFinish, secondFinish] = fastest.getAllByRole('listitem');
    if (!firstFinish || !secondFinish) throw new Error('Both tied records must be visible.');
    expect(within(firstFinish).getByRole('link', { name: 'Яна' })).toHaveAttribute(
      'href',
      '/users/u1',
    );
    expect(within(firstFinish).getByText('Весна')).toBeVisible();
    expect(within(secondFinish).getByText('Анна')).toBeVisible();
    expect(within(secondFinish).getByText('Осень')).toBeVisible();
    expect(within(secondFinish).queryByRole('link')).not.toBeInTheDocument();

    const largest = within(screen.getByRole('region', { name: t.largestRoll }));
    expect(largest.getByText('48')).toBeVisible();
    expect(largest.getAllByRole('listitem')).toHaveLength(2);
    expect(largest.getByRole('link', { name: 'Яна' })).toHaveAttribute('href', '/users/u1');
    expect(largest.getByText('Анна')).toBeVisible();
    expect(largest.queryByRole('link', { name: 'Анна' })).not.toBeInTheDocument();

    const recordHeading = longest.getByRole('heading', { name: t.record });
    const scroll = vi.spyOn(recordHeading, 'scrollIntoView');
    const address = window.location.href;
    await userEvent.click(screen.getByRole('link', { name: /^К рекордам$/ }));
    expect(recordHeading).toHaveFocus();
    expect(scroll).toHaveBeenCalledWith({ block: 'start' });
    expect(window.location.href).toBe(address);
  });

  it('renders winners while the record loads and retries only the failed record', async () => {
    let completeFirstRequest!: (response: Response) => void;
    const pending = new Promise<Response>((resolve) => {
      completeFirstRequest = resolve;
    });
    let requests = 0;
    const { sent } = fakeServer({
      [winnersPath]: seasons,
      [recordsPath]: () => (++requests === 1 ? pending : json(200, records)),
    });
    render(<HallOfFameScreen onSignedOut={vi.fn()} />);

    const winners = within(screen.getByRole('region', { name: t.winners }));
    const longest = within(screen.getByRole('region', { name: t.record }));
    expect(await winners.findByRole('link', { name: 'Яна' })).toBeVisible();
    expect(longest.getByTestId('loading')).toHaveAttribute('aria-busy', 'true');
    completeFirstRequest(json(429));
    expect(await longest.findByRole('alert')).toHaveTextContent(ru.shell.tooOftenText);
    await userEvent.click(longest.getByRole('button', { name: ru.ui.retry }));
    expect(await longest.findByRole('link', { name: 'Zelda' })).toBeVisible();
    expect(winners.getByRole('link', { name: 'Яна' })).toBeVisible();
    expect(sent('GET', winnersPath)).toHaveLength(1);
    expect(sent('GET', recordsPath)).toHaveLength(2);
  });

  it('shows one empty state when there are no winners or records', async () => {
    fakeServer({ [winnersPath]: [], [recordsPath]: { longestGames: [], fastestFinishes: [] } });
    render(<HallOfFameScreen onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: t.emptyTitle })).toBeVisible();
    expect(screen.queryByRole('link', { name: t.toRecord })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: t.noWinnersTitle })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: t.noRecordTitle })).not.toBeInTheDocument();
  });

  it('keeps showing the longest game when the server does not provide a fastest finish list', async () => {
    fakeServer({ [winnersPath]: [], [recordsPath]: { longestGames: records.longestGames } });
    render(<HallOfFameScreen onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('link', { name: 'Zelda' })).toBeVisible();
    expect(screen.getByRole('link', { name: t.toRecord })).toBeVisible();
    expect(screen.queryByRole('heading', { name: t.fastestFinish })).not.toBeInTheDocument();
  });

  it('jumps to the fastest finish when it is the only record', async () => {
    fakeServer({
      [winnersPath]: [],
      [recordsPath]: {
        longestGames: [],
        fastestFinishes: records.fastestFinishes.map((finish) => ({
          ...finish,
          elapsedSeconds: 59.99999,
        })),
      },
    });
    render(<HallOfFameScreen onSignedOut={vi.fn()} />);

    const recordHeading = await screen.findByRole('heading', { name: t.fastestFinish });
    expect(screen.getByText('1 мин')).toBeVisible();
    const scroll = vi.spyOn(recordHeading, 'scrollIntoView');
    const jump = screen.getByRole('link', { name: t.toRecord });
    expect(jump).toHaveAttribute('href', `#${recordHeading.id}`);
    await userEvent.click(jump);
    expect(recordHeading).toHaveFocus();
    expect(scroll).toHaveBeenCalledWith({ block: 'start' });
    expect(screen.queryByRole('heading', { name: t.record })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: t.emptyTitle })).not.toBeInTheDocument();
  });

  it('uses the signed-out callback when either request loses its session', async () => {
    fakeServer({ [winnersPath]: [], [recordsPath]: () => json(401) });
    const onSignedOut = vi.fn();
    render(<HallOfFameScreen onSignedOut={onSignedOut} />);

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalledOnce();
    });
    expect(screen.queryByRole('button', { name: ru.ui.retry })).not.toBeInTheDocument();
  });
});
