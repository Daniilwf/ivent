import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { linearBoard } from '../board/linearBoard';
import { movePath } from '../board/geometry';
import { ru } from '../i18n/ru';
import { CompletionMoment } from './CompletionMoment';

// The viewer asks for reduced motion (motion reads the preference once, so the whole file has it): the completion
// shows its result at once, without a skip (docs/DESIGN.md «Движение»)
vi.hoisted(() => {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: (query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      onchange: null,
      dispatchEvent: () => false,
    }),
  });
});

const { board } = linearBoard([
  { id: 'start', type: 'start' },
  { id: 'a', type: 'empty' },
  { id: 'b', type: 'empty' },
  { id: 'c', type: 'empty' },
  { id: 'finish', type: 'finish' },
]);
const me = { id: 'me', name: 'Вася', token: 0, cell: 4, points: 3, me: true };

describe('CompletionMoment (H4)', () => {
  it('with reduced motion ends by itself: the dice stand and the token is on its cell', async () => {
    const done = vi.fn();
    render(
      <CompletionMoment
        dice={{ id: 1, values: [1, 2], challenge: 1 }}
        board={board}
        players={[me]}
        mover={me}
        from={1}
        to={4}
        onDone={done}
      />,
    );

    await vi.waitFor(() => {
      expect(done).toHaveBeenCalledTimes(1);
    });
  });

  it('ends at once without a move when the token stays', async () => {
    const done = vi.fn();
    render(
      <CompletionMoment
        dice={{ id: 1, values: [2], challenge: 0 }}
        board={board}
        players={[me]}
        mover={me}
        from={4}
        to={4}
        onDone={done}
      />,
    );

    await vi.waitFor(() => {
      expect(done).toHaveBeenCalledTimes(1);
    });
  });

  it('reports the end once when skipped', async () => {
    const done = vi.fn();
    render(
      <CompletionMoment
        dice={{ id: 1, values: [1, 2], challenge: 0 }}
        board={board}
        players={[me]}
        mover={me}
        from={1}
        to={4}
        onDone={done}
      />,
    );

    await userEvent.click(
      within(screen.getByTestId('dice')).getByRole('button', { name: ru.moments.skip }),
    );
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(done).toHaveBeenCalledTimes(1);
  });
});

describe('movePath', () => {
  it('walks the chain cell by cell from the old cell to the new one', () => {
    expect(movePath(board, 1, 4)).toEqual([1, 2, 3, 4]);
  });

  it('has no path when the token stays', () => {
    expect(movePath(board, 3, 3)).toBeNull();
  });

  it('jumps straight back, which a completion does not do', () => {
    expect(movePath(board, 4, 2)).toEqual([4, 2]);
  });
});
