import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { CompleteForm } from './CompleteForm';

describe('CompleteForm', () => {
  it('sends the chosen difficulty without hours when the game has them', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    await userEvent.selectOptions(screen.getByTestId('complete-difficulty'), 'hard');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({ difficulty: 'hard' });
    expect(screen.queryByTestId('complete-hours')).not.toBeInTheDocument();
  });

  it('asks for an hours estimate when the game has no hours and accepts a comma', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    await userEvent.type(screen.getByTestId('complete-hours'), '7,5');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({ difficulty: 'normal', estimatedHours: 7.5 });
  });

  it.each(['', '0', '-2', 'abc'])('refuses the estimate "%s"', async (value) => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    if (value) await userEvent.type(screen.getByTestId('complete-hours'), value);
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(ru.turn.hoursInvalid);
  });

  it('shows every difficulty from the API contract in Russian', () => {
    render(<CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />);

    const options = screen.getAllByRole('option').map((o) => o.textContent);
    expect(options).toEqual([
      ru.difficulty.easy,
      ru.difficulty.normal,
      ru.difficulty.hard,
      ru.difficulty.extreme,
    ]);
  });

  it('disables the button while the command is pending', () => {
    render(<CompleteForm needsHours={false} pending onComplete={vi.fn()} />);

    expect(screen.getByTestId('complete-submit')).toBeDisabled();
  });
});
