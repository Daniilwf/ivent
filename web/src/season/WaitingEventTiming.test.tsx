import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { WaitingEvents } from './WaitingEvents';

type WaitingEvent = Schemas['WaitingEventView'];
type RunCondition = Schemas['RunConditionView'];

const originalDescription =
  'Следующую игру нужно пройти на сложной сложности или выше. В следующем броске на один кубик больше.';

function event(overrides: Partial<WaitingEvent> = {}): WaitingEvent {
  return {
    objectId: 'trial-and-die',
    name: 'Испытание и кубик',
    description: originalDescription,
    kind: 'bad',
    waitingForRoll: false,
    waitingForThrow: true,
    ...overrides,
  };
}

function condition(): RunCondition {
  return {
    objectId: 'trial-and-die',
    name: 'Испытание и кубик',
    kind: 'bad',
    hostile: false,
    difficultyAtLeast: 'hard',
    difficultyAtMost: null,
  };
}

function summary(name = 'Испытание и кубик') {
  const text = screen.getByText(`Текст ивента: «${name}»`);
  const line = text.closest('summary');
  if (!(line instanceof HTMLElement))
    throw new Error('The original text must use a native summary');
  return line;
}

function fold(line: HTMLElement) {
  const details = line.closest('details');
  if (!(details instanceof HTMLDetailsElement))
    throw new Error('The original text must belong to a details disclosure');
  return details;
}

function eventRow(line: HTMLElement) {
  const row = fold(line).parentElement;
  if (!row) throw new Error('The source disclosure must belong to an event row');
  return row;
}

describe('actual waiting steps separately from original event prose (S5D-5/6)', () => {
  it.each([
    {
      waitingForRoll: true,
      waitingForThrow: false,
      visible: 'Ждёт ролла игры',
      absent: 'Ждёт броска',
    },
    {
      waitingForRoll: false,
      waitingForThrow: true,
      visible: 'Ждёт броска',
      absent: 'Ждёт ролла игры',
    },
  ] as const)('identifies $visible from the server flags', (timing) => {
    render(<WaitingEvents events={[event(timing)]} />);

    expect(screen.getByText('Испытание и кубик')).toBeVisible();
    expect(screen.getByText('Плохой ивент')).toBeVisible();
    expect(screen.getByText(timing.visible)).toBeVisible();
    expect(screen.queryByText(timing.absent)).not.toBeInTheDocument();
    expect(screen.queryByText('Ждёт будущего шага')).not.toBeInTheDocument();
    expect(screen.getByText(originalDescription)).not.toBeVisible();
  });

  it('shows both waiting steps of one event without duplicating its row or original text', () => {
    render(<WaitingEvents events={[event({ waitingForRoll: true, waitingForThrow: true })]} />);

    expect(screen.getByText('Ждёт ролла игры')).toBeVisible();
    expect(screen.getByText('Ждёт броска')).toBeVisible();
    expect(screen.getAllByText('Испытание и кубик')).toHaveLength(1);
    expect(screen.getAllByText(originalDescription)).toHaveLength(1);
    expect(screen.getAllByText('Текст ивента: «Испытание и кубик»')).toHaveLength(1);
  });

  it.each(['omitted', 'null'] as const)(
    'uses an honest generic status for %s legacy timing',
    (legacy) => {
      const oldEvent: WaitingEvent = {
        objectId: 'old-tailwind',
        name: 'Старый ивент',
        description: 'Следующая игра будет сложной, а после неё бросок.',
        kind: 'good',
        ...(legacy === 'null' ? { waitingForRoll: null, waitingForThrow: null } : {}),
      };
      render(<WaitingEvents events={[oldEvent]} />);

      expect(screen.getByText('Ждёт будущего шага')).toBeVisible();
      expect(screen.queryByText('Ждёт ролла игры')).not.toBeInTheDocument();
      expect(screen.queryByText('Ждёт броска')).not.toBeInTheDocument();
      expect(screen.getByText(oldEvent.description)).not.toBeVisible();
    },
  );

  it.each([
    { waitingForRoll: true, waitingForThrow: null, expected: 'Ждёт ролла игры' },
    { waitingForRoll: null, waitingForThrow: true, expected: 'Ждёт броска' },
  ] as const)('keeps a known $expected when the other optional flag is null', (timing) => {
    render(<WaitingEvents events={[event(timing)]} />);

    expect(screen.getByText(timing.expected)).toBeVisible();
    expect(screen.queryByText('Ждёт будущего шага')).not.toBeInTheDocument();
  });

  it('starts the explicitly named original text closed and opens and closes it by clicking', async () => {
    const user = userEvent.setup();
    render(<WaitingEvents events={[event()]} />);
    const line = summary();

    expect(line).toBeVisible();
    expect(fold(line)).not.toHaveAttribute('open');
    expect(screen.getByText(originalDescription)).not.toBeVisible();

    await user.click(line);

    expect(fold(line)).toHaveAttribute('open');
    expect(screen.getByText(originalDescription)).toBeVisible();
    expect(screen.getByText('Ждёт броска')).toBeVisible();

    await user.click(line);

    expect(fold(line)).not.toHaveAttribute('open');
    expect(screen.getByText(originalDescription)).not.toBeVisible();
  });

  it('puts the native summary in the keyboard tab order without making prose a button', async () => {
    const user = userEvent.setup();
    render(<WaitingEvents events={[event()]} />);
    const line = summary();

    await user.tab();

    expect(line).toHaveFocus();
    expect(line.tagName).toBe('SUMMARY');
    expect(fold(line)).not.toHaveAttribute('open');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    // Native Enter activation needs a real browser: jsdom does not implement that default action.
  });

  it('does not infer an already consumed run condition from the remaining event description', () => {
    const { rerender } = render(<WaitingEvents events={[event()]} conditions={[condition()]} />);
    expect(screen.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();
    expect(screen.getByText(/Не ниже:/)).toBeVisible();

    // The server moved the condition to the offered run; only the next-throw modifier remains here.
    rerender(<WaitingEvents events={[event()]} conditions={[]} />);

    expect(screen.getByText('Ждёт броска')).toBeVisible();
    expect(screen.queryByText('Ждёт ролла игры')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Условия следующей игры' }),
    ).not.toBeInTheDocument();
    expect(screen.getByText(originalDescription)).not.toBeVisible();
  });

  it('keeps a real next-game condition visible while the same event source text is folded', () => {
    render(<WaitingEvents events={[event()]} conditions={[condition()]} />);

    const heading = screen.getByRole('heading', { name: 'Условия следующей игры' });
    expect(heading).toBeVisible();
    expect(screen.getByText('Не ниже: Сложная')).toBeVisible();
    expect(screen.getByText('Ждёт броска')).toBeVisible();
    expect(screen.getByText(originalDescription)).not.toBeVisible();
    expect(fold(summary())).not.toHaveAttribute('open');
  });

  it('keeps independent disclosures for the same object drawn from different event kinds', async () => {
    const user = userEvent.setup();
    render(
      <WaitingEvents
        events={[
          event({ kind: 'good', description: 'Исходный хороший текст.' }),
          event({ kind: 'bad', description: 'Исходный плохой текст.' }),
        ]}
      />,
    );
    const summaries = screen.getAllByText('Текст ивента: «Испытание и кубик»');
    expect(summaries).toHaveLength(2);
    const first = summaries[0];
    if (!first) throw new Error('Expected the first independent event disclosure');

    await user.click(first);

    expect(screen.getByText('Исходный хороший текст.')).toBeVisible();
    expect(screen.getByText('Исходный плохой текст.')).not.toBeVisible();
    expect(screen.getByText('Хороший ивент')).toBeVisible();
    expect(screen.getByText('Плохой ивент')).toBeVisible();
  });

  it('keeps server order and timing attached to the corresponding event rather than sorting kinds', () => {
    render(
      <WaitingEvents
        events={[
          event({
            name: 'Я первый',
            objectId: 'z-first',
            kind: 'bad',
            waitingForRoll: true,
            waitingForThrow: false,
          }),
          event({ name: 'А я второй', objectId: 'a-second', kind: 'special' }),
        ]}
      />,
    );
    const first = summary('Я первый');
    const second = summary('А я второй');

    expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(eventRow(first)).getByText('Ждёт ролла игры')).toBeVisible();
    expect(within(eventRow(second)).getByText('Ждёт броска')).toBeVisible();
  });

  it('keeps names and original descriptions as literal text when a disclosure opens', async () => {
    const user = userEvent.setup();
    const name = 'Длинный ивент <img src=x onerror=alert(1)>';
    const description = '<script>Никаких команд браузеру</script> Следующая игра — только текст.';
    const { container } = render(<WaitingEvents events={[event({ name, description })]} />);

    expect(screen.getByText(name)).toBeVisible();
    expect(screen.getByText(description)).not.toBeVisible();

    await user.click(summary(name));

    expect(screen.getByText(description)).toBeVisible();
    expect(container.querySelector('img, script')).toBeNull();
  });

  it('removes a consumed step and its source disclosure when the server removes the row', () => {
    const { rerender } = render(<WaitingEvents events={[event()]} conditions={[condition()]} />);

    rerender(<WaitingEvents events={[]} conditions={[condition()]} />);

    expect(screen.queryByText('Ждёт броска')).not.toBeInTheDocument();
    expect(screen.queryByText(originalDescription)).not.toBeInTheDocument();
    expect(screen.queryByText('Текст ивента: «Испытание и кубик»')).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();
  });
});
