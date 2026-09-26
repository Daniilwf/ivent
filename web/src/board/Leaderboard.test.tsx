import { render, screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ru } from '../i18n/ru';
import { Leaderboard, type LeaderRow } from './Leaderboard';
import type { Player } from './types';

// What the eye sees in a row must say the same as the row's sentence for screen readers (D-100 order, H2)

const player = (id: string, name: string, extra: Partial<Player> = {}): Player => ({
  id,
  name,
  token: 0,
  cell: 1,
  points: 0,
  ...extra,
});

const rows: LeaderRow[] = [
  {
    player: player('a', 'Вася'),
    place: 1,
    points: 4,
    cellsToFinish: 0,
    isFirst: true,
    provisional: true,
  },
  {
    player: player('b', 'Петя', { me: true }),
    place: 2,
    points: 50,
    cellsToFinish: 7,
    isFirst: false,
    provisional: false,
  },
  {
    player: player('c', 'Маша'),
    place: 3,
    points: 9,
    cellsToFinish: null,
    isFirst: false,
    provisional: false,
  },
];

describe('the leaderboard', () => {
  it('shows the place, the name, the points and the way to the finish of each row', () => {
    render(<Leaderboard rows={rows} />);
    const items = within(screen.getByTestId('leaderboard')).getAllByRole('listitem');

    expect(items.map((i) => i.dataset.testid)).toEqual(['leader-a', 'leader-b', 'leader-c']);
    const [first, second] = items as [HTMLElement, HTMLElement];
    expect(within(first).getByText('1')).toBeInTheDocument();
    expect(within(first).getByText('4')).toBeInTheDocument();
    expect(within(first).getByText(ru.ui.toFinish(0))).toBeInTheDocument();
    expect(within(first).getByText(ru.board.firstProvisional)).toBeInTheDocument();
    expect(within(second).getByText('50')).toBeInTheDocument();
    expect(within(second).getByText(ru.ui.toFinish(7))).toBeInTheDocument();
    expect(within(second).getByText(ru.board.you)).toBeInTheDocument();
  });

  it('says «no way to the finish» for a row without one, not «out of the game»', () => {
    render(<Leaderboard rows={rows} />);
    const third = screen.getByTestId('leader-c');

    expect(within(third).getByText(ru.board.noWay)).toBeInTheDocument();
    expect(within(third).queryByText(ru.board.inactive)).toBeNull();
  });

  it('marks the final first place without «provisional»', () => {
    const final = rows.map((r, i) => (i === 0 ? { ...r, provisional: false } : r));
    render(<Leaderboard rows={final} />);
    const first = screen.getByTestId('leader-a');

    expect(within(first).getByText(ru.board.first)).toBeInTheDocument();
    expect(within(first).queryByText(ru.board.firstProvisional)).toBeNull();
  });

  it('gives its test ids to one copy only', () => {
    render(
      <>
        <Leaderboard rows={rows} />
        <Leaderboard rows={rows} marked={false} />
      </>,
    );

    expect(screen.getAllByTestId('leaderboard')).toHaveLength(1);
    expect(screen.getAllByTestId('leader-a')).toHaveLength(1);
  });
});
