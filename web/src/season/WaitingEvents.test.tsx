import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { WaitingEvents } from './WaitingEvents';

type WaitingEvent = Schemas['WaitingEventView'];
type RunCondition = Schemas['RunConditionView'];

function event(overrides: Partial<WaitingEvent> = {}): WaitingEvent {
  return {
    objectId: 'tailwind',
    name: 'Попутный ветер',
    description: 'В следующем броске за прохождение на один кубик больше.',
    kind: 'good',
    ...overrides,
  };
}

function condition(overrides: Partial<RunCondition> = {}): RunCondition {
  return {
    objectId: 'not-even-sweating',
    name: 'Даже не вспотел',
    kind: 'bad',
    hostile: false,
    difficultyAtLeast: 'hard',
    difficultyAtMost: null,
    ...overrides,
  };
}

function expectBefore(first: HTMLElement, second: HTMLElement) {
  expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
}

describe('events and conditions waiting for my future steps', () => {
  it.each(['omitted', 'null', 'empty'] as const)('renders no empty sections for %s', (empty) => {
    const { container } = render(
      empty === 'omitted' ? (
        <WaitingEvents />
      ) : (
        <WaitingEvents
          events={empty === 'null' ? null : []}
          conditions={empty === 'null' ? null : []}
        />
      ),
    );

    expect(container).toBeEmptyDOMElement();
  });

  it('shows event names and their descriptions without an empty conditions section', async () => {
    render(<WaitingEvents events={[event()]} />);

    expect(screen.getByRole('heading', { name: 'Накопленные ивенты' })).toBeVisible();
    expect(screen.getByText('Попутный ветер')).toBeVisible();
    await userEvent.click(screen.getByText('Текст ивента: «Попутный ветер»'));
    expect(
      screen.getByText('В следующем броске за прохождение на один кубик больше.'),
    ).toBeVisible();
    expect(
      screen.queryByRole('heading', { name: 'Условия следующей игры' }),
    ).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('shows only the next-game conditions when there are no waiting events', () => {
    const { container } = render(<WaitingEvents events={null} conditions={[condition()]} />);

    expect(screen.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();
    expect(screen.getByText('Даже не вспотел')).toBeVisible();
    expect(container).toHaveTextContent(/Не ниже:\s*Сложная/);
    expect(screen.queryByRole('heading', { name: 'Накопленные ивенты' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('keeps the server order of several events and several conditions', () => {
    const events: readonly WaitingEvent[] = [
      event({ objectId: 'z-first', name: 'Я первый', kind: 'bad' }),
      event({ objectId: 'a-second', name: 'А я второй', kind: 'special' }),
      event({ objectId: 'b-third', name: 'Б я третий', kind: 'good' }),
    ];
    const conditions: readonly RunCondition[] = [
      condition({ objectId: 'z-hard', name: 'Я жду следующего ролла' }),
      condition({ objectId: 'a-easy', name: 'А я жду после него', difficultyAtLeast: 'easy' }),
    ];
    render(<WaitingEvents events={events} conditions={conditions} />);

    expectBefore(screen.getByText('Я первый'), screen.getByText('А я второй'));
    expectBefore(screen.getByText('А я второй'), screen.getByText('Б я третий'));
    expectBefore(
      screen.getByText('Я жду следующего ролла'),
      screen.getByText('А я жду после него'),
    );
  });

  it('names all four kinds of event in Russian', () => {
    render(
      <WaitingEvents
        events={(['special', 'good', 'bad', 'risky'] as const).map((kind) =>
          event({ objectId: kind, name: `Ивент ${kind}`, kind }),
        )}
      />,
    );

    for (const label of ['Специвент', 'Хороший ивент', 'Плохой ивент', 'Рискованный ивент']) {
      expect(screen.getByText(label)).toBeVisible();
    }
  });

  it('does not merge events from different decks merely because their object id is the same', async () => {
    render(
      <WaitingEvents
        events={[
          event({ kind: 'good', description: 'Эффект хорошего ивента ждёт броска.' }),
          event({ kind: 'bad', description: 'Эффект плохого ивента ждёт броска.' }),
        ]}
      />,
    );

    expect(screen.getAllByText('Попутный ветер')).toHaveLength(2);
    for (const summary of screen.getAllByText('Текст ивента: «Попутный ветер»')) {
      await userEvent.click(summary);
    }
    expect(screen.getByText('Эффект хорошего ивента ждёт броска.')).toBeVisible();
    expect(screen.getByText('Эффект плохого ивента ждёт броска.')).toBeVisible();
  });

  it('accepts an item condition without an event kind and explains both difficulty bounds', () => {
    const { container } = render(
      <WaitingEvents
        conditions={[
          condition({
            kind: null,
            name: 'Предмет с условием',
            difficultyAtLeast: 'normal',
            difficultyAtMost: 'hard',
          }),
        ]}
      />,
    );

    expect(screen.getByText('Предмет с условием')).toBeVisible();
    expect(container).toHaveTextContent(/Не ниже:\s*Нормальная/);
    expect(container).toHaveTextContent(/Не выше:\s*Сложная/);
    expect(container).not.toHaveTextContent(/null|undefined/);
  });

  it('keeps long content visible as text without interpreting markup', async () => {
    const name =
      'Очень длинное название накопленного ивента с уточнением <img src=x onerror=alert(1)>';
    const description =
      'Длинное описание: эффект ждёт следующей подходящей игры, в том числе после обновления страницы.';
    const conditionName = 'Длинное условие предмета: <script>сохранить как текст</script>';
    const { container } = render(
      <WaitingEvents
        events={[event({ name, description })]}
        conditions={[condition({ name: conditionName, kind: null })]}
      />,
    );

    expect(screen.getByText(name)).toBeVisible();
    await userEvent.click(screen.getByText(`Текст ивента: «${name}»`));
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.getByText(conditionName)).toBeVisible();
    expect(container.querySelector('img, script')).toBeNull();
  });

  it('removes a consumed event and hides each section as its server list becomes empty', () => {
    const { rerender } = render(<WaitingEvents events={[event()]} conditions={[condition()]} />);

    rerender(<WaitingEvents events={[]} conditions={[condition()]} />);

    expect(screen.queryByText('Попутный ветер')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Накопленные ивенты' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();

    rerender(<WaitingEvents events={[]} conditions={[]} />);

    expect(screen.queryByRole('heading')).not.toBeInTheDocument();
  });
});
