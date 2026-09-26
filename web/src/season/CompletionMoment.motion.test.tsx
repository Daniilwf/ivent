import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { linearBoard } from '../board/linearBoard';
import { ru } from '../i18n/ru';
import { CompletionMoment } from './CompletionMoment';

// Full motion (no reduced-motion preference in this file): the moment plays its steps and waits for them

const { board } = linearBoard([
  { id: 'start', type: 'start' },
  { id: 'a', type: 'empty' },
  { id: 'b', type: 'empty' },
  { id: 'finish', type: 'finish' },
]);
const me = { id: 'me', name: 'Вася', token: 0, cell: 3, points: 3, me: true };

function moment(done: () => void) {
  return (
    <CompletionMoment
      dice={{ id: 1, values: [1, 2], challenge: 0 }}
      board={board}
      players={[me]}
      mover={me}
      from={1}
      to={3}
      onDone={done}
    />
  );
}

describe('CompletionMoment in full motion (H4)', () => {
  it('throws the dice, then walks the token on the map, then ends', async () => {
    const done = vi.fn();
    render(moment(done));
    const stage = screen.getByTestId('dice');
    expect(stage.querySelectorAll('[data-die]')).toHaveLength(2);
    expect(within(stage).queryByRole('application')).toBeNull();

    // The dice land and the total rests; then the map comes with my token on the way
    await vi.waitFor(
      () => {
        expect(within(stage).getByRole('application')).toBeInTheDocument();
      },
      { timeout: 4000 },
    );
    expect(stage.querySelector('[data-die]')).toBeNull();
    expect(done).not.toHaveBeenCalled();

    await vi.waitFor(
      () => {
        expect(done).toHaveBeenCalledTimes(1);
      },
      { timeout: 4000 },
    );
  });

  it('ends at once on «Показать результат», once, without the walk', async () => {
    const done = vi.fn();
    render(moment(done));

    await userEvent.click(screen.getByRole('button', { name: ru.moments.skip }));
    expect(done).toHaveBeenCalledTimes(1);

    // Nothing more comes after the skip: no walk and no second end
    await new Promise((resolve) => setTimeout(resolve, 2500));
    expect(done).toHaveBeenCalledTimes(1);
    expect(within(screen.getByTestId('dice')).queryByRole('application')).toBeNull();
  });
});
