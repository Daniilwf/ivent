import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { GlobalEventBanner } from './GlobalEventBanner';

type GlobalEvent = Schemas['GlobalEventView'];
type Goal = Schemas['GlobalGoalView'];

function goal(overrides: Partial<Goal> = {}): Goal {
  return { metric: 'hours', target: 50, progress: 12.5, reached: false, ...overrides };
}

function event(overrides: Partial<GlobalEvent> = {}): GlobalEvent {
  return {
    id: '8ef821b3-87bb-4e88-ae15-993e1abde0f4',
    objectId: 'team-sprint',
    name: 'Общий спринт',
    description: 'Вместе наиграйте пятьдесят часов.',
    status: 'active',
    startsAt: '2026-11-17T12:00:00Z',
    endsAt: '2026-11-24T12:00:00Z',
    goal: goal(),
    ...overrides,
  };
}

/** Both native progress and ARIA progressbars express the same bounded fraction to assistive technology. */
function progressFraction(bar: HTMLElement): number {
  const current = bar.getAttribute('aria-valuenow') ?? bar.getAttribute('value');
  const minimum = Number(bar.getAttribute('aria-valuemin') ?? '0');
  const maximum = Number(
    bar.getAttribute('aria-valuemax') ??
      bar.getAttribute('max') ??
      (bar instanceof HTMLProgressElement ? String(bar.max) : '100'),
  );
  expect(current, 'A determinate goal must expose its current value').not.toBeNull();
  expect(maximum).toBeGreaterThan(minimum);
  const value = Number(current);
  expect(value).toBeGreaterThanOrEqual(minimum);
  expect(value).toBeLessThanOrEqual(maximum);
  return (value - minimum) / (maximum - minimum);
}

describe('global events on the season screen (EV18/EV19/EV39)', () => {
  it('keeps the name and progress visible and opens the other details on demand (DR7-27)', async () => {
    const running = event();
    const { container, rerender } = render(<GlobalEventBanner compact events={[running]} />);

    const banner = screen.getByRole('region', { name: running.name });
    const name = within(banner).getByRole('heading', { level: 2, name: running.name });
    const summary = name.closest('summary');
    expect(summary).toBeVisible();
    expect(summary).toHaveTextContent(/12,5\s*(?:из|\/)\s*50/);
    const description = screen.getByText(running.description);
    const deadline = container.querySelector(`time[datetime="${running.endsAt}"]`);
    expect(description).not.toBeVisible();
    expect(deadline).not.toBeVisible();

    await userEvent.click(name);
    expect(description).toBeVisible();
    expect(deadline).toBeVisible();
    expect(progressFraction(screen.getByRole('progressbar'))).toBeCloseTo(0.25);

    rerender(
      <GlobalEventBanner compact events={[{ ...running, goal: goal({ progress: 16.25 }) }]} />,
    );
    expect(summary).toHaveTextContent(/16,25\s*(?:из|\/)\s*50/);
    expect(description).toBeVisible();

    await userEvent.click(name);
    expect(description).not.toBeVisible();
  });

  it('keeps a compact goal-less event expandable without inventing progress', async () => {
    const running = event({ goal: null });
    render(<GlobalEventBanner compact events={[running]} />);

    const name = screen.getByText(running.name);
    expect(name.closest('summary')).toHaveTextContent(running.name);
    await userEvent.click(name);
    expect(screen.getByText(running.description)).toBeVisible();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });

  it.each(['omitted', 'null', 'empty'] as const)(
    'renders no empty banner when events are %s',
    (state) => {
      const { container } = render(
        state === 'omitted' ? (
          <GlobalEventBanner />
        ) : (
          <GlobalEventBanner events={state === 'null' ? null : []} />
        ),
      );

      expect(container).toBeEmptyDOMElement();
    },
  );

  it('shows the snapshot name, description and readable end time of an active global event', () => {
    const running = event();
    const { container } = render(<GlobalEventBanner events={[running]} />);

    expect(screen.getByRole('heading', { name: running.name })).toBeVisible();
    expect(screen.getByText(running.description)).toBeVisible();
    const deadline = container.querySelector(`time[datetime="${running.endsAt}"]`);
    expect(deadline).toBeVisible();
    expect(deadline).not.toHaveTextContent(/^\s*$/);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('keeps a real event without a goal and does not invent a progress indicator or a reached result', () => {
    render(<GlobalEventBanner events={[event({ goal: null })]} />);

    expect(screen.getByRole('heading', { name: 'Общий спринт' })).toBeVisible();
    expect(screen.getByText('Вместе наиграйте пятьдесят часов.')).toBeVisible();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
    expect(screen.queryByText('Цель достигнута', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText(/NaN|undefined|null|0\s*из\s*0/)).not.toBeInTheDocument();
  });

  it('shows fractional HLTB hours and exposes the same fraction in an accessible progressbar', () => {
    const { container } = render(<GlobalEventBanner events={[event()]} />);

    expect(container).toHaveTextContent(/12,5\s*(?:из|\/)\s*50/);
    expect(container).toHaveTextContent(/час/);
    const bar = screen.getByRole('progressbar');
    expect(bar).toHaveAccessibleName(/Общий спринт/);
    expect(progressFraction(bar)).toBeCloseTo(0.25);
    expect(screen.queryByText('Цель достигнута', { exact: true })).not.toBeInTheDocument();
  });

  it('distinguishes a goal in completed runs from a goal in hours', () => {
    const { container } = render(
      <GlobalEventBanner
        events={[event({ goal: goal({ metric: 'runs', target: 8, progress: 3 }) })]}
      />,
    );

    expect(container).toHaveTextContent(/3\s*(?:из|\/)\s*8/);
    expect(container).toHaveTextContent(/прохождени/);
    expect(container).not.toHaveTextContent(/3\s*час/);
    expect(progressFraction(screen.getByRole('progressbar'))).toBeCloseTo(3 / 8);
  });

  it('shows a true zero rather than an empty or indeterminate progress state', () => {
    const { container } = render(
      <GlobalEventBanner events={[event({ goal: goal({ progress: 0 }) })]} />,
    );

    expect(container).toHaveTextContent(/0\s*(?:из|\/)\s*50/);
    expect(progressFraction(screen.getByRole('progressbar'))).toBe(0);
    expect(screen.queryByText('Цель достигнута', { exact: true })).not.toBeInTheDocument();
  });

  it('keeps the reached fact when a rejected run later reduces the current contribution sum', () => {
    const { container, rerender } = render(
      <GlobalEventBanner events={[event({ goal: goal({ progress: 50, reached: true }) })]} />,
    );
    expect(screen.getByText('Цель достигнута', { exact: true })).toBeVisible();

    rerender(
      <GlobalEventBanner events={[event({ goal: goal({ progress: 37.5, reached: true }) })]} />,
    );

    expect(screen.getByText('Цель достигнута', { exact: true })).toBeVisible();
    expect(container).toHaveTextContent(/37,5\s*(?:из|\/)\s*50/);
    expect(progressFraction(screen.getByRole('progressbar'))).toBeCloseTo(0.75);
  });

  it('bounds the visual progress at the target while retaining the actual contribution total', () => {
    const { container } = render(
      <GlobalEventBanner events={[event({ goal: goal({ progress: 62.5, reached: true }) })]} />,
    );

    expect(progressFraction(screen.getByRole('progressbar'))).toBe(1);
    expect(container).toHaveTextContent(/62,5\s*(?:из|\/)\s*50/);
    expect(screen.getByText('Цель достигнута', { exact: true })).toBeVisible();
  });

  it('updates a goal from server data without inventing contributions from elapsed time', () => {
    const { container, rerender } = render(<GlobalEventBanner events={[event()]} />);

    rerender(<GlobalEventBanner events={[event({ goal: goal({ progress: 16.25 }) })]} />);

    expect(container).toHaveTextContent(/16,25\s*(?:из|\/)\s*50/);
    expect(progressFraction(screen.getByRole('progressbar'))).toBeCloseTo(16.25 / 50);
    expect(screen.queryByText('Цель достигнута', { exact: true })).not.toBeInTheDocument();
  });

  it('preserves the order of several events and gives their goals distinct accessible names', () => {
    render(
      <GlobalEventBanner
        events={[
          event({ name: 'Сначала старый' }),
          event({ id: '63c69a56-d038-4a8a-9fb2-599ba7842f5a', name: 'Затем новый' }),
        ]}
      />,
    );

    const first = screen.getByRole('heading', { name: 'Сначала старый' });
    const second = screen.getByRole('heading', { name: 'Затем новый' });
    expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    const bars = screen.getAllByRole('progressbar');
    expect(bars).toHaveLength(2);
    expect(bars[0]).toHaveAccessibleName(/Сначала старый/);
    expect(bars[1]).toHaveAccessibleName(/Затем новый/);
  });

  it('shows long imported names and descriptions as plain text', () => {
    const name = 'Очень длинный общий ивент <img src=x onerror=alert(1)> для всех друзей';
    const description = 'Сыграем вместе: <script>это текст, а не исполняемый код</script>';
    const { container } = render(<GlobalEventBanner events={[event({ name, description })]} />);

    expect(screen.getByRole('heading', { name })).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(container.querySelector('img, script')).toBeNull();
  });

  it('removes only the banner whose global event ended on a refresh', () => {
    const second = event({ id: '63c69a56-d038-4a8a-9fb2-599ba7842f5a', name: 'Другой ивент' });
    const { rerender } = render(<GlobalEventBanner events={[event(), second]} />);

    rerender(<GlobalEventBanner events={[second]} />);

    expect(screen.queryByRole('heading', { name: 'Общий спринт' })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Другой ивент' })).toBeVisible();
    expect(screen.getAllByRole('progressbar')).toHaveLength(1);
    rerender(<GlobalEventBanner events={[]} />);
    expect(screen.queryByRole('heading', { name: 'Другой ивент' })).not.toBeInTheDocument();
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument();
  });
});
