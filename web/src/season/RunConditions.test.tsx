import { render, screen, within } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { RunConditions } from './RunConditions';

type RunCondition = Schemas['RunConditionView'];

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

const outcomeLabels = ['Выполнено', 'Нарушено', 'Не применимо'];

describe('conditions fixed on a run', () => {
  it.each(['omitted', 'null', 'empty'] as const)(
    'hides the section when its conditions are %s',
    (empty) => {
      const { container } = render(
        empty === 'omitted' ? (
          <RunConditions check={{ met: false, waived: false }} />
        ) : (
          <RunConditions
            conditions={empty === 'null' ? null : []}
            check={{ met: false, waived: false }}
          />
        ),
      );

      expect(container).toBeEmptyDOMElement();
    },
  );

  it('shows an active runs conditions without inventing a result before completion', () => {
    render(<RunConditions conditions={[condition()]} />);

    expect(screen.getByRole('heading', { name: 'Условия прохождения' })).toBeVisible();
    expect(screen.getByText('Даже не вспотел')).toBeVisible();
    for (const label of outcomeLabels) expect(screen.queryByText(label)).not.toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('treats an explicit null check as not checked yet', () => {
    render(<RunConditions conditions={[condition()]} check={null} />);

    expect(screen.getByText('Даже не вспотел')).toBeVisible();
    for (const label of outcomeLabels) expect(screen.queryByText(label)).not.toBeInTheDocument();
  });

  it.each([
    ['easy', 'Лёгкая'],
    ['normal', 'Нормальная'],
    ['hard', 'Сложная'],
    ['extreme', 'Выше сложной'],
  ] as const)('explains the lower and upper %s bounds in Russian', (difficulty, label) => {
    const { container } = render(
      <RunConditions
        conditions={[condition({ difficultyAtLeast: difficulty, difficultyAtMost: difficulty })]}
      />,
    );

    expect(container).toHaveTextContent(new RegExp(`Не ниже:\\s*${label}`));
    expect(container).toHaveTextContent(new RegExp(`Не выше:\\s*${label}`));
  });

  it('does not fabricate a difficulty bound or event kind when the API fields are null', () => {
    const { container } = render(
      <RunConditions
        conditions={[
          condition({
            name: 'Условие предмета',
            kind: null,
            difficultyAtLeast: null,
            difficultyAtMost: null,
          }),
        ]}
      />,
    );

    expect(screen.getByText('Условие предмета')).toBeVisible();
    expect(container).not.toHaveTextContent(/Не ниже|Не выше|null|undefined/);
    expect(
      screen.queryByText(/Хороший ивент|Плохой ивент|Специвент|Рискованный ивент/),
    ).not.toBeInTheDocument();
  });

  it('keeps several conditions in server order, including two conditions from the same object', () => {
    const conditions: readonly RunCondition[] = [
      condition({ name: 'Я первое условие', difficultyAtLeast: 'normal' }),
      condition({ name: 'А я второе условие', difficultyAtMost: 'hard' }),
    ];
    render(<RunConditions conditions={conditions} />);

    const first = screen.getByText('Я первое условие');
    const second = screen.getByText('А я второе условие');
    expect(first).toBeVisible();
    expect(second).toBeVisible();
    expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it.each([
    [{ met: true, waived: false }, 'Выполнено'],
    [{ met: false, waived: false }, 'Нарушено'],
    [{ met: true, waived: true }, 'Не применимо'],
    [{ met: false, waived: true }, 'Не применимо'],
  ] as const)(
    'shows one aggregate result for %j, outside the individual condition rows',
    (check, label) => {
      render(
        <RunConditions
          conditions={[
            condition(),
            condition({
              objectId: 'calm',
              name: 'Без напряжения',
              difficultyAtLeast: null,
              difficultyAtMost: 'normal',
            }),
          ]}
          check={check}
        />,
      );

      expect(screen.getByText(label, { exact: true })).toBeVisible();
      for (const other of outcomeLabels.filter((text) => text !== label)) {
        expect(screen.queryByText(other, { exact: true })).not.toBeInTheDocument();
      }
      const rows = screen.getAllByRole('listitem');
      expect(rows).toHaveLength(2);
      for (const row of rows) {
        for (const outcome of outcomeLabels) {
          expect(within(row).queryByText(outcome, { exact: true })).not.toBeInTheDocument();
        }
      }
      expect(screen.queryByRole('button')).not.toBeInTheDocument();
    },
  );

  it('updates the aggregate result when an admin rechecks or waives the conditions', () => {
    const conditions = [condition()];
    const { rerender } = render(
      <RunConditions conditions={conditions} check={{ met: false, waived: false }} />,
    );
    expect(screen.getByText('Нарушено')).toBeVisible();

    rerender(<RunConditions conditions={conditions} check={{ met: true, waived: false }} />);

    expect(screen.getByText('Выполнено')).toBeVisible();
    expect(screen.queryByText('Нарушено')).not.toBeInTheDocument();

    rerender(<RunConditions conditions={conditions} check={{ met: true, waived: true }} />);

    expect(screen.getByText('Не применимо')).toBeVisible();
    expect(screen.queryByText('Выполнено')).not.toBeInTheDocument();
    expect(screen.getByText('Даже не вспотел')).toBeVisible();
  });

  it('renders a long markup-looking condition name as ordinary visible text', () => {
    const name =
      'Очень длинное условие следующего прохождения: <img src=x onerror=alert(1)> — пройти без упрощений';
    const { container } = render(<RunConditions conditions={[condition({ name, kind: null })]} />);

    expect(screen.getByText(name)).toBeVisible();
    expect(container.querySelector('img')).toBeNull();
  });
});
