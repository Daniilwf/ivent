import { render, screen } from '@testing-library/react';
import { ru } from '../i18n/ru';
import { MyFinish } from './MyFinish';

describe('MyFinish', () => {
  it('uses a short first-finish headline and keeps the provisional explanation in the body', () => {
    render(<MyFinish finish={{ order: 1, frozen: false }} first place={1} rollsOpen />);

    const heading = screen.getByRole('heading', { name: 'Ты первый на финише!' });
    expect(heading).not.toHaveTextContent('предварительно');
    expect(screen.getByTestId('my-finish')).toHaveTextContent(ru.turn.myFinish.firstProvisional);
  });

  it('uses the current leaderboard place in the later-finisher headline', () => {
    render(<MyFinish finish={{ order: 2, frozen: false }} first={false} place={4} rollsOpen />);

    expect(screen.getByRole('heading', { name: 'Ты на финише, 4-е место' })).toBeVisible();
  });

  it('keeps a confirmed frozen first place final after rolls close', () => {
    render(<MyFinish finish={{ order: 1, frozen: true }} first place={1} rollsOpen={false} />);
    const finish = screen.getByTestId('my-finish');
    expect(finish).toHaveTextContent(ru.turn.myFinish.firstFrozen);
    expect(finish).toHaveTextContent('Броски закрыты. Первое место подтверждено.');
    expect(finish).not.toHaveTextContent('место станет окончательным после проверки пруфов');
    expect(finish).not.toHaveTextContent(ru.turn.myFinish.firstFrozenNext);
  });

  it('keeps a later finish accurate when rolls have already closed', () => {
    render(
      <MyFinish finish={{ order: 2, frozen: false }} first={false} place={2} rollsOpen={false} />,
    );
    expect(screen.getByTestId('my-finish')).toHaveTextContent(ru.turn.myFinish.later(2));
    expect(screen.getByText('Броски закрыты.', { exact: true })).toBeVisible();
    expect(screen.getByTestId('my-finish')).not.toHaveTextContent(
      'место станет окончательным после проверки пруфов',
    );
  });

  it.each([
    {
      frozen: false,
      first: true,
      open: false,
      main: ru.turn.myFinish.firstProvisional,
      next: ru.turn.myFinish.closedNext,
    },
    {
      frozen: false,
      first: false,
      open: false,
      main: ru.turn.myFinish.later(2),
      next: ru.turn.myFinish.closedNext,
    },
    {
      frozen: true,
      first: true,
      open: true,
      main: ru.turn.myFinish.firstFrozen,
      next: ru.turn.myFinish.firstFrozenNext,
    },
    {
      frozen: false,
      first: false,
      open: true,
      main: ru.turn.myFinish.later(2),
      next: ru.turn.myFinish.laterNext,
    },
  ])(
    'preserves the other finish state $frozen/$first/$open',
    ({ frozen, first, open, main, next }) => {
      render(
        <MyFinish
          finish={{ order: first ? 1 : 2, frozen }}
          first={first}
          place={first ? 1 : 2}
          rollsOpen={open}
        />,
      );
      expect(screen.getByTestId('my-finish')).toHaveTextContent(main);
      expect(screen.getByTestId('my-finish')).toHaveTextContent(next);
    },
  );
});
