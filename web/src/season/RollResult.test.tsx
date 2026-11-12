import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { ChoiceCard, OfferCard } from './RollResult';

const offer = { id: 'g1', title: 'Silent Hill', hours: 6, marks: [] };

describe('OfferCard', () => {
  it('starts on normal by default and says the completion counts no higher (D-633)', async () => {
    const onStart = vi.fn();
    render(
      <OfferCard
        offer={offer}
        roll={null}
        price={null}
        pending={false}
        onStart={onStart}
        onAlreadyPlayed={vi.fn()}
        onReroll={vi.fn()}
      />,
    );

    const group = screen.getByTestId('start-difficulty');
    expect(group).toHaveAccessibleDescription(ru.turn.startDifficultyHint);
    expect(within(group).getByRole('radio', { name: ru.difficulty.normal })).toBeChecked();
    await userEvent.click(screen.getByTestId('start'));

    expect(onStart).toHaveBeenCalledWith('normal');
  });

  it('sends the difficulty the player starts on', async () => {
    const onStart = vi.fn();
    render(
      <OfferCard
        offer={offer}
        roll={null}
        price={null}
        pending={false}
        onStart={onStart}
        onAlreadyPlayed={vi.fn()}
        onReroll={vi.fn()}
      />,
    );

    await userEvent.click(
      within(screen.getByTestId('start-difficulty')).getByRole('radio', {
        name: ru.difficulty.easy,
      }),
    );
    await userEvent.click(screen.getByTestId('start'));

    expect(onStart).toHaveBeenCalledWith('easy');
  });
});

describe('ChoiceCard', () => {
  it('starts the picked game on the chosen difficulty (D-91, D-633)', async () => {
    const onChoose = vi.fn();
    render(
      <ChoiceCard
        choice={{
          id: 'c1',
          kind: 'game',
          options: [
            { id: 'a', game: offer },
            { id: 'b', game: { ...offer, id: 'g2', title: 'Alan Wake' } },
          ],
        }}
        roll={null}
        price={null}
        pending={false}
        onChoose={onChoose}
        onAlreadyPlayed={vi.fn()}
        onReroll={vi.fn()}
      />,
    );

    await userEvent.click(
      within(screen.getByTestId('start-difficulty')).getByRole('radio', {
        name: ru.difficulty.hard,
      }),
    );
    await userEvent.click(screen.getByTestId('option-b'));

    expect(onChoose).toHaveBeenCalledWith('b', 'hard');
  });
});
