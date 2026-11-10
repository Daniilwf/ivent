import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { CompleteForm } from './CompleteForm';

describe('CompleteForm', () => {
  it('links to what counts as a completion (D-207)', () => {
    render(<CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />);

    const form = screen.getByTestId('complete-form');
    expect(within(form).getByRole('link', { name: ru.rules.sections.completion })).toHaveAttribute(
      'href',
      '/rules#rules-completion',
    );
  });

  it('sends the chosen difficulty without hours when the game has them', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours={false} pending={false} onComplete={onComplete} />);

    await userEvent.click(
      within(screen.getByTestId('complete-difficulty')).getByRole('radio', {
        name: ru.difficulty.hard,
      }),
    );
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

    // H4: every difficulty in sight as a radio button, «Нормальная» picked by default
    const radios = within(screen.getByTestId('complete-difficulty')).getAllByRole('radio');
    expect(radios.map((r) => (r as HTMLInputElement).value)).toEqual([
      'easy',
      'normal',
      'hard',
      'extreme',
    ]);
    expect(radios.filter((r) => (r as HTMLInputElement).checked)).toEqual([radios[1]]);
    const options = radios.map((r) => r.closest('label')?.textContent);
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

  it('shows no challenge box by default', () => {
    // D-96 (1): the claim is closed by features.challenges, off by default
    render(<CompleteForm needsHours={false} pending={false} onComplete={vi.fn()} />);

    expect(screen.queryByLabelText(ru.turn.challengeDone)).not.toBeInTheDocument();
  });

  it('shows no challenge box when challenges are off', async () => {
    const onComplete = vi.fn();
    render(
      <CompleteForm
        needsHours={false}
        pending={false}
        challengesEnabled={false}
        onComplete={onComplete}
      />,
    );

    expect(screen.queryByLabelText(ru.turn.challengeDone)).not.toBeInTheDocument();
    await userEvent.click(screen.getByTestId('complete-submit'));
    expect(onComplete).toHaveBeenCalledWith({ difficulty: 'normal' });
  });

  it('claims the challenge only when the box is checked', async () => {
    const onComplete = vi.fn();
    render(
      <CompleteForm needsHours={false} pending={false} challengesEnabled onComplete={onComplete} />,
    );

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
    render(<CompleteForm needsHours pending={false} challengesEnabled onComplete={onComplete} />);

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

  // ---- H4: the form on the design system ----

  it('tells every mistake under its field at once and puts the focus on the first', async () => {
    const onComplete = vi.fn();
    render(<CompleteForm needsHours pending={false} onComplete={onComplete} />);

    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    const hours = screen.getByTestId('complete-hours');
    expect(hours).toHaveAccessibleDescription(`${ru.turn.hoursHint} ${ru.turn.hoursInvalid}`);
    expect(screen.getByTestId('complete-hours-source')).toHaveAccessibleDescription(
      `${ru.turn.hoursSourceHint} ${ru.turn.hoursSourceRequired}`,
    );
    expect(screen.getAllByRole('alert')).toHaveLength(2);
    await vi.waitFor(() => {
      expect(hours).toHaveFocus();
    });
  });

  it('tells a mistake under its own field and marks only that field', async () => {
    render(<CompleteForm needsHours pending={false} onComplete={vi.fn()} />);

    await userEvent.type(screen.getByTestId('complete-hours'), '6');
    await userEvent.click(screen.getByTestId('complete-submit'));

    const source = screen.getByTestId('complete-hours-source');
    expect(source).toHaveAttribute('aria-invalid', 'true');
    expect(source).toHaveAccessibleDescription(
      `${ru.turn.hoursSourceHint} ${ru.turn.hoursSourceRequired}`,
    );
    expect(screen.getByTestId('complete-hours')).not.toHaveAttribute('aria-invalid');
    expect(screen.getAllByRole('alert')).toHaveLength(1);
  });

  it('keeps the review folded and opens it to show its mistake', async () => {
    const onComplete = vi.fn();
    const { container } = render(
      <CompleteForm needsHours={false} pending={false} onComplete={onComplete} />,
    );
    const review = container.querySelector('details');
    expect(review).not.toHaveAttribute('open');
    expect(within(review as HTMLElement).getByText(ru.turn.review)).toBeInTheDocument();

    await userEvent.type(screen.getByTestId('complete-review-text'), 'Туман');
    await userEvent.click(screen.getByTestId('complete-submit'));

    expect(onComplete).not.toHaveBeenCalled();
    expect(review).toHaveAttribute('open');
    expect(screen.getByTestId('complete-review-rating')).toHaveAttribute('aria-invalid', 'true');
  });

  it('names the one main action and shows it busy while the command runs', () => {
    render(<CompleteForm needsHours={false} pending onComplete={vi.fn()} />);

    const submit = screen.getByTestId('complete-submit');
    expect(submit).toHaveTextContent(ru.turn.complete);
    expect(submit).toHaveAttribute('data-variant', 'main');
    expect(submit).toHaveAttribute('aria-busy', 'true');
  });

  it('names the die of every difficulty and the event it grants in the hint', () => {
    render(
      <CompleteForm
        needsHours={false}
        pending={false}
        onComplete={vi.fn()}
        dice={[
          { difficulty: 'easy', sides: 2, grantEvent: null },
          { difficulty: 'normal', sides: 4, grantEvent: null },
          { difficulty: 'hard', sides: 6, grantEvent: null },
          { difficulty: 'extreme', sides: 6, grantEvent: 'good' },
        ]}
      />,
    );

    const group = screen.getByRole('group', { name: ru.turn.difficulty });
    expect(group).toHaveAccessibleDescription(
      `${ru.turn.difficultyHint}. ${ru.turn.difficultyDice([
        { label: ru.difficulty.easy, sides: 2, grant: null },
        { label: ru.difficulty.normal, sides: 4, grant: null },
        { label: ru.difficulty.hard, sides: 6, grant: null },
        { label: ru.difficulty.extreme, sides: 6, grant: 'good' },
      ])}`,
    );
    expect(group).toHaveAccessibleDescription(/пруф/);
    expect(group).toHaveAccessibleDescription(/Выше сложной — d6 и хороший ивент/);
  });
});
