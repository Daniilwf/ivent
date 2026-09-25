import { render, screen, within } from '@testing-library/react';
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
    await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({
      difficulty: 'normal',
      estimatedHours: 7.5,
      hoursSource: 'HLTB',
    });
  });

  it.each(['', '0', '-2', 'abc'])('refuses the estimate "%s"', async (value) => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    if (value) await userEvent.type(screen.getByTestId('complete-hours'), value);
    await userEvent.type(screen.getByTestId('complete-hours-source'), 'HLTB');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(ru.turn.hoursInvalid);
  });

  it('shows every difficulty from the API contract in Russian', () => {
    render(<CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />);

    const options = within(screen.getByTestId('complete-difficulty'))
      .getAllByRole('option')
      .map((o) => o.textContent);
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

  // ---- C7a (D-96): challenge, hours source, review ----

  it('claims the challenge only when the box is checked', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    const box = screen.getByLabelText(ru.turn.challengeDone);
    expect(box).toHaveAttribute('type', 'checkbox');
    expect(box).not.toBeChecked();
    await userEvent.click(box);
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({ difficulty: 'normal', challengeDone: true });
  });

  it('asks for the source of the estimate only when the game has no hours', () => {
    const { unmount } = render(
      <CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />,
    );
    expect(screen.queryByTestId('complete-hours-source')).not.toBeInTheDocument();
    unmount();

    render(<CompleteForm needsHours pending={false} onComplete={vi.fn()} />);
    const source = screen.getByLabelText(ru.turn.hoursSource, { exact: false });
    expect(source).toBe(screen.getByTestId('complete-hours-source'));
    expect(source).toHaveAttribute('maxLength', '300');
  });

  it.each(['', '   '])('refuses an estimate without a source "%s"', async (value) => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    await userEvent.type(screen.getByTestId('complete-hours'), '6');
    if (value) await userEvent.type(screen.getByTestId('complete-hours-source'), value);
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(ru.turn.hoursSourceRequired);
  });

  it('sends a link as the source of the estimate', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    await userEvent.type(screen.getByTestId('complete-hours'), '12');
    await userEvent.type(
      screen.getByTestId('complete-hours-source'),
      'https://howlongtobeat.com/game/2231',
    );
    await userEvent.click(screen.getByLabelText(ru.turn.challengeDone));
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({
      difficulty: 'normal',
      estimatedHours: 12,
      hoursSource: 'https://howlongtobeat.com/game/2231',
      challengeDone: true,
    });
  });

  it('offers no review by default and ratings from 1 to 10', () => {
    render(<CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />);

    const rating = screen.getByTestId('complete-review-rating');
    expect(rating).toBe(screen.getByLabelText(ru.turn.reviewRating));
    expect(
      within(rating)
        .getAllByRole('option')
        .map((o) => o.textContent),
    ).toEqual([ru.turn.reviewNoRating, ...Array.from({ length: 10 }, (_, i) => String(i + 1))]);
    expect(rating).toHaveValue('');
    expect(screen.getByLabelText(ru.turn.reviewText)).toHaveAttribute('maxLength', '2000');
  });

  it('sends the review with the completion', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    await userEvent.selectOptions(screen.getByTestId('complete-review-rating'), '8');
    await userEvent.type(screen.getByTestId('complete-review-text'), 'Туман и радио');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledWith({
      difficulty: 'normal',
      review: { rating: 8, text: 'Туман и радио' },
    });
  });

  it('sends a rating without text as a review without text', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    await userEvent.selectOptions(screen.getByTestId('complete-review-rating'), '10');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).toHaveBeenCalledTimes(1);
    const sent = onComplete.mock.calls[0]?.[0] as {
      review?: { rating: number; text?: string | null };
    };
    expect(sent.review?.rating).toBe(10);
    expect(sent.review?.text ?? null).toBeNull();
  });

  it('refuses a review text without a rating', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    await userEvent.type(screen.getByTestId('complete-review-text'), 'Без оценки');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent(ru.turn.reviewRatingRequired);
  });
});
