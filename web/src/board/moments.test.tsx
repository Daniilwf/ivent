import { render, screen, waitFor } from '@testing-library/react';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ru } from '../i18n/ru';
import { demoBoard } from './demoBoard';
import { DiceMoment } from './Dice';
import { walk } from './geometry';
import { TokenMove } from './TokenMove';
import type { MomentHandle } from './moment';
import { WheelMoment } from './Wheel';

// The viewer asks for reduced motion: every moment shows its result at once, in words too (docs/DESIGN.md «Движение»)
vi.hoisted(() => {
  // jsdom has no ResizeObserver; the map only needs it to exist
  Object.defineProperty(window, 'ResizeObserver', {
    configurable: true,
    value: class {
      observe() {}
      disconnect() {}
      unobserve() {}
    },
  });
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: (query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      onchange: null,
      dispatchEvent: () => false,
    }),
  });
});

describe('the main moments with reduced motion', () => {
  it('the dice show the total at once and say every die, the challenge one apart', async () => {
    const phase = vi.fn();
    render(<DiceMoment roll={{ id: 1, values: [2, 5, 3, 6], challenge: 1 }} onPhase={phase} />);
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    expect(screen.getByText('+16')).toBeInTheDocument();
    expect(screen.getByText(ru.moments.dice.result([2, 5, 3], [6], 16))).toBeInTheDocument();
  });

  it('a die of more than six sides shows its number, a die of six its pips', async () => {
    const phase = vi.fn();
    const { container } = render(
      <DiceMoment roll={{ id: 1, values: [8, 3], challenge: 0 }} onPhase={phase} />,
    );
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    expect(container.querySelector('[data-die="8"]')).toHaveTextContent('8');
    expect(container.querySelector('[data-die="3"]')).not.toHaveTextContent('3');
  });

  it('draws a d4 with its number and says its sides', async () => {
    const phase = vi.fn();
    const { container } = render(
      <DiceMoment roll={{ id: 1, values: [3], challenge: 0, sides: [4] }} onPhase={phase} />,
    );
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    const die = container.querySelector('[data-die="3"]');
    expect(die).toHaveTextContent('3');
    expect(die).toHaveTextContent(ru.moments.dice.sides(4));
  });

  it('says free mode and shows no total when the dice give no points', async () => {
    render(<DiceMoment roll={{ id: 1, values: [2, 3], challenge: 0 }} mode="free" />);
    expect(await screen.findByText(ru.moments.dice.resultFree([2, 3], []))).toBeInTheDocument();
    expect(screen.queryByText('+5')).not.toBeInTheDocument();
  });

  it('says only points when the token stays', async () => {
    render(<DiceMoment roll={{ id: 1, values: [2, 3], challenge: 0 }} mode="stay" />);
    expect(await screen.findByText(ru.moments.dice.resultStay([2, 3], [], 5))).toBeInTheDocument();
  });

  it('the wheel stands on the pick at once and still tells the miss and who completed it', async () => {
    const phase = vi.fn();
    render(
      <WheelMoment
        sectors={['Action', 'Horror', 'RPG']}
        roll={{
          id: 1,
          misses: [{ sector: 1, game: { title: 'Hollow Knight' }, by: 'Сова' }],
          pick: { sector: 2, game: { title: 'Dead Cells' } },
        }}
        onPhase={phase}
      />,
    );
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    const said = screen.getByText(new RegExp(ru.moments.wheel.result('Dead Cells')));
    // The wheel stands: the miss is told as done
    expect(said).toHaveTextContent(ru.moments.wheel.missedCompleted('Hollow Knight', 'Сова', null));
    expect(said).toHaveTextContent(ru.moments.wheel.category('RPG'));
  });

  it('a skipped moment ends once', async () => {
    const phase = vi.fn();
    const handle = createRef<MomentHandle>();
    render(
      <DiceMoment ref={handle} roll={{ id: 1, values: [1, 1], challenge: 0 }} onPhase={phase} />,
    );
    handle.current?.skip();
    handle.current?.skip();
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    expect(phase.mock.calls.filter(([p]) => p === 'done')).toHaveLength(1);
  });

  it('a skipped wheel ends once', async () => {
    const phase = vi.fn();
    const handle = createRef<MomentHandle>();
    render(
      <WheelMoment
        ref={handle}
        sectors={['Action', 'RPG']}
        roll={{ id: 1, misses: [], pick: { sector: 1, game: { title: 'Dead Cells' } } }}
        onPhase={phase}
      />,
    );
    handle.current?.skip();
    handle.current?.skip();
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(phase.mock.calls.filter(([p]) => p === 'done')).toHaveLength(1);
  });

  it('a skipped move ends once', async () => {
    const phase = vi.fn();
    const handle = createRef<MomentHandle>();
    const mover = { id: 'me', name: 'Лиса', token: 2, cell: 1, points: 0, me: true };
    const path = walk(demoBoard, 1, 5);
    render(
      <TokenMove
        ref={handle}
        board={demoBoard}
        players={[mover]}
        mover={mover}
        path={path}
        onPhase={phase}
      />,
    );
    handle.current?.skip();
    handle.current?.skip();
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(phase.mock.calls.filter(([p]) => p === 'done')).toHaveLength(1);
  });
});
