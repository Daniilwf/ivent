import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { EventChoice } from './EventChoice';

const name = 'Попутный ветер';
const description =
  'Подари игроку дополнительный кубик к следующему прохождению. Выбери, кому он достанется.';
const prompt = 'Кому подарить кубик?';

function question(overrides: Partial<Schemas['ChoiceView']> = {}): Schemas['ChoiceView'] {
  return {
    id: 'ee100000-0000-0000-0000-000000000001',
    kind: 'effect',
    objectId: 'tailwind-gift',
    name,
    description,
    prompt,
    options: [
      { id: 'to-petya', label: 'Петя', game: null },
      { id: 'self', label: 'Себе', game: null },
    ],
    ...overrides,
  };
}

const drawn: Schemas['DrawnEventView'] = {
  sequence: 7,
  deck: 'good',
  kind: 'good',
  objectId: 'tailwind-gift',
  name,
  description,
  source: 'cell',
};

const submit = () => screen.getByRole('button', { name: /ответить/i });
const before = (earlier: Element, later: Element) => {
  expect(earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING).not.toBe(0);
};

describe('the context before an event answer', () => {
  it('shows the instructions before the options and their submission', () => {
    render(<EventChoice choice={question()} busy={false} onChoose={vi.fn()} />);

    const instructions = screen.getByText(description);
    expect(instructions).toBeVisible();
    before(instructions, screen.getByRole('radio', { name: 'Петя' }));
    before(instructions, submit());
  });

  it('explains why answering is unavailable before the disabled controls', () => {
    const reason = 'Ивенты выключены. Ответить можно, когда их снова включат.';
    render(
      <EventChoice
        choice={question()}
        busy={false}
        disabled
        unavailableReason={reason}
        onChoose={vi.fn()}
      />,
    );

    const explanation = screen.getByText(reason);
    expect(explanation).toBeVisible();
    expect(screen.getAllByText(reason)).toHaveLength(1);
    before(explanation, screen.getByRole('radio', { name: 'Петя' }));
    before(explanation, submit());
    expect(screen.getByRole('radio', { name: 'Петя' })).toBeDisabled();
    expect(submit()).toBeDisabled();
  });

  it('keeps a matching drawn event and its answer in one card with one heading and one description', () => {
    render(<EventChoice choice={question()} drawn={drawn} busy={false} onChoose={vi.fn()} />);

    expect(screen.getAllByRole('heading', { name })).toHaveLength(1);
    expect(screen.getAllByText(description)).toHaveLength(1);
    const card = screen.getByRole('article', { name });
    expect(within(card).getByText(description)).toBeVisible();
    expect(within(card).getByText(prompt)).toBeVisible();
    expect(within(card).getByText(ru.events.waitingAnswer)).toBeVisible();
    expect(within(card).getByRole('radio', { name: 'Петя' })).toBeVisible();
    expect(within(card).getByRole('button', { name: /ответить/i })).toBeInTheDocument();
    expect(screen.queryByText(ru.events.played)).not.toBeInTheDocument();
    before(within(card).getByText(description), within(card).getByRole('radio', { name: 'Петя' }));
  });

  it('preserves the fresh drawn moment while putting its question in the same card', () => {
    render(<EventChoice choice={question()} drawn={drawn} fresh busy={false} onChoose={vi.fn()} />);

    const card = screen.getByTestId('drawn-event');
    expect(card).toHaveAttribute('data-drawing', 'true');
    expect(within(card).getByText(description)).toBeInTheDocument();
    expect(within(card).getByRole('radio', { name: 'Петя' })).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { name })).toHaveLength(1);
    expect(screen.getAllByText(description)).toHaveLength(1);
  });

  it('uses the question context when no matching draw is present', () => {
    render(<EventChoice choice={question()} drawn={null} busy={false} onChoose={vi.fn()} />);

    expect(screen.getByRole('heading', { name })).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.getByText(prompt)).toBeVisible();
    before(screen.getByText(description), screen.getByRole('radio', { name: 'Петя' }));
  });

  it('does not replace the question context with a different event passed as the last draw', () => {
    render(
      <EventChoice
        choice={question()}
        drawn={{
          ...drawn,
          objectId: 'another-event',
          name: 'Другой ивент',
          description: 'Это описание другого ивента.',
        }}
        busy={false}
        onChoose={vi.fn()}
      />,
    );

    expect(screen.getByRole('heading', { name })).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.queryByText('Другой ивент')).not.toBeInTheDocument();
    expect(screen.queryByText('Это описание другого ивента.')).not.toBeInTheDocument();
    before(screen.getByText(description), screen.getByRole('radio', { name: 'Петя' }));
  });

  it('keeps the selected answer while its contextual form becomes busy and ready again', async () => {
    const onChoose = vi.fn();
    const choice = question();
    const { rerender } = render(
      <EventChoice choice={choice} drawn={drawn} busy={false} onChoose={onChoose} />,
    );
    await userEvent.click(screen.getByRole('radio', { name: 'Петя' }));

    rerender(<EventChoice choice={{ ...choice }} drawn={{ ...drawn }} busy onChoose={onChoose} />);

    expect(screen.getByRole('radio', { name: 'Петя' })).toBeChecked();
    expect(submit()).toBeDisabled();
    expect(onChoose).not.toHaveBeenCalled();

    rerender(
      <EventChoice choice={{ ...choice }} drawn={{ ...drawn }} busy={false} onChoose={onChoose} />,
    );

    expect(screen.getByRole('radio', { name: 'Петя' })).toBeChecked();
    before(screen.getByText(description), screen.getByRole('radio', { name: 'Петя' }));
    await userEvent.click(submit());
    expect(onChoose).toHaveBeenCalledExactlyOnceWith('to-petya');
  });
});
