import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { EventChoice } from './EventChoice';

const playerId = 'ab100000-0000-0000-0000-00000000000f';
const questionPrompt = 'Кому подарить попутный ветер?';

function question(overrides: Partial<Schemas['ChoiceView']> = {}): Schemas['ChoiceView'] {
  return {
    id: 'ee100000-0000-0000-0000-000000000001',
    kind: 'effect',
    prompt: questionPrompt,
    objectId: 'tailwind-gift',
    name: 'Попутный ветер',
    options: [
      { id: 'Себе', game: null },
      { id: 'Другу', game: null },
    ],
    ...overrides,
  };
}

const submit = () => screen.getByRole('button', { name: /ответить/i });

describe('an event waiting for my answer', () => {
  it('shows the event and its question as the accessible name of the answer group', () => {
    const choice = question();
    render(<EventChoice choice={choice} busy={false} onChoose={vi.fn()} />);

    expect(screen.getByText('Попутный ветер')).toBeVisible();
    expect(screen.getByText(questionPrompt)).toBeVisible();
    const group =
      screen.queryByRole('radiogroup', { name: questionPrompt }) ??
      screen.getByRole('group', { name: questionPrompt });
    expect(group).toContainElement(screen.getByRole('radio', { name: 'Себе' }));
    expect(group).toContainElement(screen.getByRole('radio', { name: 'Другу' }));
  });

  it('requires a radio choice and a separate explicit submission', async () => {
    const onChoose = vi.fn();
    render(<EventChoice choice={question()} busy={false} onChoose={onChoose} />);

    expect(submit()).toBeDisabled();
    expect(
      screen.getAllByRole('radio').every((radio) => !(radio as HTMLInputElement).checked),
    ).toBe(true);
    await userEvent.click(screen.getByRole('radio', { name: 'Другу' }));
    expect(onChoose).not.toHaveBeenCalled();
    expect(submit()).toBeEnabled();
    await userEvent.click(submit());
    expect(onChoose).toHaveBeenCalledExactlyOnceWith('Другу');
  });

  it.each([
    playerId,
    playerId.toUpperCase(),
    playerId.replaceAll('-', ''),
    playerId.replaceAll('-', '').toUpperCase(),
  ])('shows the server label for option %s but submits the exact original id', async (optionId) => {
    const onChoose = vi.fn();
    render(
      <EventChoice
        choice={question({ options: [{ id: optionId, game: null, label: 'Вася' }] })}
        busy={false}
        onChoose={onChoose}
      />,
    );

    await userEvent.click(screen.getByRole('radio', { name: 'Вася' }));
    await userEvent.click(submit());

    expect(onChoose).toHaveBeenCalledExactlyOnceWith(optionId);
    expect(screen.queryByText(optionId, { exact: true })).not.toBeInTheDocument();
  });

  it('keeps unlabelled options exact, including a literal guid that could also name a player', async () => {
    const options = [
      'Horror & / Action',
      playerId,
      'ab100000-0000-0000-0000-00000000000g',
      'cc30000000000000000000000000000d',
    ] as const;
    const onChoose = vi.fn();
    render(
      <EventChoice
        choice={question({ options: options.map((id) => ({ id, game: null })) })}
        busy={false}
        onChoose={onChoose}
      />,
    );

    for (const option of options) expect(screen.getByRole('radio', { name: option })).toBeVisible();
    await userEvent.click(screen.getByRole('radio', { name: options[0] }));
    await userEvent.click(submit());
    expect(onChoose).toHaveBeenCalledExactlyOnceWith(options[0]);
  });

  it('shows long content as text, including markup-looking content, without creating HTML from it', () => {
    const prompt =
      'Кому подарить очень длинный ивент: <img src=x onerror=alert(1)> и почему именно этому игроку?';
    const option = 'Категория с длинным названием, пробелами, скобками (и уточнением)';
    const { container } = render(
      <EventChoice
        choice={question({ prompt, options: [{ id: option, game: null }] })}
        busy={false}
        onChoose={vi.fn()}
      />,
    );

    expect(screen.getByText(prompt)).toBeVisible();
    expect(screen.getByRole('radio', { name: option })).toBeVisible();
    expect(container.querySelector('img')).toBeNull();
  });

  it('is operable with the keyboard without sending as soon as an option is selected', async () => {
    const user = userEvent.setup();
    const onChoose = vi.fn();
    render(<EventChoice choice={question()} busy={false} onChoose={onChoose} />);

    await user.tab();
    expect(screen.getByRole('radio', { name: 'Себе' })).toHaveFocus();
    await user.keyboard('[Space]');
    expect(screen.getByRole('radio', { name: 'Себе' })).toBeChecked();
    expect(onChoose).not.toHaveBeenCalled();
    await user.tab();
    expect(submit()).toHaveFocus();
    await user.keyboard('[Enter]');
    expect(onChoose).toHaveBeenCalledExactlyOnceWith('Себе');
  });

  it.each(['busy', 'disabled'] as const)(
    'cannot change or submit an answer while %s',
    async (block) => {
      const onChoose = vi.fn();
      const choice = question();
      const { rerender } = render(<EventChoice choice={choice} busy={false} onChoose={onChoose} />);
      await userEvent.click(screen.getByRole('radio', { name: 'Другу' }));

      rerender(
        <EventChoice
          choice={choice}
          busy={block === 'busy'}
          disabled={block === 'disabled'}
          onChoose={onChoose}
        />,
      );

      expect(submit()).toBeDisabled();
      for (const radio of screen.getAllByRole('radio')) expect(radio).toBeDisabled();
      await userEvent.click(submit());
      expect(onChoose).not.toHaveBeenCalled();
    },
  );

  it('preserves an unsent selection when the same question is refreshed', async () => {
    const onChoose = vi.fn();
    const choice = question();
    const { rerender } = render(<EventChoice choice={choice} busy={false} onChoose={onChoose} />);
    await userEvent.click(screen.getByRole('radio', { name: 'Другу' }));

    rerender(
      <EventChoice
        choice={{ ...choice, options: [...choice.options] }}
        busy={false}
        onChoose={onChoose}
      />,
    );

    expect(screen.getByRole('radio', { name: 'Другу' })).toBeChecked();
    expect(onChoose).not.toHaveBeenCalled();
  });

  it('starts a new question without carrying over the old selection even if the option text matches', async () => {
    const onChoose = vi.fn();
    const { rerender } = render(
      <EventChoice choice={question()} busy={false} onChoose={onChoose} />,
    );
    await userEvent.click(screen.getByRole('radio', { name: 'Другу' }));

    rerender(
      <EventChoice
        choice={question({ id: 'ee100000-0000-0000-0000-000000000002' })}
        busy={false}
        onChoose={onChoose}
      />,
    );

    expect(screen.getByRole('radio', { name: 'Другу' })).not.toBeChecked();
    expect(submit()).toBeDisabled();
    await userEvent.click(submit());
    expect(onChoose).not.toHaveBeenCalled();
  });

  it('does not submit a selected option that the server removed from the question', async () => {
    const onChoose = vi.fn();
    const choice = question();
    const { rerender } = render(<EventChoice choice={choice} busy={false} onChoose={onChoose} />);
    await userEvent.click(screen.getByRole('radio', { name: 'Другу' }));

    rerender(
      <EventChoice
        choice={{ ...choice, options: [{ id: 'Себе', game: null }] }}
        busy={false}
        onChoose={onChoose}
      />,
    );

    expect(submit()).toBeDisabled();
    await userEvent.click(submit());
    expect(onChoose).not.toHaveBeenCalled();
  });

  it('does not invent an answer when the server supplied no options', () => {
    render(<EventChoice choice={question({ options: [] })} busy={false} onChoose={vi.fn()} />);

    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(screen.getByText(questionPrompt)).toBeVisible();
    const answer = screen.queryByRole('button', { name: /ответить/i });
    if (answer) expect(answer).toBeDisabled();
  });

  it.each([
    ['forward', 'Вперёд'],
    ['back', 'Назад'],
  ] as const)(
    'translates explicit direction %s while returning its original id',
    async (direction, label) => {
      const onChoose = vi.fn();
      render(
        <EventChoice
          choice={question({ options: [{ id: direction, game: null, direction }] })}
          busy={false}
          onChoose={onChoose}
        />,
      );

      await userEvent.click(screen.getByRole('radio', { name: label }));
      await userEvent.click(submit());

      expect(onChoose).toHaveBeenCalledExactlyOnceWith(direction);
    },
  );

  it('does not translate a literal forward option without direction metadata', async () => {
    const onChoose = vi.fn();
    render(
      <EventChoice
        choice={question({ options: [{ id: 'forward', game: null }] })}
        busy={false}
        onChoose={onChoose}
      />,
    );

    await userEvent.click(screen.getByRole('radio', { name: 'forward' }));
    await userEvent.click(submit());

    expect(onChoose).toHaveBeenCalledExactlyOnceWith('forward');
  });
});
